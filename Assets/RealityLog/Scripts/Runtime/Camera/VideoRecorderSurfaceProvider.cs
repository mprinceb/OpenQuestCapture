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
        private const string REQUEST_STOP_RECORDING_METHOD_NAME = "requestStopRecording";
        private const string STOP_RECORDING_METHOD_NAME = "stopRecording";
        private const string GET_SOURCE_FRAME_COUNT_METHOD_NAME = "getSourceFrameCount";
        private const string GET_SOURCE_DROPPED_FRAME_COUNT_METHOD_NAME = "getSourceDroppedFrameCount";
        private const string GET_SELECTED_FRAME_COUNT_METHOD_NAME = "getSelectedFrameCount";
        private const string CLOSE_METHOD_NAME = "close";
        private const string CaptureContractVersion = "1.4.0";
        private const string FrameTimestampsSchema = "openquest.camera_frame_timestamps/v2";

        [SerializeField] private string dataDirectoryName = string.Empty;
        [SerializeField] private string outputVideoFileName = "left_camera.mp4";
        [SerializeField] private string cameraMetaDataFileName = "left_camera_characteristics.json";
        // Per-stream, so two providers recording in the same session (left + right eye)
        // don't overwrite each other's start/stop stamps. The primary stream keeps the
        [SerializeField] private string videoMetadataFileName = "left_camera_metadata.json";
        [SerializeField] private string frameTimestampsFileName = "left_camera_timestamps.csv";
        [SerializeField] private int targetFrameRate = 30;
        [SerializeField] private int targetBitrateMbps = 4;
        [SerializeField] private int iFrameIntervalSeconds = 1;
        [SerializeField] private int maxResolutionHeight = 720;
        [SerializeField] private bool useHevc = true;
        [Header("Audio")]
        [SerializeField] private bool enableAudio = false;
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
        private long sourceFrameCount;
        private long sourceDroppedFrameCount;
        private long selectedFrameCount;

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

        /// <summary>Per-encoded-frame Camera2 exposure timestamp sidecar.</summary>
        public string FrameTimestampsFileName => frameTimestampsFileName;

        /// <summary>
        /// True while the video file is still being finalized by the OS after stopRecording().
        /// RecordingManager should wait for this to become false before validating files.
        /// </summary>
        public bool IsFinalizingVideo { get; private set; }

        public override AndroidJavaObject? GetJavaInstance(CameraMetadata metadata)
        {
            cameraMetadata = metadata;
            cameraSessionManager ??= GetComponent<CameraSessionManager>();

            // CameraSessionManager may be recreated across an XR pause. Re-register
            // the persistent Camera2-facing surface instead of destroying the active
            // encoder and losing its episode metadata.
            if (currentInstance != null)
            {
                return currentInstance;
            }

            var sensorSize = metadata.sensor.pixelArraySize;
            var scale = maxResolutionHeight > 0
                ? Mathf.Min(1f, maxResolutionHeight / (float)sensorSize.height)
                : 1f;
            var outputWidth = Mathf.Max(2, Mathf.RoundToInt(sensorSize.width * scale) & ~1);
            var outputHeight = Mathf.Max(2, Mathf.RoundToInt(sensorSize.height * scale) & ~1);
            var outputFilePath = BuildVideoOutputPath();
            var frameTimestampFilePath = BuildFrameTimestampOutputPath();

            try
            {
                currentInstance = new AndroidJavaObject(
                    VIDEO_RECORDER_SURFACE_PROVIDER_CLASS_NAME,
                    outputWidth,
                    outputHeight,
                    outputFilePath,
                    frameTimestampFilePath,
                    targetFrameRate,
                    targetBitrateMbps,
                    iFrameIntervalSeconds,
                    enableAudio,
                    audioBitrate,
                    audioSamplingRate
                );

                Debug.Log($"[{Constants.LOG_TAG}] VideoRecorderSurfaceProvider initialized ({outputWidth}x{outputHeight} from {sensorSize.width}x{sensorSize.height}, {targetFrameRate}fps, {targetBitrateMbps}Mbps, audio={enableAudio}).");
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
            sourceFrameCount = 0;
            sourceDroppedFrameCount = 0;
            selectedFrameCount = 0;

            if (currentInstance == null)
            {
                return;
            }

            var outputFilePath = BuildVideoOutputPath();
            var frameTimestampFilePath = BuildFrameTimestampOutputPath();
            try
            {
                currentInstance.Call(
                    UPDATE_OUTPUT_FILE_METHOD_NAME,
                    outputFilePath,
                    frameTimestampFilePath
                );
                WriteCameraMetadataFile();
                // The native MediaCodec pipeline keeps a persistent Camera2-facing
                // SurfaceTexture, so changing the MP4 output no longer requires a
                // camera close/reopen. Codec setup happens here, before timestamps
                // are captured by StartRecordingNow().
                waitingForCameraReopen = false;
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

        public override void RequestStopRecordingSession()
        {
            if (currentInstance == null || !isRecordingSessionActive)
            {
                return;
            }

            try
            {
                currentInstance.Call(REQUEST_STOP_RECORDING_METHOD_NAME);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] VideoRecorderSurfaceProvider: requestStopRecording threw: {ex.Message}");
            }
        }

        public override void StopRecordingSession()
        {
            if (delayedStartCoroutine != null)
            {
                StopCoroutine(delayedStartCoroutine);
                delayedStartCoroutine = null;
            }
            waitingForCameraReopen = false;

            if (currentInstance == null || !isRecordingSessionActive)
            {
                return;
            }

            FinalizeRecordingSession(scheduleFinalizationPoll: true);
        }

        private void FinalizeRecordingSession(bool scheduleFinalizationPoll)
        {
            if (currentInstance == null || !isRecordingSessionActive)
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
                currentInstance.Call(REQUEST_STOP_RECORDING_METHOD_NAME);
                currentInstance.Call(STOP_RECORDING_METHOD_NAME);
                stopSucceeded = true;
            }
            catch (Exception ex)
            {
                // AndroidJavaException.Message only contains the outer JNI wrapper.
                // Preserve the Java cause/stack so a primary encoder failure is not
                // mistaken for whichever finalization invariant observes it later.
                Debug.LogError($"[{Constants.LOG_TAG}] VideoRecorderSurfaceProvider: stopRecording threw: {ex}");
            }
            finally
            {
                ReadCaptureStats();
                isRecordingSessionActive = false;
            }

            WriteVideoMetadata(sessionDirName);

            // CRITICAL: Reset dataDirectoryName immediately after stop so that any
            // future camera reinit (app resume, session reopen) cannot start a new
            // native encoder pointing at this completed session's video file.
            // With dataDirectoryName empty, GetJavaInstance() -> BuildVideoOutputPath()
            // resolves to the root files directory, which is safe to truncate.
            dataDirectoryName = string.Empty;

            if (stopSucceeded && scheduleFinalizationPoll)
            {
                IsFinalizingVideo = true;
                Task.Run(() => PollVideoFinalization(videoPath));
            }
        }

        /// <summary>
        /// Runs on a background thread. Polls the video file size until it stabilizes
        /// at >0 bytes, meaning Android's MediaMuxer has finished flushing the MP4.
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

        private string BuildFrameTimestampOutputPath()
        {
            var dataDirPath = Path.Join(Application.persistentDataPath, dataDirectoryName);
            Directory.CreateDirectory(dataDirPath);
            return Path.Join(dataDirPath, frameTimestampsFileName);
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
                var timestampSource = cameraMetadata?.sensor?.timestampSource ?? "UNKNOWN";

                var json = $"{{\n" +
                    $"  \"capture_contract_version\": \"{CaptureContractVersion}\",\n" +
                    $"  \"recording_start_unix_ms\": {VideoStartUnixTimeMs},\n" +
                    $"  \"recording_stop_unix_ms\": {stopUnixMs},\n" +
                    $"  \"recording_start_mono_ns\": {VideoStartMonoTimeNs},\n" +
                    $"  \"recording_stop_mono_ns\": {stopMonoNs},\n" +
                    $"  \"configured_fps\": {targetFrameRate},\n" +
                    $"  \"gop_frames\": {targetFrameRate * iFrameIntervalSeconds},\n" +
                    $"  \"video_file\": \"{EscapeJson(outputVideoFileName)}\",\n" +
                    $"  \"frame_timestamps_file\": \"{EscapeJson(frameTimestampsFileName)}\",\n" +
                    $"  \"frame_timestamps_schema\": \"{FrameTimestampsSchema}\",\n" +
                    $"  \"frame_timestamp_semantics\": \"sensor_exposure_start\",\n" +
                    $"  \"sensor_timestamp_source\": \"{EscapeJson(timestampSource)}\",\n" +
                    $"  \"source_frame_count\": {sourceFrameCount},\n" +
                    $"  \"source_dropped_frame_count\": {sourceDroppedFrameCount},\n" +
                    $"  \"selected_frame_count\": {selectedFrameCount},\n" +
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

        private static string EscapeJson(string value)
        {
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        private void ReadCaptureStats()
        {
            if (currentInstance == null)
            {
                return;
            }

            try
            {
                sourceFrameCount = currentInstance.Call<long>(GET_SOURCE_FRAME_COUNT_METHOD_NAME);
                sourceDroppedFrameCount = currentInstance.Call<long>(
                    GET_SOURCE_DROPPED_FRAME_COUNT_METHOD_NAME
                );
                selectedFrameCount = currentInstance.Call<long>(GET_SELECTED_FRAME_COUNT_METHOD_NAME);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] VideoRecorderSurfaceProvider: failed to read capture counters: {ex.Message}");
                sourceFrameCount = -1;
                sourceDroppedFrameCount = -1;
                selectedFrameCount = -1;
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
                    FinalizeRecordingSession(scheduleFinalizationPoll: false);
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
