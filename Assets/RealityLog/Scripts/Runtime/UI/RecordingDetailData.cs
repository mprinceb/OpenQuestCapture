#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace RealityLog.UI
{
    public enum HealthLevel
    {
        Good,
        Warning,
        Error
    }

    [Serializable]
    public class RecordingFileInfo
    {
        public string FileName;
        public bool Exists;
        public long SizeBytes;
        public HealthLevel Health;
        public string Issue;

        public RecordingFileInfo(string fileName, bool exists, long sizeBytes, HealthLevel health, string issue = "")
        {
            FileName = fileName;
            Exists = exists;
            SizeBytes = sizeBytes;
            Health = health;
            Issue = issue;
        }

        public string FormattedSize
        {
            get
            {
                if (!Exists) return "missing";
                if (SizeBytes == 0) return "0 B";
                string[] sizes = { "B", "KB", "MB", "GB" };
                double len = SizeBytes;
                int order = 0;
                while (len >= 1024 && order < sizes.Length - 1)
                {
                    order++;
                    len /= 1024;
                }
                return $"{len:0.##} {sizes[order]}";
            }
        }
    }

    public class RecordingDetailData
    {
        public string DirectoryPath { get; private set; } = string.Empty;
        public List<RecordingFileInfo> Files { get; private set; } = new List<RecordingFileInfo>();
        public double DurationSeconds { get; private set; } = -1;
        public int ConfiguredFps { get; private set; } = 30;
        public HealthLevel OverallHealth { get; private set; } = HealthLevel.Good;
        public List<string> Issues { get; private set; } = new List<string>();

        public string FormattedDuration
        {
            get
            {
                if (DurationSeconds < 0) return "Unknown";
                int totalSec = (int)DurationSeconds;
                int min = totalSec / 60;
                int sec = totalSec % 60;
                return $"{min:D2}:{sec:D2} @ {ConfiguredFps}fps";
            }
        }

        // Known required files
        private static readonly string[] RequiredFiles = new[]
        {
            "center_camera.mp4",
            "hmd_poses.csv",
            "video_metadata.json"
        };

        // Secondary camera streams — listed only when that camera was recording
        private static readonly string[] OptionalVideoFiles = new[]
        {
            "right_camera.mp4"
        };

        // Known optional CSV files
        private static readonly string[] OptionalCsvFiles = new[]
        {
            "imu.csv",
            "left_controller_poses.csv",
            "right_controller_poses.csv",
            "left_depth_descriptors.csv",
            "right_depth_descriptors.csv"
        };

        // Known optional JSON files
        private static readonly string[] OptionalJsonFiles = new[]
        {
            "left_camera_characteristics.json",
            "right_camera_characteristics.json",
            "left_camera_image_format.json",
            "right_camera_metadata.json",
            "episode_markers.json"
        };

        // Known optional directories
        private static readonly string[] OptionalDirs = new[]
        {
            "left_camera_raw",
            "right_camera_raw",
            "left_depth",
            "right_depth"
        };

        [Serializable]
        private class VideoMetadataJson
        {
            public long recording_start_unix_ms;
            public long recording_stop_unix_ms;
            public int configured_fps;
            public string video_file = string.Empty;
        }

        public static RecordingDetailData Parse(string fullPath)
        {
            var data = new RecordingDetailData { DirectoryPath = fullPath };

            // Parse video metadata for duration
            string metadataPath = Path.Combine(fullPath, "video_metadata.json");
            if (File.Exists(metadataPath))
            {
                try
                {
                    string json = File.ReadAllText(metadataPath);
                    var metadata = JsonUtility.FromJson<VideoMetadataJson>(json);
                    if (metadata != null && metadata.recording_stop_unix_ms > metadata.recording_start_unix_ms)
                    {
                        data.DurationSeconds = (metadata.recording_stop_unix_ms - metadata.recording_start_unix_ms) / 1000.0;
                        data.ConfiguredFps = metadata.configured_fps > 0 ? metadata.configured_fps : 30;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[RealityLog] RecordingDetailData: Failed to parse video_metadata.json: {e.Message}");
                }
            }

            // Check required files
            foreach (string fileName in RequiredFiles)
            {
                string filePath = Path.Combine(fullPath, fileName);
                data.Files.Add(CheckFile(filePath, fileName, required: true));
            }

            // Check secondary camera streams
            foreach (string fileName in OptionalVideoFiles)
            {
                string filePath = Path.Combine(fullPath, fileName);
                if (File.Exists(filePath))
                {
                    data.Files.Add(CheckFile(filePath, fileName, required: false));
                }
            }

            // Check optional CSV files
            foreach (string fileName in OptionalCsvFiles)
            {
                string filePath = Path.Combine(fullPath, fileName);
                if (File.Exists(filePath))
                {
                    data.Files.Add(CheckFile(filePath, fileName, required: false));
                }
            }

            // Check optional JSON files
            foreach (string fileName in OptionalJsonFiles)
            {
                string filePath = Path.Combine(fullPath, fileName);
                if (File.Exists(filePath))
                {
                    data.Files.Add(CheckFile(filePath, fileName, required: false));
                }
            }

            // Check optional directories
            foreach (string dirName in OptionalDirs)
            {
                string dirPath = Path.Combine(fullPath, dirName);
                if (Directory.Exists(dirPath))
                {
                    int fileCount = 0;
                    long totalSize = 0;
                    try
                    {
                        var files = Directory.GetFiles(dirPath);
                        fileCount = files.Length;
                        foreach (var f in files)
                        {
                            try { totalSize += new FileInfo(f).Length; } catch { }
                        }
                    }
                    catch { }

                    var health = HealthLevel.Good;
                    string issue = "";
                    if (fileCount == 0)
                    {
                        health = HealthLevel.Warning;
                        issue = "Directory exists but is empty";
                    }

                    string label = $"{dirName}/ ({fileCount} files)";
                    data.Files.Add(new RecordingFileInfo(label, true, totalSize, health, issue));
                }
            }

            // Health rules
            bool hasMotionData = false;
            foreach (var file in data.Files)
            {
                if (file.FileName == "hmd_poses.csv" && file.Exists && file.SizeBytes > 0)
                    hasMotionData = true;
                if (file.FileName == "imu.csv" && file.Exists && file.SizeBytes > 0)
                    hasMotionData = true;
                if ((file.FileName == "left_controller_poses.csv" || file.FileName == "right_controller_poses.csv")
                    && file.Exists && file.SizeBytes > 0)
                    hasMotionData = true;
            }

            if (!hasMotionData)
            {
                data.Issues.Add("No motion data found");
                data.RaiseHealth(HealthLevel.Error);
            }

            if (data.DurationSeconds >= 0 && data.DurationSeconds < 1.0)
            {
                data.Issues.Add("Recording duration < 1 second");
                data.RaiseHealth(HealthLevel.Warning);
            }

            // Roll up file-level issues
            foreach (var file in data.Files)
            {
                if (file.Health == HealthLevel.Error)
                {
                    data.Issues.Add($"{file.FileName}: {(string.IsNullOrEmpty(file.Issue) ? "Error" : file.Issue)}");
                    data.RaiseHealth(HealthLevel.Error);
                }
                else if (file.Health == HealthLevel.Warning)
                {
                    data.Issues.Add($"{file.FileName}: {(string.IsNullOrEmpty(file.Issue) ? "Warning" : file.Issue)}");
                    data.RaiseHealth(HealthLevel.Warning);
                }
            }

            return data;
        }

        private static RecordingFileInfo CheckFile(string filePath, string fileName, bool required)
        {
            bool exists = File.Exists(filePath);
            long size = 0;
            if (exists)
            {
                try { size = new FileInfo(filePath).Length; } catch { }
            }

            var health = HealthLevel.Good;
            string issue = "";

            if (fileName.EndsWith(".mp4"))
            {
                if (!exists || size < 1024)
                {
                    health = HealthLevel.Error;
                    issue = !exists ? "Video file missing" : "Video file too small (<1KB)";
                }
                else if (size < 51200)
                {
                    health = HealthLevel.Warning;
                    issue = "Video file suspiciously small (<50KB)";
                }
            }
            else if (fileName == "hmd_poses.csv")
            {
                if (!exists || size == 0)
                {
                    health = HealthLevel.Error;
                    issue = !exists ? "HMD pose data missing" : "HMD pose data empty";
                }
            }
            else if (fileName.EndsWith(".csv"))
            {
                if (exists && size == 0)
                {
                    health = HealthLevel.Warning;
                    issue = "CSV file is empty";
                }
            }
            else if (fileName == "video_metadata.json")
            {
                if (!exists)
                {
                    health = HealthLevel.Warning;
                    issue = "Metadata missing — duration unknown";
                }
            }

            return new RecordingFileInfo(fileName, exists, size, health, issue);
        }

        private void RaiseHealth(HealthLevel level)
        {
            if (level > OverallHealth)
                OverallHealth = level;
        }
    }
}
