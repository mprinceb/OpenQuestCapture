# nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Events;
using RealityLog.Camera;
using RealityLog.Common;
using RealityLog.Core;
using RealityLog.Depth;
using RealityLog.OVR;

namespace RealityLog
{
    /// <summary>
    /// Central coordinator for all recording subsystems.
    /// Handles proper sequencing and lifecycle management of depth, camera, and pose recording.
    /// </summary>
    public class RecordingManager : MonoBehaviour
    {
        [Header("Recording Components")]
        [Tooltip("Manages depth map export")]
        [SerializeField] private DepthMapExporter? depthMapExporter = default!;
        
        [Tooltip("Manages camera image capture (left, right, etc.)")]
        [SerializeField] private SurfaceProviderBase[] cameraProviders = default!;
        
        [Tooltip("Manages pose logging (HMD, controllers, etc.)")]
        [SerializeField] private PoseLogger[] poseLoggers = default!;
        
        [Tooltip("Manages IMU logging (accelerometer, gyroscope)")]
        [SerializeField] private IMULogger[] imuLoggers = default!;

        [Tooltip("Manages body tracking logging (full body skeleton)")]
        [SerializeField] private BodyTrackingLogger[] bodyTrackingLoggers = default!;

        [Tooltip("Manages FPS timing for synchronized capture")]
        [SerializeField] private CaptureTimer captureTimer = default!;

        [Header("Recording Settings")]
        [SerializeField] private bool generateTimestampedDirectories = true;
        [SerializeField] private bool recordDepthMaps = false;

        [Header("Events")]
        [Tooltip("Invoked when recording stops and files are saved. Passes the directory name where files were saved.")]
        [SerializeField] private UnityEvent<string> onRecordingSaved = default!;

        [Tooltip("Invoked when recording starts.")]
        [SerializeField] private UnityEvent onRecordingStarted = default!;

        private bool isRecording = false;
        private float recordingStartTime = 0f;
        private string? currentSessionDirectory = null;
        private Coroutine? stopCoroutine;
        private const long MinExpectedVideoBytes = 1024;
        private const long MinExpectedTimestampBytes = 128;
        private const string TrackingOriginFileName = "tracking_origin.json";
        private TrackingOriginSession? trackingOriginSession;
        private bool recenterEventSubscribed;

        // Grace period: don't stop recording on a brief OS pause (proximity sensor misfire,
        // system overlay, Guardian boundary glitch). Only stop if the pause lasts longer than
        // this threshold. If the app is killed while paused, OnDestroy handles the stop.
        private DateTime? recordingPauseStartTime;
        private const double PauseGraceSeconds = 8.0;

        private static readonly string[] MotionFileNames = new[]
        {
            "imu.csv",
            "hmd_poses.csv",
            "left_controller_poses.csv",
            "right_controller_poses.csv",
        };

        // Cached reference to InfoCanvas animator for hiding standby label during recording
        private Animator? infoCanvasAnimator;
        private static readonly int IsRunningParam = Animator.StringToHash("IsRunning");

        public bool IsRecording => isRecording;
        public string? CurrentSessionDirectory => currentSessionDirectory;
        
        /// <summary>
        /// Gets the elapsed recording time in seconds.
        /// Returns 0 if not currently recording.
        /// </summary>
        public float RecordingDuration => isRecording ? Time.time - recordingStartTime : 0f;

        private void Start()
        {
            // Auto-discover body tracking loggers if not assigned in Inspector
            if (bodyTrackingLoggers == null || bodyTrackingLoggers.Length == 0)
            {
                bodyTrackingLoggers = FindObjectsByType<BodyTrackingLogger>(FindObjectsSortMode.None);
                if (bodyTrackingLoggers.Length > 0)
                {
                    Debug.Log($"[{Constants.LOG_TAG}] RecordingManager: Auto-discovered {bodyTrackingLoggers.Length} BodyTrackingLogger(s)");
                }
            }

            if (depthMapExporter != null)
            {
                depthMapExporter.IsExportEnabled = recordDepthMaps;
            }

            // Find InfoCanvas animator so we can hide the standby label when recording
            // starts from any source (cloud relay, HTTP, toggle, calibration)
            var infoCanvas = GameObject.Find("InfoCanvas");
            if (infoCanvas != null)
            {
                infoCanvasAnimator = infoCanvas.GetComponent<Animator>();
            }
        }

        /// <summary>
        /// Starts recording from all subsystems in the proper order.
        /// </summary>
        public void StartRecording()
        {
            if (isRecording)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingManager: Already recording!");
                return;
            }

            // Generate session directory name if needed
            if (generateTimestampedDirectories)
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                currentSessionDirectory = timestamp;
                if (recordDepthMaps && depthMapExporter != null)
                {
                    depthMapExporter.DirectoryName = timestamp;
                }
                foreach (var provider in cameraProviders)
                {
                    provider.SetDataDirectoryName(timestamp);
                }
                foreach (var logger in poseLoggers)
                {
                    logger.DirectoryName = timestamp;
                }
                foreach (var logger in imuLoggers)
                {
                    logger.DirectoryName = timestamp;
                }
                foreach (var logger in bodyTrackingLoggers)
                {
                    logger.DirectoryName = timestamp;
                }
            }
            else
            {
                currentSessionDirectory = recordDepthMaps && depthMapExporter != null && !string.IsNullOrEmpty(depthMapExporter.DirectoryName)
                    ? depthMapExporter.DirectoryName
                    : "manual_session";

                foreach (var provider in cameraProviders)
                {
                    provider.SetDataDirectoryName(currentSessionDirectory);
                }
                foreach (var logger in poseLoggers)
                {
                    logger.DirectoryName = currentSessionDirectory;
                }
                foreach (var logger in imuLoggers)
                {
                    logger.DirectoryName = currentSessionDirectory;
                }
                foreach (var logger in bodyTrackingLoggers)
                {
                    logger.DirectoryName = currentSessionDirectory;
                }
            }

            Debug.Log($"[{Constants.LOG_TAG}] RecordingManager: Starting recording session '{currentSessionDirectory}'");

            // Step 1: Update camera paths for new session
            // This ensures format info and images are written to the new directory
            foreach (var provider in cameraProviders)
            {
                provider.PrepareRecordingSession();
            }

            // Step 2: Setup file writers and directories
            // (Depth and camera systems are already initialized from app start)
            if (depthMapExporter != null)
            {
                depthMapExporter.IsExportEnabled = recordDepthMaps;
                if (recordDepthMaps)
                {
                    depthMapExporter.StartExport();
                }
            }
            foreach (var logger in poseLoggers)
            {
                logger.StartLogging();
            }
            foreach (var logger in imuLoggers)
            {
                logger.StartLogging();
            }
            foreach (var logger in bodyTrackingLoggers)
            {
                logger.StartLogging();
            }

            foreach (var provider in cameraProviders)
            {
                provider.StartRecordingSession();
            }
            
            // Optional: Reset camera base time to sync with depth timestamps
            // Currently commented out as both use system monotonic clock
            // Uncomment if timestamp alignment issues occur
            // foreach (var provider in cameraProviders)
            // {
            //     provider.ResetBaseTime();
            // }

            // Step 3: Start synchronized capture
            // This begins the actual frame capture loop
            captureTimer.StartCapture();

            isRecording = true;
            recordingStartTime = Time.time;
            StartTrackingOriginRecord();

            // Hide the standby label (green instruction box) regardless of how recording was started
            infoCanvasAnimator?.SetBool(IsRunningParam, true);

            WriteVideoStartTime();
            DeviceInfo.WriteToSession(Path.Combine(Application.persistentDataPath, currentSessionDirectory ?? ""));

            onRecordingStarted?.Invoke();
            ForegroundServiceManager.UpdateNotification("Recording in progress");

            Debug.Log($"[{Constants.LOG_TAG}] RecordingManager: Recording started successfully");
        }

        /// <summary>
        /// Stops recording from all subsystems in the proper order.
        /// Video finalization (OS flushing the MP4) runs on a background thread;
        /// a coroutine waits for it to complete before firing saved events.
        /// </summary>
        public void StopRecording()
        {
            if (!isRecording)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingManager: Not currently recording!");
                return;
            }

            Debug.Log($"[{Constants.LOG_TAG}] RecordingManager: Stopping recording session");

            // Stop in reverse order
            // Step 1: Stop capture loop first
            captureTimer.StopCapture();

            // Freeze both camera exposure timelines before either encoder performs
            // synchronous drain/finalization. Otherwise the second eye keeps recording
            // while the first eye stops and their delivered frame counts diverge.
            foreach (var provider in cameraProviders)
            {
                provider.RequestStopRecordingSession();
            }
            foreach (var provider in cameraProviders)
            {
                provider.StopRecordingSession();
            }

            // Step 2: Close file writers and cleanup
            if (recordDepthMaps && depthMapExporter != null)
            {
                depthMapExporter.StopExport();
            }
            foreach (var logger in poseLoggers)
            {
                logger.StopLogging();
            }
            foreach (var logger in imuLoggers)
            {
                logger.StopLogging();
            }
            foreach (var logger in bodyTrackingLoggers)
            {
                logger.StopLogging();
            }

            // Store directory name before resetting state
            string savedDirectory = currentSessionDirectory ?? string.Empty;

            WriteTrackingOriginRecord(savedDirectory);

            isRecording = false;
            recordingStartTime = 0f;
            currentSessionDirectory = null;

            // Show the standby label again now that recording has stopped
            infoCanvasAnimator?.SetBool(IsRunningParam, false);

            // Wait for video finalization on a background thread, then fire events
            if (!string.IsNullOrEmpty(savedDirectory))
            {
                if (stopCoroutine != null)
                    StopCoroutine(stopCoroutine);
                stopCoroutine = StartCoroutine(WaitForFinalizationThenNotify(savedDirectory));
            }
            else
            {
                Debug.Log($"[{Constants.LOG_TAG}] RecordingManager: Recording stopped (no session directory)");
            }
        }

        private IEnumerator WaitForFinalizationThenNotify(string savedDirectory)
        {
            // Yield until all video providers finish background finalization
            bool anyFinalizing = true;
            while (anyFinalizing)
            {
                anyFinalizing = false;
                foreach (var provider in cameraProviders)
                {
                    if (provider is VideoRecorderSurfaceProvider vp && vp.IsFinalizingVideo)
                    {
                        anyFinalizing = true;
                        break;
                    }
                }
                if (anyFinalizing)
                    yield return null;
            }

            onRecordingSaved?.Invoke(savedDirectory);
            ValidateSavedSession(savedDirectory);
            ForegroundServiceManager.UpdateNotification("Ready — waiting for commands");

            Debug.Log($"[{Constants.LOG_TAG}] RecordingManager: Recording stopped successfully. Files saved to '{savedDirectory}'");
            stopCoroutine = null;
        }

        /// <summary>
        /// Toggle recording on/off. Useful for UI buttons.
        /// </summary>
        public void ToggleRecording()
        {
            if (isRecording)
                StopRecording();
            else
                StartRecording();
        }

        private void OnValidate()
        {
            // Validate required references in editor
            if (recordDepthMaps && depthMapExporter == null)
                Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingManager: recordDepthMaps is enabled but DepthMapExporter is missing!");
            
            if (cameraProviders == null || cameraProviders.Length == 0)
                Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingManager: No camera surface providers assigned!");
            
            if (poseLoggers == null || poseLoggers.Length == 0)
                Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingManager: No PoseLoggers assigned! Add HMD, controllers, etc.");
            
            if (captureTimer == null)
                Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingManager: Missing CaptureTimer reference!");
        }

        private void OnDestroy()
        {
            // Safety: ensure recording stops on cleanup.
            // Can't rely on the coroutine here since Unity is tearing down the
            // MonoBehaviour — do a synchronous stop and validate immediately.
            // Native encoder stop/finalization is synchronous, and the bg poll is
            // just a verification safety net, so skipping it on destroy is fine.
            recordingPauseStartTime = null; // Clear any pending grace period on destroy.

            if (isRecording)
            {
                Debug.Log($"[{Constants.LOG_TAG}] RecordingManager: OnDestroy while recording; performing synchronous stop.");

                captureTimer.StopCapture();

                foreach (var provider in cameraProviders)
                    provider.RequestStopRecordingSession();
                foreach (var provider in cameraProviders)
                    provider.StopRecordingSession();

                if (recordDepthMaps && depthMapExporter != null)
                    depthMapExporter.StopExport();
                foreach (var logger in poseLoggers)
                    logger.StopLogging();
                foreach (var logger in imuLoggers)
                    logger.StopLogging();
                foreach (var logger in bodyTrackingLoggers)
                    logger.StopLogging();

                string savedDirectory = currentSessionDirectory ?? string.Empty;
                WriteTrackingOriginRecord(savedDirectory);
                isRecording = false;
                recordingStartTime = 0f;
                currentSessionDirectory = null;
                infoCanvasAnimator?.SetBool(IsRunningParam, false);

                if (!string.IsNullOrEmpty(savedDirectory))
                {
                    onRecordingSaved?.Invoke(savedDirectory);
                    ValidateSavedSession(savedDirectory);
                }
                ForegroundServiceManager.UpdateNotification("Ready — waiting for commands");
            }
            else if (stopCoroutine != null)
            {
                // A finalization coroutine from a prior StopRecording() is still waiting.
                // The bg thread will finish on its own; fire the events synchronously now
                // since the coroutine won't survive OnDestroy.
                // (savedDirectory was already cleared, so nothing further to do — the bg
                //  thread's PollVideoFinalization will still log its result.)
                stopCoroutine = null;
            }
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus)
            {
                // App is pausing. Don't stop immediately — brief pauses (proximity sensor
                // misfire, system overlay, Guardian glitch) should not kill the recording.
                // Record the real-world timestamp and decide on resume.
                if (isRecording)
                {
                    recordingPauseStartTime = DateTime.UtcNow;
                    Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingManager: App paused during recording — grace period is {PauseGraceSeconds}s.");
                }
            }
            else
            {
                // App is resuming. Decide whether the pause was long enough to warrant stopping.
                if (recordingPauseStartTime.HasValue)
                {
                    var elapsed = (DateTime.UtcNow - recordingPauseStartTime.Value).TotalSeconds;
                    recordingPauseStartTime = null;

                    if (!isRecording)
                    {
                        // Recording was stopped by another path (e.g., user pressed stop via cloud relay).
                        return;
                    }

                    if (elapsed > PauseGraceSeconds)
                    {
                        Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingManager: App was paused for {elapsed:F1}s (> {PauseGraceSeconds}s grace) — stopping recording.");
                        StopRecordingForInterruption("extended_pause");
                    }
                    else
                    {
                        Debug.Log($"[{Constants.LOG_TAG}] RecordingManager: App resumed after {elapsed:F1}s (within grace period) — recording continues.");
                    }
                }
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            // Do NOT stop recording on focus loss. On Quest, focus loss is frequent
            // (guardian boundary, quick settings, notifications) and the camera keeps
            // delivering frames. Only OnApplicationPause (headset removal, app switch)
            // requires stopping the recording.
        }

        private void StopRecordingForInterruption(string reason)
        {
            if (!isRecording)
            {
                return;
            }

            Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingManager: App interruption ({reason}) during recording; forcing stop to finalize files.");
            StopRecording();
        }

        private void WriteVideoStartTime()
        {
            // Back-compat anchor for the primary stream (the first video provider in
            // `cameraProviders`). Per-stream start/stop stamps — including the monotonic
            // ones used for cross-stream alignment — live in each provider's own metadata
            // JSON, which is the file to read when more than one camera is recording.
            long videoStartMs = 0;
            foreach (var provider in cameraProviders)
            {
                if (provider is VideoRecorderSurfaceProvider videoProvider
                    && videoProvider.VideoStartUnixTimeMs > 0)
                {
                    videoStartMs = videoProvider.VideoStartUnixTimeMs;
                    break;
                }
            }

            if (videoStartMs <= 0)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] No video start timestamp available");
                return;
            }

            try
            {
                var filePath = Path.Combine(
                    Application.persistentDataPath,
                    currentSessionDirectory ?? "",
                    "video_start_time.txt"
                );
                File.WriteAllText(filePath, videoStartMs.ToString());
                Debug.Log($"[{Constants.LOG_TAG}] Wrote video_start_time.txt: {videoStartMs}ms");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] Failed to write video_start_time.txt: {ex.Message}");
            }
        }

        private void ValidateSavedSession(string sessionDirectoryName)
        {
            try
            {
                var sessionDir = Path.Join(Application.persistentDataPath, sessionDirectoryName);

                // Every video provider in the session must have produced a usable file —
                // with more than one camera stream recording, a single healthy MP4 is not
                // enough to call the session good.
                var videoOk = true;
                var videoReport = string.Empty;
                foreach (var provider in cameraProviders)
                {
                    if (provider is VideoRecorderSurfaceProvider videoProvider)
                    {
                        var videoPath = Path.Join(sessionDir, videoProvider.OutputVideoFileName);
                        var timestampPath = Path.Join(
                            sessionDir,
                            videoProvider.FrameTimestampsFileName
                        );
                        var videoBytes = File.Exists(videoPath) ? new FileInfo(videoPath).Length : 0L;
                        var timestampBytes = File.Exists(timestampPath)
                            ? new FileInfo(timestampPath).Length
                            : 0L;
                        if (
                            videoBytes < MinExpectedVideoBytes
                            || timestampBytes < MinExpectedTimestampBytes
                        )
                        {
                            videoOk = false;
                        }

                        videoReport +=
                            $" {videoProvider.OutputVideoFileName}={videoBytes}B" +
                            $" {videoProvider.FrameTimestampsFileName}={timestampBytes}B";
                    }
                }

                var trackingOriginPath = Path.Join(sessionDir, TrackingOriginFileName);
                var trackingOriginOk =
                    File.Exists(trackingOriginPath)
                    && new FileInfo(trackingOriginPath).Length > 0;

                var motionOk = false;
                foreach (var fileName in MotionFileNames)
                {
                    var motionPath = Path.Join(sessionDir, fileName);
                    if (!File.Exists(motionPath))
                    {
                        continue;
                    }

                    if (new FileInfo(motionPath).Length > 0)
                    {
                        motionOk = true;
                        break;
                    }
                }

                if (videoOk && motionOk && trackingOriginOk)
                {
                    return;
                }

                Debug.LogWarning(
                    $"[{Constants.LOG_TAG}] RecordingManager: Session integrity warning for '{sessionDirectoryName}'. " +
                    $"videos:{videoReport}, has_motion_stream={motionOk}, " +
                    $"has_tracking_origin={trackingOriginOk}"
                );
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingManager: Failed to validate session '{sessionDirectoryName}': {ex.Message}");
            }
        }

        private void StartTrackingOriginRecord()
        {
            TrySubscribeToRecenterEvents();
            var manager = OVRManager.instance;
            var runtimeOrigin = OVRPlugin.GetTrackingOriginType().ToString();
            trackingOriginSession = new TrackingOriginSession
            {
                schema = "openquest.tracking_origin/v1",
                configured_origin = manager != null
                    ? manager.trackingOriginType.ToString()
                    : "Unknown",
                runtime_origin_at_start = runtimeOrigin,
                runtime_origin_at_stop = runtimeOrigin,
                allow_recenter = manager != null && manager.AllowRecenter,
                floor_height_reference = IsFloorReferenced(runtimeOrigin)
                    ? "quest_runtime_calibrated_floor"
                    : "not_floor_referenced",
                recording_start_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                recording_start_mono_ns = MonotonicClock.Nanos(),
                recenter_count_at_start = OVRPlugin.GetLocalTrackingSpaceRecenterCount(),
                recenter_count_at_stop = OVRPlugin.GetLocalTrackingSpaceRecenterCount(),
                recenter_events = new List<TrackingOriginRecenterEvent>(),
            };
        }

        private void OnTrackingOriginRecentered()
        {
            if (!isRecording || trackingOriginSession == null)
            {
                return;
            }

            trackingOriginSession.recenter_events.Add(
                new TrackingOriginRecenterEvent
                {
                    unix_time_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    mono_time_ns = MonotonicClock.Nanos(),
                    recenter_count = OVRPlugin.GetLocalTrackingSpaceRecenterCount(),
                    runtime_origin = OVRPlugin.GetTrackingOriginType().ToString(),
                }
            );
        }

        private void WriteTrackingOriginRecord(string sessionDirectoryName)
        {
            try
            {
                if (trackingOriginSession == null || string.IsNullOrEmpty(sessionDirectoryName))
                {
                    return;
                }

                trackingOriginSession.recording_stop_unix_ms =
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                trackingOriginSession.recording_stop_mono_ns = MonotonicClock.Nanos();
                trackingOriginSession.runtime_origin_at_stop =
                    OVRPlugin.GetTrackingOriginType().ToString();
                trackingOriginSession.recenter_count_at_stop =
                    OVRPlugin.GetLocalTrackingSpaceRecenterCount();

                var sessionDir = Path.Join(
                    Application.persistentDataPath,
                    sessionDirectoryName
                );
                Directory.CreateDirectory(sessionDir);
                File.WriteAllText(
                    Path.Join(sessionDir, TrackingOriginFileName),
                    JsonUtility.ToJson(trackingOriginSession, true)
                );
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[{Constants.LOG_TAG}] Failed to write {TrackingOriginFileName}: {ex.Message}"
                );
            }
            finally
            {
                trackingOriginSession = null;
                UnsubscribeFromRecenterEvents();
            }
        }

        private void TrySubscribeToRecenterEvents()
        {
            if (recenterEventSubscribed || OVRManager.display == null)
            {
                return;
            }
            OVRManager.display.RecenteredPose += OnTrackingOriginRecentered;
            recenterEventSubscribed = true;
        }

        private void UnsubscribeFromRecenterEvents()
        {
            if (!recenterEventSubscribed || OVRManager.display == null)
            {
                return;
            }
            OVRManager.display.RecenteredPose -= OnTrackingOriginRecentered;
            recenterEventSubscribed = false;
        }

        private static bool IsFloorReferenced(string origin)
        {
            return origin == "FloorLevel" || origin == "Stage";
        }

        [Serializable]
        private sealed class TrackingOriginSession
        {
            public string schema = string.Empty;
            public string configured_origin = string.Empty;
            public string runtime_origin_at_start = string.Empty;
            public string runtime_origin_at_stop = string.Empty;
            public bool allow_recenter;
            public string floor_height_reference = string.Empty;
            public long recording_start_unix_ms;
            public long recording_stop_unix_ms;
            public long recording_start_mono_ns;
            public long recording_stop_mono_ns;
            public int recenter_count_at_start;
            public int recenter_count_at_stop;
            public List<TrackingOriginRecenterEvent> recenter_events = new();
        }

        [Serializable]
        private sealed class TrackingOriginRecenterEvent
        {
            public long unix_time_ms;
            public long mono_time_ns;
            public int recenter_count;
            public string runtime_origin = string.Empty;
        }
    }
}
