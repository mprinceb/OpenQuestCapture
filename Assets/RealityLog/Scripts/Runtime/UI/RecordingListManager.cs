# nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using RealityLog.Common;

namespace RealityLog.UI
{
    /// <summary>
    /// Manages scanning and listing recording directories from the file system.
    /// </summary>
    public class RecordingListManager : MonoBehaviour
    {
        [Header("Settings")]
        [Tooltip("Base directory name pattern for recordings (e.g., YYYYMMDD_HHMMSS)")]
        [SerializeField] private string recordingDirectoryPattern = "\\d{8}_\\d{6}";

        private List<RecordingInfo> recordings = new List<RecordingInfo>();

        /// <summary>
        /// Information about a recording session.
        /// </summary>
        [Serializable]
        public class RecordingInfo
        {
            public string DirectoryName { get; set; } = string.Empty;
            public string FullPath { get; set; } = string.Empty;
            public DateTime CreationTime { get; set; }
            public long SizeBytes { get; set; }
            public HealthLevel QuickHealth { get; set; } = HealthLevel.Good;
            public double DurationSeconds { get; set; } = -1;
            public int EpisodeMarkerCount { get; set; } = 0;
            public string FormattedSize => FormatBytes(SizeBytes);
            public string FormattedDate => CreationTime.ToString("yyyy-MM-dd HH:mm:ss");

            public string FriendlyTitle
            {
                get
                {
                    if (DirectoryName.Length >= 15 &&
                        int.TryParse(DirectoryName.Substring(0, 4), out int year) &&
                        int.TryParse(DirectoryName.Substring(4, 2), out int month) &&
                        int.TryParse(DirectoryName.Substring(6, 2), out int day) &&
                        int.TryParse(DirectoryName.Substring(9, 2), out int hour) &&
                        int.TryParse(DirectoryName.Substring(11, 2), out int minute))
                    {
                        var dt = new DateTime(year, month, day, hour, minute, 0);
                        var today = DateTime.Today;

                        if (dt.Date == today)
                            return $"Today {dt:h:mm tt}";
                        if (dt.Date == today.AddDays(-1))
                            return $"Yesterday {dt:h:mm tt}";
                        if (dt.Year == today.Year)
                            return dt.ToString("MMM d, h:mm tt");
                        return dt.ToString("MMM d yyyy, h:mm tt");
                    }

                    return DirectoryName;
                }
            }

            public string FormattedDuration
            {
                get
                {
                    if (DurationSeconds < 0) return "--:--";
                    int totalSec = (int)DurationSeconds;
                    int min = totalSec / 60;
                    int sec = totalSec % 60;
                    return $"{min:D2}:{sec:D2}";
                }
            }

            private static string FormatBytes(long bytes)
            {
                string[] sizes = { "B", "KB", "MB", "GB" };
                double len = bytes;
                int order = 0;
                while (len >= 1024 && order < sizes.Length - 1)
                {
                    order++;
                    len = len / 1024;
                }
                return $"{len:0.##} {sizes[order]}";
            }
        }

        /// <summary>
        /// Event fired when the recording list is updated.
        /// </summary>
        public event Action<List<RecordingInfo>>? OnRecordingsUpdated;

        /// <summary>
        /// Gets the current list of recordings.
        /// </summary>
        public List<RecordingInfo> Recordings => new List<RecordingInfo>(recordings);

        /// <summary>
        /// Scans the persistent data path for recording directories.
        /// </summary>
        public void RefreshRecordings()
        {
            recordings.Clear();

            try
            {
                string dataPath = Application.persistentDataPath;
                
                if (!Directory.Exists(dataPath))
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingListManager: Data path does not exist: {dataPath}");
                    OnRecordingsUpdated?.Invoke(recordings);
                    return;
                }

                var directories = Directory.GetDirectories(dataPath);
                
                foreach (var dirPath in directories)
                {
                    string dirName = Path.GetFileName(dirPath);
                    
                    // Check if directory name matches recording pattern (YYYYMMDD_HHMMSS)
                    if (System.Text.RegularExpressions.Regex.IsMatch(dirName, recordingDirectoryPattern))
                    {
                        try
                        {
                            var info = new DirectoryInfo(dirPath);
                            var recordingInfo = new RecordingInfo
                            {
                                DirectoryName = dirName,
                                FullPath = dirPath,
                                CreationTime = info.CreationTime,
                                SizeBytes = CalculateDirectorySize(dirPath),
                                QuickHealth = QuickHealthCheck(dirPath),
                                DurationSeconds = QuickParseDuration(dirPath),
                                EpisodeMarkerCount = QuickParseEpisodeMarkerCount(dirPath)
                            };

                            recordings.Add(recordingInfo);
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingListManager: Error reading directory {dirName}: {e.Message}");
                        }
                    }
                }

                // Sort by creation time, newest first
                recordings = recordings.OrderByDescending(r => r.CreationTime).ToList();

                Debug.Log($"[{Constants.LOG_TAG}] RecordingListManager: Found {recordings.Count} recordings");
                OnRecordingsUpdated?.Invoke(recordings);
            }
            catch (Exception e)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] RecordingListManager: Error scanning recordings: {e.Message}");
                OnRecordingsUpdated?.Invoke(recordings);
            }
        }

        /// <summary>
        /// Calculates the total size of a directory in bytes.
        /// </summary>
        private long CalculateDirectorySize(string directoryPath)
        {
            long size = 0;
            try
            {
                var files = Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        size += fileInfo.Length;
                    }
                    catch
                    {
                        // Skip files we can't access
                    }
                }
            }
            catch
            {
                // Return 0 if we can't calculate size
            }
            return size;
        }

        [Serializable]
        private class VideoMetadataQuick
        {
            public long recording_start_unix_ms;
            public long recording_stop_unix_ms;
        }

        private static HealthLevel QuickHealthCheck(string dirPath)
        {
            // Check video exists and has reasonable size
            string videoPath = Path.Combine(dirPath, "left_camera.mp4");
            bool videoOk = File.Exists(videoPath);
            long videoSize = 0;
            if (videoOk)
            {
                try { videoSize = new FileInfo(videoPath).Length; } catch { }
            }

            if (!videoOk || videoSize < 1024)
                return HealthLevel.Error;

            // Check for any motion data
            bool hasMotion = false;
            string[] motionFiles = { "hmd_poses.csv", "imu.csv", "left_controller_poses.csv", "right_controller_poses.csv" };
            foreach (var f in motionFiles)
            {
                string p = Path.Combine(dirPath, f);
                if (File.Exists(p))
                {
                    try
                    {
                        if (new FileInfo(p).Length > 0)
                        {
                            hasMotion = true;
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (!hasMotion)
                return HealthLevel.Error;

            if (videoSize < 51200)
                return HealthLevel.Warning;

            return HealthLevel.Good;
        }

        private static double QuickParseDuration(string dirPath)
        {
            string metadataPath = Path.Combine(dirPath, "left_camera_metadata.json");
            if (!File.Exists(metadataPath))
                return -1;

            try
            {
                string json = File.ReadAllText(metadataPath);
                var metadata = JsonUtility.FromJson<VideoMetadataQuick>(json);
                if (metadata != null && metadata.recording_stop_unix_ms > metadata.recording_start_unix_ms)
                {
                    return (metadata.recording_stop_unix_ms - metadata.recording_start_unix_ms) / 1000.0;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingListManager: Failed to parse duration from {metadataPath}: {e.Message}");
            }

            return -1;
        }

        [Serializable]
        private class EpisodeMarkersQuick
        {
            public int marker_count;
        }

        private static int QuickParseEpisodeMarkerCount(string dirPath)
        {
            string markersPath = Path.Combine(dirPath, "episode_markers.json");
            if (!File.Exists(markersPath))
                return 0;

            try
            {
                string json = File.ReadAllText(markersPath);
                var data = JsonUtility.FromJson<EpisodeMarkersQuick>(json);
                return data?.marker_count ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private void Start()
        {
            RefreshRecordings();
        }
    }
}
