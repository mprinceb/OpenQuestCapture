#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using RealityLog.Common;
using RealityLog.IO;

namespace RealityLog.OVR
{
    /// <summary>
    /// Logs full body tracking skeleton data to CSV during recording sessions.
    /// Uses OVRPlugin.GetBodyState4 with FullBody joint set (84 joints).
    /// Each row contains a timestamp and all joint positions/orientations.
    ///
    /// <para><b>Timing:</b> Polled every FixedUpdate (~50 Hz), but the OVR body tracking
    /// runtime delivers new poses at ~30 Hz. Duplicate timestamps are filtered, so the
    /// effective CSV output rate is ~30 Hz with slight jitter. Gaps of 580 ms or more
    /// indicate tracking loss (e.g. hands out of view), not timing jitter.</para>
    ///
    /// <para><b>Root joint (index 0):</b> Floor-projected origin (Y ≈ 0). For actual
    /// pelvis height, use Hips (index 1).</para>
    ///
    /// <para><b>Joint names:</b> Hardcoded from the XR_META_body_tracking_full_body spec
    /// to avoid an assembly dependency on the Interaction SDK (BodyJointId enum).</para>
    /// </summary>
    public class BodyTrackingLogger : MonoBehaviour
    {
        private static readonly OVRPlugin.BodyJointSet JOINT_SET = OVRPlugin.BodyJointSet.FullBody;
        private const int FULL_BODY_JOINT_COUNT = 84;
        // 7 values per joint (pos xyz + rot xyzw)
        private const int VALUES_PER_JOINT = 7;

        /// <summary>
        /// Full-body joint names matching XR_META_body_tracking_full_body / BodyJointId enum.
        /// Index 0 = Root (floor-projected, Y≈0; use Hips for pelvis height).
        /// </summary>
        private static readonly string[] JOINT_NAMES = new string[]
        {
            "Root",                          // 0
            "Hips",                          // 1
            "SpineLower",                    // 2
            "SpineMiddle",                   // 3
            "SpineUpper",                    // 4
            "Chest",                         // 5
            "Neck",                          // 6
            "Head",                          // 7
            "LeftShoulder",                  // 8
            "LeftScapula",                   // 9
            "LeftArmUpper",                  // 10
            "LeftArmLower",                  // 11
            "LeftHandWristTwist",            // 12
            "RightShoulder",                 // 13
            "RightScapula",                  // 14
            "RightArmUpper",                 // 15
            "RightArmLower",                 // 16
            "RightHandWristTwist",           // 17
            "LeftHandPalm",                  // 18
            "LeftHandWrist",                 // 19
            "LeftHandThumbMetacarpal",       // 20
            "LeftHandThumbProximal",         // 21
            "LeftHandThumbDistal",           // 22
            "LeftHandThumbTip",              // 23
            "LeftHandIndexMetacarpal",       // 24
            "LeftHandIndexProximal",         // 25
            "LeftHandIndexIntermediate",     // 26
            "LeftHandIndexDistal",           // 27
            "LeftHandIndexTip",              // 28
            "LeftHandMiddleMetacarpal",      // 29
            "LeftHandMiddleProximal",        // 30
            "LeftHandMiddleIntermediate",    // 31
            "LeftHandMiddleDistal",          // 32
            "LeftHandMiddleTip",             // 33
            "LeftHandRingMetacarpal",        // 34
            "LeftHandRingProximal",          // 35
            "LeftHandRingIntermediate",      // 36
            "LeftHandRingDistal",            // 37
            "LeftHandRingTip",               // 38
            "LeftHandLittleMetacarpal",      // 39
            "LeftHandLittleProximal",        // 40
            "LeftHandLittleIntermediate",    // 41
            "LeftHandLittleDistal",          // 42
            "LeftHandLittleTip",             // 43
            "RightHandPalm",                 // 44
            "RightHandWrist",                // 45
            "RightHandThumbMetacarpal",      // 46
            "RightHandThumbProximal",        // 47
            "RightHandThumbDistal",          // 48
            "RightHandThumbTip",             // 49
            "RightHandIndexMetacarpal",      // 50
            "RightHandIndexProximal",        // 51
            "RightHandIndexIntermediate",    // 52
            "RightHandIndexDistal",          // 53
            "RightHandIndexTip",             // 54
            "RightHandMiddleMetacarpal",     // 55
            "RightHandMiddleProximal",       // 56
            "RightHandMiddleIntermediate",   // 57
            "RightHandMiddleDistal",         // 58
            "RightHandMiddleTip",            // 59
            "RightHandRingMetacarpal",       // 60
            "RightHandRingProximal",         // 61
            "RightHandRingIntermediate",     // 62
            "RightHandRingDistal",           // 63
            "RightHandRingTip",              // 64
            "RightHandLittleMetacarpal",     // 65
            "RightHandLittleProximal",       // 66
            "RightHandLittleIntermediate",   // 67
            "RightHandLittleDistal",         // 68
            "RightHandLittleTip",            // 69
            "LeftUpperLeg",                  // 70
            "LeftLowerLeg",                  // 71
            "LeftFootAnkleTwist",            // 72
            "LeftFootAnkle",                 // 73
            "LeftFootSubtalar",              // 74
            "LeftFootTransverse",            // 75
            "LeftFootBall",                  // 76
            "RightUpperLeg",                 // 77
            "RightLowerLeg",                 // 78
            "RightFootAnkleTwist",           // 79
            "RightFootAnkle",                // 80
            "RightFootSubtalar",             // 81
            "RightFootTransverse",           // 82
            "RightFootBall",                 // 83
        };

        [SerializeField] private string fileName = "body_tracking.csv";
        [SerializeField] private string directoryName = "";
        [SerializeField] private bool startLoggingOnStart = false;

        private CsvWriter? writer = null;
        private OVRPlugin.BodyState bodyState;
        private bool bodyTrackingStarted = false;

        private double baseOvrTimeSec;
        private long baseUnixTimeMs;
        private double latestTimestamp;

        public string DirectoryName
        {
            get => directoryName;
            set => directoryName = value;
        }

        public void StartLogging()
        {
            try
            {
                StopLogging();

                baseOvrTimeSec = OVRPlugin.GetTimeInSeconds();
                baseUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                latestTimestamp = 0;

                Debug.Log($"[{Constants.LOG_TAG}] {fileName} - Reset base times: OVR={baseOvrTimeSec:F3}s, Unix={baseUnixTimeMs}ms");

                if (!bodyTrackingStarted)
                {
                    bodyTrackingStarted = OVRPlugin.StartBodyTracking2(JOINT_SET);
                    if (!bodyTrackingStarted)
                    {
                        Debug.LogError($"[{Constants.LOG_TAG}] BodyTrackingLogger - Failed to start body tracking");
                        return;
                    }
                    OVRPlugin.RequestBodyTrackingFidelity(OVRPlugin.BodyTrackingFidelity2.High);
                    Debug.Log($"[{Constants.LOG_TAG}] BodyTrackingLogger - Body tracking started (FullBody, High fidelity)");
                }

                var filePath = Path.Combine(Application.persistentDataPath, DirectoryName, fileName);
                writer = new CsvWriter(filePath, BuildHeader());
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] BodyTrackingLogger - Failed to start: {ex.Message}");
                writer = null;
            }
        }

        public void StopLogging()
        {
            try
            {
                writer?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] BodyTrackingLogger - Failed to dispose writer: {ex.Message}");
            }
            writer = null;
        }

        private void Start()
        {
            baseOvrTimeSec = OVRPlugin.GetTimeInSeconds();
            baseUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            bodyState = new OVRPlugin.BodyState
            {
                JointLocations = new OVRPlugin.BodyJointLocation[FULL_BODY_JOINT_COUNT]
            };

            if (startLoggingOnStart)
            {
                StartLogging();
            }
        }

        private void FixedUpdate()
        {
            if (writer == null)
                return;

            if (!OVRPlugin.GetBodyState4(OVRPlugin.Step.Render, JOINT_SET, ref bodyState))
                return;

            var timestamp = bodyState.Time;
            if (timestamp <= latestTimestamp)
                return;

            latestTimestamp = timestamp;

            var joints = bodyState.JointLocations;
            if (joints == null || joints.Length == 0)
                return;

            int jointCount = Mathf.Min(joints.Length, FULL_BODY_JOINT_COUNT);

            // Build row: unix_time, ovr_timestamp, mono_time_ns, confidence,
            // calibration_status, fidelity, then per-joint data
            var row = new double[6 + jointCount * VALUES_PER_JOINT];
            row[0] = ConvertOvrSecToUnixTimeMs(timestamp);
            row[1] = timestamp;
            row[2] = MonotonicClock.Nanos();
            row[3] = bodyState.Confidence;
            row[4] = (double)bodyState.CalibrationStatus;
            row[5] = (double)bodyState.Fidelity;

            int offset = 6;
            for (int i = 0; i < jointCount; i++)
            {
                var joint = joints[i];
                var pose = joint.Pose;
                row[offset + 0] = pose.Position.x;
                row[offset + 1] = pose.Position.y;
                row[offset + 2] = pose.Position.z;
                row[offset + 3] = pose.Orientation.x;
                row[offset + 4] = pose.Orientation.y;
                row[offset + 5] = pose.Orientation.z;
                row[offset + 6] = pose.Orientation.w;
                offset += VALUES_PER_JOINT;
            }

            writer.EnqueueRow(row);
        }

        private string[] BuildHeader()
        {
            var header = new List<string>
            {
                "unix_time", "ovr_timestamp", "mono_time_ns", "confidence", "calibration_status", "fidelity"
            };

            for (int i = 0; i < FULL_BODY_JOINT_COUNT; i++)
            {
                string jointName = GetJointName(i);
                header.Add($"{jointName}_pos_x");
                header.Add($"{jointName}_pos_y");
                header.Add($"{jointName}_pos_z");
                header.Add($"{jointName}_rot_x");
                header.Add($"{jointName}_rot_y");
                header.Add($"{jointName}_rot_z");
                header.Add($"{jointName}_rot_w");
            }

            return header.ToArray();
        }

        private static string GetJointName(int index)
        {
            if (index >= 0 && index < JOINT_NAMES.Length)
            {
                return JOINT_NAMES[index];
            }
            return $"Joint_{index}";
        }

        private long ConvertOvrSecToUnixTimeMs(double ovrTime)
        {
            var deltaSec = ovrTime - baseOvrTimeSec;
            var deltaMs = (long)(deltaSec * 1000.0);
            return baseUnixTimeMs + deltaMs;
        }

        private void OnDestroy()
        {
            if (bodyTrackingStarted)
            {
                OVRPlugin.StopBodyTracking();
                bodyTrackingStarted = false;
            }
            writer?.Dispose();
            writer = null;
        }
    }
}
