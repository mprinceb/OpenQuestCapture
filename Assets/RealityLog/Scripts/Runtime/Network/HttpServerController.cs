# nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using RealityLog.Common;
using RealityLog.Core;
using RealityLog.FileOperations;
using RealityLog.UI;

namespace RealityLog.Network
{
    /// <summary>
    /// MonoBehaviour that starts the embedded HTTP server and routes requests
    /// to RecordingManager, RecordingListManager, and RecordingOperations.
    /// Implements the Quest HTTP API contract for FieldData Pro phone control.
    /// Auto-bootstraps at runtime — no need to manually add to the scene.
    /// </summary>
    public class HttpServerController : MonoBehaviour
    {
        [Header("Server Settings")]
        [SerializeField] private int port = 8080;

        [Header("Dependencies")]
        [SerializeField] private RecordingManager? recordingManager;
        [SerializeField] private RecordingListManager? recordingListManager;
        [SerializeField] private RecordingOperations? recordingOperations;



        [Header("Storage")]
        [Tooltip("Minimum free bytes required to start recording (500 MB)")]
        [SerializeField] private long minFreeStorageBytes = 524_288_000;

        private EmbeddedHttpServer? server;
        private float appStartTime;
        private long appStartUnixMs; // For background-thread-safe uptime calc
        private string cachedDataPath = ""; // Cache for background thread access

        // State tracked across start/stop for response metadata
        private string? currentRecordingFile;
        private string? currentRecordingStartedAt;
        private long currentRecordingStartMs;

        // Sidecar metadata for recordings (sessionId, employeeId, etc.)
        private readonly Dictionary<string, string> recordingMetadata = new();

        private static readonly Regex RecordingDirPattern = new(@"^\d{8}_\d{6}$", RegexOptions.Compiled);

        // Cached status values (refreshed every Update, read lock-free by HTTP thread)
        private volatile int cachedBattery = 100;
        private long cachedStorageFree = long.MaxValue;
        private long cachedStorageTotal = 0;
        private volatile bool cachedIsRecording = false;
        private float cachedDuration = 0f;
        private string cachedAppVersion = "1.2.0";
        private float lastStorageRefresh = 0f;
        private float lastHeartbeat = 0f;

        // Keep-awake: wake lock handle to prevent Quest from sleeping when headset is removed
#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject? wakeLock;
#endif

        private void Awake()
        {
            appStartTime = Time.realtimeSinceStartup;

            // Auto-discover dependencies if not assigned
            recordingManager ??= FindFirstObjectByType<RecordingManager>();
            recordingListManager ??= FindFirstObjectByType<RecordingListManager>();
            recordingOperations ??= FindFirstObjectByType<RecordingOperations>();

            if (recordingManager == null)
                Debug.LogError($"[{Constants.LOG_TAG}] HttpServerController: RecordingManager not found!");
        }

        private void Start()
        {
            // Start foreground service FIRST — this is the critical fix that prevents
            // Android from killing the app when it goes to background. Must happen
            // before any other initialization so the process is protected immediately.
            ForegroundServiceManager.StartService();

            cachedDataPath = Application.persistentDataPath; // Cache on main thread
            appStartUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            server = new EmbeddedHttpServer(port);
            RegisterRoutes();
            server.Start();

            // Auto-apply keep-awake so Quest never sleeps when headset is removed
            ApplyKeepAwakeSettings();
            Debug.Log($"[{Constants.LOG_TAG}] KeepAwake: Auto-applied on startup");
        }

        private void Update()
        {
            server?.ProcessMainThreadQueue();

            // Refresh cached values on main thread (safe for JNI)
            cachedIsRecording = recordingManager != null && recordingManager.IsRecording;
            cachedDuration = recordingManager != null ? recordingManager.RecordingDuration : 0f;
            cachedBattery = GetBatteryPercent();

            // Refresh storage every 30 seconds (expensive JNI call)
            if (Time.realtimeSinceStartup - lastStorageRefresh > 30f)
            {
                cachedStorageFree = GetStorageFreeBytes();
                cachedStorageTotal = GetStorageTotalBytes();
                cachedAppVersion = Application.version;
                lastStorageRefresh = Time.realtimeSinceStartup;
            }

            // Heartbeat log every 60 seconds — confirms server is alive and shows IP info
            if (Time.realtimeSinceStartup - lastHeartbeat > 60f)
            {
                lastHeartbeat = Time.realtimeSinceStartup;
                try
                {
                    var ips = new List<string>();
                    var hostName = System.Net.Dns.GetHostName();
                    foreach (var addr in System.Net.Dns.GetHostAddresses(hostName))
                    {
                        if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            ips.Add(addr.ToString());
                    }
                    Debug.Log($"[{Constants.LOG_TAG}] HEARTBEAT: Server alive on port {port} | IPs: {string.Join(", ", ips)} | Battery: {cachedBattery}% | Recording: {cachedIsRecording}");
                }
                catch (Exception ex)
                {
                    Debug.Log($"[{Constants.LOG_TAG}] HEARTBEAT: Server alive on port {port} | IP lookup failed: {ex.Message}");
                }
            }
        }

        private void OnDestroy()
        {
            server?.Stop();

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                if (wakeLock != null)
                {
                    wakeLock.Call("release");
                    wakeLock.Dispose();
                    wakeLock = null;
                    Debug.Log($"[{Constants.LOG_TAG}] KeepAwake: Wake lock released");
                }
            }
            catch { }
#endif
        }

        private void OnApplicationPause(bool paused)
        {
            // Keep server alive through pause/resume — it's low-overhead when idle
        }

        // ── Route registration ──

        private void RegisterRoutes()
        {
            if (server == null) return;

            server.Get("/api/status", HandleStatus);
            server.Post("/api/start-recording", HandleStartRecording);
            server.Post("/api/stop-recording", HandleStopRecording);
            server.Get("/api/recordings", HandleListRecordings);
            server.WildcardGet("/api/recordings/", HandleStreamRecording);
            server.WildcardDelete("/api/recordings/", HandleDeleteRecording);
            server.Post("/api/mark-episode", HandleMarkEpisode);
            server.Post("/api/keep-awake", HandleKeepAwake);
            server.Get("/api/diagnostics", HandleDiagnostics);
            server.Post("/api/timesync/ping", HandleTimesyncPing);
        }

        // ── GET /api/status ──

        private HttpResponse HandleStatus(HttpRequest request)
        {
            // Read cached values — no main thread dispatch needed, no blocking
            bool isRecording = cachedIsRecording;
            string? currentFile = isRecording ? currentRecordingFile : null;
            int durationMs = (int)(cachedDuration * 1000);
            long uptimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - appStartUnixMs;

            long fileSizeBytes = 0;
            if (isRecording && currentFile != null)
            {
                var videoPath = FindVideoFile(currentFile);
                if (videoPath != null && File.Exists(videoPath))
                {
                    try { fileSizeBytes = new FileInfo(videoPath).Length; } catch { }
                }
            }

            var json = "{\n" +
                $"  \"device\": \"MetaQuest3\",\n" +
                $"  \"batteryPercent\": {cachedBattery},\n" +
                $"  \"storageFreeBytes\": {cachedStorageFree},\n" +
                $"  \"storageTotalBytes\": {cachedStorageTotal},\n" +
                $"  \"recording\": {{\n" +
                $"    \"active\": {(isRecording ? "true" : "false")},\n" +
                $"    \"currentFile\": {(currentFile != null ? $"\"{EscapeJson(currentFile)}\"" : "null")},\n" +
                $"    \"durationMs\": {durationMs},\n" +
                $"    \"fileSizeBytes\": {fileSizeBytes}\n" +
                $"  }},\n" +
                $"  \"apkVersion\": \"{EscapeJson(cachedAppVersion)}\",\n" +
                $"  \"uptimeMs\": {uptimeMs}\n" +
                "}";

            return HttpResponse.Ok(json);
        }

        // ── POST /api/start-recording ──

        private HttpResponse HandleStartRecording(HttpRequest request)
        {
            bool alreadyRecording = false;
            bool success = false;
            bool storageFull = false;
            long freeBytes = 0;
            string? errorMessage = null;

            var signal = server!.EnqueueOnMainThread(() =>
            {
                try
                {
                    // Storage check on main thread (JNI-safe)
                    freeBytes = GetStorageFreeBytes();
                    if (freeBytes > 0 && freeBytes < minFreeStorageBytes)
                    {
                        storageFull = true;
                        return;
                    }

                    if (recordingManager == null)
                    {
                        errorMessage = "RecordingManager not available";
                        return;
                    }

                    if (recordingManager.IsRecording)
                    {
                        alreadyRecording = true;
                        return;
                    }

                    recordingManager.StartRecording();

                    // Capture metadata
                    currentRecordingFile = recordingManager.CurrentSessionDirectory;
                    currentRecordingStartMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    currentRecordingStartedAt = DateTimeOffset.UtcNow.ToString("O");

                    // Save sidecar metadata from request body
                    if (!string.IsNullOrEmpty(request.Body) && currentRecordingFile != null)
                    {
                        SaveSidecarMetadata(currentRecordingFile, request.Body);
                    }

                    success = true;
                }
                catch (Exception ex)
                {
                    errorMessage = ex.Message;
                    Debug.LogError($"[{Constants.LOG_TAG}] HttpServerController: StartRecording error: {ex}");
                }
            });
            signal.Wait(5000);

            if (storageFull)
            {
                return HttpResponse.StorageFull(freeBytes, minFreeStorageBytes);
            }

            if (alreadyRecording)
            {
                return HttpResponse.Json(409,
                    "{\n" +
                    $"  \"error\": \"already_recording\",\n" +
                    $"  \"message\": \"Recording is already in progress\",\n" +
                    $"  \"currentFile\": \"{EscapeJson(currentRecordingFile ?? "unknown")}\"\n" +
                    "}");
            }

            if (!success)
            {
                return HttpResponse.InternalError(errorMessage ?? "Failed to start recording");
            }

            var json = "{\n" +
                $"  \"status\": \"recording\",\n" +
                $"  \"file\": \"{EscapeJson(currentRecordingFile ?? "")}\",\n" +
                $"  \"startedAt\": \"{EscapeJson(currentRecordingStartedAt ?? "")}\"\n" +
                "}";

            return HttpResponse.Ok(json);
        }

        // ── POST /api/stop-recording ──

        private HttpResponse HandleStopRecording(HttpRequest request)
        {
            bool notRecording = false;
            bool success = false;
            string? errorMessage = null;
            string? stoppedFile = null;
            long durationMs = 0;
            string? startedAt = null;

            var signal = server!.EnqueueOnMainThread(() =>
            {
                try
                {
                    if (recordingManager == null)
                    {
                        errorMessage = "RecordingManager not available";
                        return;
                    }

                    if (!recordingManager.IsRecording)
                    {
                        notRecording = true;
                        return;
                    }

                    durationMs = (long)(recordingManager.RecordingDuration * 1000);
                    stoppedFile = currentRecordingFile;
                    startedAt = currentRecordingStartedAt;

                    recordingManager.StopRecording();

                    // Clear recording state
                    currentRecordingFile = null;
                    currentRecordingStartedAt = null;
                    currentRecordingStartMs = 0;

                    success = true;
                }
                catch (Exception ex)
                {
                    errorMessage = ex.Message;
                    Debug.LogError($"[{Constants.LOG_TAG}] HttpServerController: StopRecording error: {ex}");
                }
            });
            signal.Wait(5000);

            if (notRecording)
            {
                return HttpResponse.Conflict("not_recording", "No recording in progress");
            }

            if (!success)
            {
                return HttpResponse.InternalError(errorMessage ?? "Failed to stop recording");
            }

            // Get file size
            long fileSizeBytes = 0;
            if (stoppedFile != null)
            {
                var videoPath = FindVideoFile(stoppedFile);
                if (videoPath != null)
                {
                    // Wait briefly for finalization
                    for (int i = 0; i < 20; i++)
                    {
                        try
                        {
                            if (File.Exists(videoPath))
                            {
                                var size = new FileInfo(videoPath).Length;
                                if (size > 0) { fileSizeBytes = size; break; }
                            }
                        }
                        catch { }
                        Thread.Sleep(100);
                    }
                }
            }

            var stoppedAt = DateTimeOffset.UtcNow.ToString("O");
            var json = "{\n" +
                $"  \"status\": \"stopped\",\n" +
                $"  \"file\": \"{EscapeJson(stoppedFile ?? "")}\",\n" +
                $"  \"durationMs\": {durationMs},\n" +
                $"  \"fileSizeBytes\": {fileSizeBytes},\n" +
                $"  \"startedAt\": \"{EscapeJson(startedAt ?? "")}\",\n" +
                $"  \"stoppedAt\": \"{EscapeJson(stoppedAt)}\"\n" +
                "}";

            return HttpResponse.Ok(json);
        }

        // ── GET /api/recordings ──

        private HttpResponse HandleListRecordings(HttpRequest request)
        {
            int limit = 20;
            if (request.QueryParams.TryGetValue("limit", out var limitStr) && int.TryParse(limitStr, out var l))
                limit = Math.Max(1, Math.Min(l, 100));

            string? afterFilter = null;
            if (request.QueryParams.TryGetValue("after", out var after) && !string.IsNullOrEmpty(after))
                afterFilter = after;

            var recordings = ScanRecordings(limit, afterFilter);

            var items = new List<string>();
            foreach (var rec in recordings)
            {
                var metaJson = "null";
                if (recordingMetadata.TryGetValue(rec.DirectoryName, out var meta))
                    metaJson = meta;
                else
                {
                    var sidecarMeta = LoadSidecarMetadata(rec.DirectoryName);
                    if (sidecarMeta != null) metaJson = sidecarMeta;
                }

                items.Add(
                    "    {\n" +
                    $"      \"file\": \"{EscapeJson(rec.DirectoryName)}\",\n" +
                    $"      \"durationMs\": {(int)(rec.DurationSeconds * 1000)},\n" +
                    $"      \"fileSizeBytes\": {rec.SizeBytes},\n" +
                    $"      \"createdAt\": \"{rec.CreationTime:O}\",\n" +
                    $"      \"metadata\": {metaJson}\n" +
                    "    }");
            }

            int total = CountAllRecordings();

            var json = "{\n" +
                $"  \"recordings\": [\n{string.Join(",\n", items)}\n  ],\n" +
                $"  \"total\": {total}\n" +
                "}";

            return HttpResponse.Ok(json);
        }

        // ── GET /api/recordings/:filename ──

        private HttpResponse HandleStreamRecording(HttpRequest request)
        {
            var filename = request.PathParam;
            var videoPath = FindVideoFile(filename);

            if (videoPath == null || !File.Exists(videoPath))
            {
                return HttpResponse.NotFound($"Recording not found: {filename}");
            }

            return HttpResponse.File(videoPath, "video/mp4");
        }

        // ── DELETE /api/recordings/:filename ──

        private HttpResponse HandleDeleteRecording(HttpRequest request)
        {
            var filename = request.PathParam;

            // Check if this recording is currently being recorded
            bool isCurrentRecording = false;
            var signal = server!.EnqueueOnMainThread(() =>
            {
                if (recordingManager != null && recordingManager.IsRecording
                    && currentRecordingFile == filename)
                {
                    isCurrentRecording = true;
                }
            });
            signal.Wait(3000);

            if (isCurrentRecording)
            {
                return HttpResponse.Conflict("recording_in_progress", "Cannot delete file that is currently being recorded");
            }

            var dirPath = Path.Combine(Application.persistentDataPath, filename);
            if (!Directory.Exists(dirPath))
            {
                return HttpResponse.NotFound("Recording not found");
            }

            long freedBytes = 0;
            try
            {
                // Calculate size before deletion
                var files = Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories);
                foreach (var f in files)
                {
                    try { freedBytes += new FileInfo(f).Length; } catch { }
                }

                Directory.Delete(dirPath, true);
                recordingMetadata.Remove(filename);

                Debug.Log($"[{Constants.LOG_TAG}] HttpServerController: Deleted recording {filename}");
            }
            catch (Exception ex)
            {
                return HttpResponse.InternalError($"Failed to delete: {ex.Message}");
            }

            var json = "{\n" +
                $"  \"deleted\": \"{EscapeJson(filename)}\",\n" +
                $"  \"freedBytes\": {freedBytes}\n" +
                "}";

            return HttpResponse.Ok(json);
        }

        // ── POST /api/mark-episode ──
        // Called by the phone app when the operator presses the Mark button.
        // Shows a visual flash inside the VR headset so the wearer knows a new episode started.

        private HttpResponse HandleMarkEpisode(HttpRequest request)
        {
            int episodeNumber = 0;
            bool success = false;
            string? errorMessage = null;

            // Parse episode number from request body
            if (!string.IsNullOrEmpty(request.Body))
            {
                try
                {
                    var payload = JsonUtility.FromJson<MarkEpisodePayload>(request.Body);
                    episodeNumber = payload.episodeNumber;
                }
                catch { }
            }

            if (episodeNumber <= 0) episodeNumber = 1;

            var signal = server!.EnqueueOnMainThread(() =>
            {
                try
                {
                    var marker = FindFirstObjectByType<EpisodeMarkerController>();
                    if (marker != null)
                    {
                        marker.MarkFromPhone(episodeNumber);
                        success = true;
                    }
                    else
                    {
                        errorMessage = "EpisodeMarkerController not found";
                    }
                }
                catch (Exception ex)
                {
                    errorMessage = ex.Message;
                    Debug.LogError($"[{Constants.LOG_TAG}] HttpServerController: MarkEpisode error: {ex}");
                }
            });
            signal.Wait(3000);

            if (!success)
            {
                return HttpResponse.InternalError(errorMessage ?? "Failed to mark episode");
            }

            return HttpResponse.Ok($"{{\"status\": \"marked\", \"episodeNumber\": {episodeNumber}}}");
        }

        [Serializable]
        private class MarkEpisodePayload
        {
            public int episodeNumber;
        }

        // ── POST /api/keep-awake ──
        // Called by the phone app on every connection to ensure Quest stays awake
        // even when the headset is removed (proximity sensor disabled).

        private HttpResponse HandleKeepAwake(HttpRequest request)
        {
            bool success = false;
            string errorMessage = "";
            var applied = new List<string>();

            var signal = server!.EnqueueOnMainThread(() =>
            {
                try
                {
                    ApplyKeepAwakeSettings(applied);
                    success = true;
                }
                catch (Exception ex)
                {
                    errorMessage = ex.Message;
                    Debug.LogError($"[{Constants.LOG_TAG}] HttpServerController: KeepAwake error: {ex}");
                }
            });
            signal.Wait(5000);

            if (!success)
            {
                return HttpResponse.InternalError(errorMessage);
            }

            var appliedJson = string.Join(", ", applied.Select(a => $"\"{EscapeJson(a)}\""));
            var json = "{\n" +
                $"  \"status\": \"applied\",\n" +
                $"  \"settings\": [{appliedJson}]\n" +
                "}";

            return HttpResponse.Ok(json);
        }

        /// <summary>
        /// Apply Android system settings to prevent the Quest from sleeping
        /// when the headset is removed. Safe to call multiple times (idempotent).
        /// </summary>
        private void ApplyKeepAwakeSettings(List<string>? log = null)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // 1. Acquire partial wake lock — keeps CPU alive even when display off
            try
            {
                if (wakeLock == null)
                {
                    using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                    using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                    using var powerManager = activity.Call<AndroidJavaObject>("getSystemService", "power");
                    wakeLock = powerManager.Call<AndroidJavaObject>("newWakeLock", 1 /* PARTIAL_WAKE_LOCK */, "RealityLog:KeepAwake");
                    wakeLock.Call("acquire");
                    log?.Add("wake_lock_acquired");
                    Debug.Log($"[{Constants.LOG_TAG}] KeepAwake: Wake lock acquired");
                }
                else
                {
                    log?.Add("wake_lock_already_held");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] KeepAwake: Wake lock failed: {ex.Message}");
            }

            // 2. Keep screen on via Window flag (FLAG_KEEP_SCREEN_ON = 128)
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using var window = activity.Call<AndroidJavaObject>("getWindow");
                window.Call("addFlags", 128);
                log?.Add("flag_keep_screen_on");
                Debug.Log($"[{Constants.LOG_TAG}] KeepAwake: FLAG_KEEP_SCREEN_ON set");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] KeepAwake: Window flag failed: {ex.Message}");
            }

            // 3. Disable proximity sensor via system property + set power settings
            try
            {
                using var processClass = new AndroidJavaClass("java.lang.Runtime");
                using var runtime = processClass.CallStatic<AndroidJavaObject>("getRuntime");

                // Disable proximity-triggered sleep — THE key fix
                using var p1 = runtime.Call<AndroidJavaObject>("exec", new string[] { "/system/bin/setprop", "debug.oculus.proximityDisabled", "1" });
                int p1Exit = p1.Call<int>("waitFor");
                if (p1Exit == 0)
                {
                    log?.Add("proximity_sensor_disabled");
                    Debug.Log($"[{Constants.LOG_TAG}] KeepAwake: Proximity sensor disabled");
                }
                else
                {
                    log?.Add("proximity_sensor_disable_failed");
                    Debug.LogWarning($"[{Constants.LOG_TAG}] KeepAwake: setprop proximityDisabled returned exit code {p1Exit} — proximity sensor may still be active. Recording may stop if headset is jostled.");
                }

                // Set screen timeout to max
                using var p2 = runtime.Call<AndroidJavaObject>("exec", new string[] { "/system/bin/settings", "put", "system", "screen_off_timeout", "2147483647" });
                p2.Call<int>("waitFor");
                log?.Add("screen_timeout_max");

                // Stay on while plugged in (all sources: AC=1 + USB=2 + Wireless=4 = 7)
                using var p3 = runtime.Call<AndroidJavaObject>("exec", new string[] { "/system/bin/settings", "put", "global", "stay_on_while_plugged_in", "7" });
                p3.Call<int>("waitFor");
                log?.Add("stay_on_while_plugged");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] KeepAwake: System settings failed: {ex.Message}");
            }
#else
            log?.Add("editor_mock_applied");
#endif
        }

        // ── GET /api/diagnostics ──
        // Returns all network interfaces, server state, and connection info for debugging

        private HttpResponse HandleDiagnostics(HttpRequest request)
        {
            var interfaces = new List<string>();
            try
            {
                var hostName = System.Net.Dns.GetHostName();
                var addresses = System.Net.Dns.GetHostAddresses(hostName);
                foreach (var addr in addresses)
                {
                    if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        interfaces.Add($"\"{addr}\"");
                    }
                }
            }
            catch { }

            long uptimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - appStartUnixMs;

            var json = "{\n" +
                $"  \"device\": \"MetaQuest3\",\n" +
                $"  \"hostname\": \"{EscapeJson(System.Net.Dns.GetHostName())}\",\n" +
                $"  \"serverPort\": {port},\n" +
                $"  \"ipAddresses\": [{string.Join(", ", interfaces)}],\n" +
                $"  \"uptimeMs\": {uptimeMs},\n" +
                $"  \"batteryPercent\": {cachedBattery},\n" +
                $"  \"isRecording\": {(cachedIsRecording ? "true" : "false")},\n" +
                $"  \"apkVersion\": \"{EscapeJson(cachedAppVersion)}\",\n" +
                $"  \"timestamp\": \"{DateTimeOffset.UtcNow:O}\"\n" +
                "}";

            Debug.Log($"[{Constants.LOG_TAG}] Diagnostics requested — IPs: {string.Join(", ", interfaces)}");
            return HttpResponse.Ok(json);
        }

        // ── POST /api/timesync/ping ──
        // Cristian's algorithm endpoint. Pi 5 orchestrator sends host monotonic at request
        // time; Quest replies with its boot-relative monotonic at request entry/exit plus
        // wall-clock at midpoint. Handled on the HTTP thread (no main-thread dispatch) so the
        // sample is not biased by Unity's frame cadence.

        private HttpResponse HandleTimesyncPing(HttpRequest request)
        {
            long recvMonoNs = MonotonicNanos();

            long pingId = 0;
            long hostSendUtcNs = 0;
            if (!string.IsNullOrEmpty(request.Body))
            {
                try
                {
                    var payload = JsonUtility.FromJson<TimesyncPingPayload>(request.Body);
                    pingId = payload.ping_id;
                    hostSendUtcNs = payload.host_send_utc_ns;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] HttpServerController: " +
                        $"TimesyncPing: failed to parse body: {ex.Message}");
                }
            }

            long midUtcNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
            long sendMonoNs = MonotonicNanos();

            var json = "{\n" +
                $"  \"ping_id\": {pingId},\n" +
                $"  \"quest_recv_mono_ns\": {recvMonoNs},\n" +
                $"  \"quest_send_mono_ns\": {sendMonoNs},\n" +
                $"  \"quest_utc_ns\": {midUtcNs},\n" +
                $"  \"host_send_utc_ns_echo\": {hostSendUtcNs}\n" +
                "}";
            return HttpResponse.Ok(json);
        }

        [Serializable]
        private class TimesyncPingPayload
        {
            public long ping_id;
            public long host_send_utc_ns;
        }

        // Monotonic nanosecond counter, safe to call from the HTTP worker thread.
        //
        // Earlier attempts used `android.os.SystemClock.elapsedRealtimeNanos` via Unity's
        // `AndroidJavaClass` wrapper. Even with `AndroidJNI.AttachCurrentThread()`, the wrapper
        // silently returned 0 on threadpool threads on Quest 3 (Horizon OS 79). The wrapper
        // calls into UnityEngine internals that aren't safe off-main-thread.
        //
        // The reliable path is a `Stopwatch` started once and thread-safe to read. It now lives
        // in RealityLog.Common.MonotonicClock so the recording loggers stamp every row with the
        // SAME counter this timesync endpoint measures — a host maps each sample to its own clock
        // via host_time = mono_time_ns + measured_offset.
        private static long MonotonicNanos() => MonotonicClock.Nanos();

        // ── Helpers ──

        private string? FindVideoFile(string dirName)
        {
            var dirPath = Path.Combine(Application.persistentDataPath, dirName);
            if (!Directory.Exists(dirPath)) return null;

            var videoPath = Path.Combine(dirPath, "center_camera.mp4");
            return File.Exists(videoPath) ? videoPath : null;
        }

        private List<RecordingEntry> ScanRecordings(int limit, string? afterIso)
        {
            var results = new List<RecordingEntry>();
            var dataPath = Application.persistentDataPath;

            if (!Directory.Exists(dataPath)) return results;

            DateTimeOffset? afterTime = null;
            if (afterIso != null && DateTimeOffset.TryParse(afterIso, out var parsed))
                afterTime = parsed;

            var dirs = Directory.GetDirectories(dataPath)
                .Select(d => Path.GetFileName(d))
                .Where(d => RecordingDirPattern.IsMatch(d))
                .OrderByDescending(d => d)
                .ToList();

            foreach (var dirName in dirs)
            {
                if (results.Count >= limit) break;

                var fullPath = Path.Combine(dataPath, dirName);
                var creationTime = Directory.GetCreationTime(fullPath);

                if (afterTime.HasValue && creationTime <= afterTime.Value.LocalDateTime)
                    continue;

                long sizeBytes = 0;
                double durationSec = -1;

                try
                {
                    var files = Directory.GetFiles(fullPath, "*", SearchOption.AllDirectories);
                    foreach (var f in files)
                    {
                        try { sizeBytes += new FileInfo(f).Length; } catch { }
                    }

                    // Parse duration from video_metadata.json
                    var metadataPath = Path.Combine(fullPath, "video_metadata.json");
                    if (File.Exists(metadataPath))
                    {
                        var json = File.ReadAllText(metadataPath);
                        // Simple parse — avoid dependency on JsonUtility from background thread
                        var startMatch = Regex.Match(json, "\"recording_start_unix_ms\"\\s*:\\s*(\\d+)");
                        var stopMatch = Regex.Match(json, "\"recording_stop_unix_ms\"\\s*:\\s*(\\d+)");
                        if (startMatch.Success && stopMatch.Success)
                        {
                            long startMs = long.Parse(startMatch.Groups[1].Value);
                            long stopMs = long.Parse(stopMatch.Groups[1].Value);
                            if (stopMs > startMs)
                                durationSec = (stopMs - startMs) / 1000.0;
                        }
                    }
                }
                catch { }

                results.Add(new RecordingEntry
                {
                    DirectoryName = dirName,
                    SizeBytes = sizeBytes,
                    DurationSeconds = durationSec >= 0 ? durationSec : 0,
                    CreationTime = creationTime
                });
            }

            return results;
        }

        private int CountAllRecordings()
        {
            try
            {
                return Directory.GetDirectories(Application.persistentDataPath)
                    .Count(d => RecordingDirPattern.IsMatch(Path.GetFileName(d)));
            }
            catch { return 0; }
        }

        private void SaveSidecarMetadata(string dirName, string requestBody)
        {
            try
            {
                recordingMetadata[dirName] = requestBody;

                // Also write to disk as sidecar JSON
                var dirPath = Path.Combine(Application.persistentDataPath, dirName);
                Directory.CreateDirectory(dirPath);
                var metaPath = Path.Combine(dirPath, "fielddata_metadata.json");
                File.WriteAllText(metaPath, requestBody);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] HttpServerController: Failed to save sidecar metadata: {ex.Message}");
            }
        }

        private string? LoadSidecarMetadata(string dirName)
        {
            try
            {
                var metaPath = Path.Combine(Application.persistentDataPath, dirName, "fielddata_metadata.json");
                if (File.Exists(metaPath))
                    return File.ReadAllText(metaPath);
            }
            catch { }
            return null;
        }

        // ── Platform helpers ──

        private static int GetBatteryPercent()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                return (int)(SystemInfo.batteryLevel * 100);
            }
            catch { }
#endif
            return (int)(SystemInfo.batteryLevel >= 0 ? SystemInfo.batteryLevel * 100 : 100);
        }

        private long GetStorageFreeBytes()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Try multiple paths — persistentDataPath may fail from background thread
            string[] paths = { cachedDataPath, "/storage/emulated/0", "/data" };
            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                try
                {
                    using var statFs = new AndroidJavaObject("android.os.StatFs", path);
                    long blockSize = statFs.Call<long>("getBlockSizeLong");
                    long availBlocks = statFs.Call<long>("getAvailableBlocksLong");
                    long result = blockSize * availBlocks;
                    if (result > 0) return result;
                }
                catch { }
            }
#endif
            return long.MaxValue; // If we can't determine, assume plenty of space
        }

        private long GetStorageTotalBytes()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            string[] paths = { cachedDataPath, "/storage/emulated/0", "/data" };
            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                try
                {
                    using var statFs = new AndroidJavaObject("android.os.StatFs", path);
                    long blockSize = statFs.Call<long>("getBlockSizeLong");
                    long totalBlocks = statFs.Call<long>("getBlockCountLong");
                    long result = blockSize * totalBlocks;
                    if (result > 0) return result;
                }
                catch { }
            }
#endif
            return 0;
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        private class RecordingEntry
        {
            public string DirectoryName { get; set; } = "";
            public long SizeBytes { get; set; }
            public double DurationSeconds { get; set; }
            public DateTime CreationTime { get; set; }
        }
    }
}
