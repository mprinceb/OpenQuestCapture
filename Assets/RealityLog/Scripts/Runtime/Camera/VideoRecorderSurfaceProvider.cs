# nullable enable

using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using RealityLog.Common;

namespace RealityLog.Camera
{
    public class VideoRecorderSurfaceProvider : SurfaceProviderBase
    {
        private const string VIDEO_RECORDER_SURFACE_PROVIDER_CLASS_NAME = "com.samusynth.questcamera.io.VideoRecorderSurfaceProvider";
        private const string UPDATE_OUTPUT_FILE_METHOD_NAME = "updateOutputFile";
        private const string START_RECORDING_METHOD_NAME = "startRecording";
        private const string STOP_RECORDING_METHOD_NAME = "stopRecording";
        private const string CLOSE_METHOD_NAME = "close";

        [SerializeField] private string dataDirectoryName = string.Empty;
        [SerializeField] private string outputVideoFileName = "left_camera.mp4";
        [SerializeField] private string cameraMetaDataFileName = "left_camera_characteristics.json";
        // Per-stream, so two providers recording in the same session (left + right eye)
        // don't overwrite each other's start/stop stamps. The primary stream keeps the
        [SerializeField] private string videoMetadataFileName = "left_camera_metadata.json";
        [SerializeField] private int targetFrameRate = 30;
        [SerializeField] private int targetBitrateMbps = 4;
        [SerializeField] private int iFrameIntervalSeconds = 1;
        [SerializeField] private int maxResolutionHeight = 720;
        [SerializeField] private bool useHevc = true;
        [Header("Audio")]
        [SerializeField] private bool enableAudio = true;
        [SerializeField] private int audioBitrate = 128000;
        [SerializeField] private int audioSamplingRate = 44100;
        [SerializeField] private CameraSessionManager? cameraSessionManager = default!;
        [SerializeField] private float recorderStartDelayAfterReopenSeconds = 0.25f;
        [SerializeField] private float maxWaitForCameraOpenSeconds = 1.5f;

        private AndroidJavaObject? currentInstance;
        private CameraMetadata? cameraMetadata;
        private bool isRecordingSessionActive;
        private bool waitingForCameraReopen;
        private Coroutine? delayedStartCoroutine;

        public long VideoStartUnixTimeMs { get; private set; }

        /// <summary>
        /// Start of this stream on the shared monotonic clock (see
        /// <see cref="MonotonicClock"/>). Written to the per-stream metadata so a host can
        /// align this video against the pose/IMU rows and against the other camera stream
        /// with the same host_time = mono_time_ns + measured_offset mapping the CSVs use.
        /// </summary>
        public long VideoStartMonoTimeNs { get; private set; }

        /// <summary>File name this provider writes its MP4 to, within the session directory.</summary>
        public string OutputVideoFileName => outputVideoFileName;

        /// <summary>
        /// True while the video file is still being finalized by the OS after stopRecording().
        /// RecordingManager should wait for this to become false before validating files.
        /// </summary>
        public bool IsFinalizingVideo { get; private set; }

        public override AndroidJavaObject? GetJavaInstance(CameraMetadata metadata)
        {
            Close();

            cameraMetadata = metadata;
            cameraSessionManager ??= GetComponent<CameraSessionManager>();

            var size = metadata.sensor.pixelArraySize;
            var outputFilePath = BuildVideoOutputPath();

            try
            {
                currentInstance = new AndroidJavaObject(
                    VIDEO_RECORDER_SURFACE_PROVIDER_CLASS_NAME,
                    size.width,
                    size.height,
                    outputFilePath,
                    targetFrameRate,
                    targetBitrateMbps,
                    iFrameIntervalSeconds,
                    enableAudio,
                    audioBitrate,
                    audioSamplingRate
                );

                Debug.Log($"[{Constants.LOG_TAG}] VideoRecorderSurfaceProvider initialized ({size.width}x{size.height}, {targetFrameRate}fps, {targetBitrateMbps}Mbps, audio={enableAudio}).");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                currentInstance = null;
            }

            return currentInstance;
        }

        public override void SetDataDirectoryName(string directoryName)
        {
            dataDirectoryName = directoryName;
        }

        public override void PrepareRecordingSession()
        {
            VideoStartUnixTimeMs = 0;
            VideoStartMonoTimeNs = 0;

            if (currentInstance == null)
            {
                return;
            }

            var outputFilePath = BuildVideoOutputPath();
            try
            {
                currentInstance.Call(UPDATE_OUTPUT_FILE_METHOD_NAME, outputFilePath);
                WriteCameraMetadataFile();
                waitingForCameraReopen = true;
                cameraSessionManager?.ReopenSession();
                Debug.Log($"[{Constants.LOG_TAG}] VideoRecorderSurfaceProvider prepared output: {outputFilePath}");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        public override void StartRecordingSession()
        {
            if (currentInstance == null)
            {
                return;
            }

            if (delayedStartCoroutine != null)
            {
                StopCoroutine(delayedStartCoroutine);
                delayedStartCoroutine = null;
            }

            if (waitingForCameraReopen)
            {
                delayedStartCoroutine = StartCoroutine(StartRecordingWhenCameraReady());
                return;
            }

            StartRecordingNow();
        }

        private const int MaxVideoFinalizeWaitMs = 3000;
        private const int VideoFinalizeCheckIntervalMs = 50;

        public override void StopRecordingSession()
        {
            if (delayedStartCoroutine != null)
            {
                StopCoroutine(delayedStartCoroutine);
                delayedStartCoroutine = null;
            }
            waitingForCameraReopen = false;

            if (currentInstance == null)
            {
                return;
            }

            // Capture the current session path before resetting, so metadata writes
            // and finalization poll use the correct file.
            var videoPath = BuildVideoOutputPath();
            var sessionDirName = dataDirectoryName;

            bool stopSucceeded = false;
            try
            {
                currentInstance.Call(STOP_RECORDING_METHOD_NAME);
                stopSucceeded = true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] VideoRecorderSurfaceProvider: stopRecording threw: {ex.Message}");
            }
            finally
            {
                isRecordingSessionActive = false;
            }

            WriteVideoMetadata(sessionDirName);

            // CRITICAL: Reset dataDirectoryName immediately after stop so that any
            // future camera reinit (app resume, session reopen) cannot create a new
            // native MediaRecorder pointing at this completed session's video file.
            // The native constructor truncates the output file, which would overwrite
            // a valid recording with 0 bytes.
            // With dataDirectoryName empty, GetJavaInstance() -> BuildVideoOutputPath()
            // resolves to the root files directory, which is safe to truncate.
            dataDirectoryName = string.Empty;

            // Also redirect the live native instance away from the saved session path.
            // This protects against the native layer touching the file during camera
            // session close/reopen before the instance is fully recreated.
            try
            {
                var safePath = BuildVideoOutputPath(); // Now resolves to root (empty dataDirectoryName)
                currentInstance.Call(UPDATE_OUTPUT_FILE_METHOD_NAME, safePath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] VideoRecorderSurfaceProvider: Failed to redirect output after stop: {ex.Message}");
            }

            if (stopSucceeded)
            {
                IsFinalizingVideo = true;
                Task.Run(() => PollVideoFinalization(videoPath));
            }
        }

        /// <summary>
        /// Runs on a background thread. Polls the video file size until it stabilizes
        /// at >0 bytes, meaning Android's MediaRecorder has finished flushing the MP4.
        /// </summary>
        private void PollVideoFinalization(string videoPath)
        {
            try
            {
                if (!File.Exists(videoPath))
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] VideoRecorderSurfaceProvider: Video file does not exist after stop: {videoPath}");
                    return;
                }

                int elapsed = 0;
                long lastSize = -1;
                while (elapsed < MaxVideoFinalizeWaitMs)
                {
                    try
                    {
                        var currentSize = new FileInfo(videoPath).Length;
                        if (currentSize > 0 && currentSize == lastSize)
                        {
                            Debug.Log($"[{Constants.LOG_TAG}] VideoRecorderSurfaceProvider: Video finalized ({currentSize} bytes, waited {elapsed}ms)");
                            return;
                        }
                        lastSize = currentSize;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[{Constants.LOG_TAG}] VideoRecorderSurfaceProvider: File check error: {ex.Message}");
                    }

                    System.Threading.Thread.Sleep(VideoFinalizeCheckIntervalMs);
                    elapsed += VideoFinalizeCheckIntervalMs;
                }

                long finalSize = 0;
                try { finalSize = File.Exists(videoPath) ? new FileInfo(videoPath).Length : 0; } catch { }
                if (finalSize == 0)
                {
                    Debug.LogError($"[{Constants.LOG_TAG}] VideoRecorderSurfaceProvider: Video file is STILL 0 bytes after {MaxVideoFinalizeWaitMs}ms wait!");
                }
                else
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] VideoRecorderSurfaceProvider: Video file size still changing after {MaxVideoFinalizeWaitMs}ms ({finalSize} bytes). Proceeding anyway.");
                }
            }
            finally
            {
                IsFinalizingVideo = false;
            }
        }

        private IEnumerator StartRecordingWhenCameraReady()
        {
            var delay = Mathf.Max(0f, recorderStartDelayAfterReopenSeconds);
            if (delay > 0f)
            {
                yield return new WaitForSecondsRealtime(delay);
            }

            var waitDeadline = Time.realtimeSinceStartup + Mathf.Max(0f, maxWaitForCameraOpenSeconds);
            while (cameraSessionManager != null && !cameraSessionManager.IsSessionOpen)
            {
                if (Time.realtimeSinceStartup >= waitDeadline)
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] VideoRecorderSurfaceProvider start wait timed out; attempting start anyway.");
                    break;
                }
                yield return null;
            }

            waitingForCameraReopen = false;
            delayedStartCoroutine = null;
            StartRecordingNow();
        }

        private void StartRecordingNow()
        {
            if (currentInstance == null)
            {
                return;
            }

            try
            {
                VideoStartUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                VideoStartMonoTimeNs = MonotonicClock.Nanos();
                currentInstance.Call(START_RECORDING_METHOD_NAME);
                isRecordingSessionActive = true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private string BuildVideoOutputPath()
        {
            var dataDirPath = Path.Join(Application.persistentDataPath, dataDirectoryName);
            Directory.CreateDirectory(dataDirPath);
            return Path.Join(dataDirPath, outputVideoFileName);
        }

        private void WriteVideoMetadata(string sessionDirName)
        {
            try
            {
                var stopUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var stopMonoNs = MonotonicClock.Nanos();
                var dataDirPath = Path.Join(Application.persistentDataPath, sessionDirName);
                Directory.CreateDirectory(dataDirPath);
                var metadataPath = Path.Join(dataDirPath, videoMetadataFileName);

                var json = $"{{\n" +
                    $"  \"recording_start_unix_ms\": {VideoStartUnixTimeMs},\n" +
                    $"  \"recording_stop_unix_ms\": {stopUnixMs},\n" +
                    $"  \"recording_start_mono_ns\": {VideoStartMonoTimeNs},\n" +
                    $"  \"recording_stop_mono_ns\": {stopMonoNs},\n" +
                    $"  \"configured_fps\": {targetFrameRate},\n" +
                    $"  \"video_file\": \"{outputVideoFileName}\",\n" +
                    $"  \"audio_enabled\": {(enableAudio ? "true" : "false")},\n" +
                    $"  \"audio_bitrate\": {audioBitrate},\n" +
                    $"  \"audio_sampling_rate\": {audioSamplingRate}\n" +
                    $"}}";
                File.WriteAllText(metadataPath, json);
                Debug.Log($"[{Constants.LOG_TAG}] VideoRecorderSurfaceProvider wrote {metadataPath}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] VideoRecorderSurfaceProvider - Failed to write video metadata: {ex.Message}");
            }
        }

        private void WriteCameraMetadataFile()
        {
            if (cameraMetadata == null)
            {
                return;
            }

            var dataDirPath = Path.Join(Application.persistentDataPath, dataDirectoryName);
            Directory.CreateDirectory(dataDirPath);

            var metadataPath = Path.Join(dataDirPath, cameraMetaDataFileName);
            var metadataJson = JsonUtility.ToJson(cameraMetadata);
            File.WriteAllText(metadataPath, metadataJson);
        }

        private void OnDestroy()
        {
            Close();
        }

        private void Close()
        {
            if (delayedStartCoroutine != null)
            {
                StopCoroutine(delayedStartCoroutine);
                delayedStartCoroutine = null;
            }
            waitingForCameraReopen = false;

            if (currentInstance == null)
            {
                return;
            }

            try
            {
                if (isRecordingSessionActive)
                {
                    try
                    {
                        currentInstance.Call(STOP_RECORDING_METHOD_NAME);
                    }
                    catch (Exception stopEx)
                    {
                        Debug.LogException(stopEx);
                    }
                    finally
                    {
                        isRecordingSessionActive = false;
                    }

                    // Reset so the next GetJavaInstance() (called immediately after
                    // Close() during camera reinit) won't point at the saved session.
                    dataDirectoryName = string.Empty;
                }
                currentInstance.Call(CLOSE_METHOD_NAME);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                currentInstance.Dispose();
                currentInstance = null;
                isRecordingSessionActive = false;
            }
        }
    }
}
