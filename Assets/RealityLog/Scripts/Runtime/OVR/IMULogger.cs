# nullable enable

using System;
using System.IO;
using UnityEngine;
using RealityLog.Common;
using RealityLog.IO;

namespace RealityLog.OVR
{
    /// <summary>
    /// Logs head/controller IMU data (acceleration, gyroscope, velocity) to CSV.
    /// <para><b>Acceleration values:</b> The OVR runtime returns gravity-compensated
    /// (linear) acceleration — gravity has already been subtracted. The CSV columns
    /// are labelled <c>linear_acc_*</c> to reflect this.</para>
    /// </summary>
    public class IMULogger : MonoBehaviour
    {
        private static readonly string[] HEADER = new string[]
            {
                "unix_time", "ovr_timestamp", "mono_time_ns",
                "linear_acc_x", "linear_acc_y", "linear_acc_z",
                "gyro_x", "gyro_y", "gyro_z",
                "vel_x", "vel_y", "vel_z",
                "ang_acc_x", "ang_acc_y", "ang_acc_z"
            };

        [SerializeField] private OVRPlugin.Node node = OVRPlugin.Node.Head;
        [SerializeField] private string fileName = "imu.csv";
        [SerializeField] private string directoryName = "";
        [SerializeField] private bool startLoggingOnStart = false;
        [SerializeField] private int samplingRateHz = 200;

        private CsvWriter? writer = null;
        private System.Threading.Thread? samplingThread;
        private bool isSampling = false;

        private double baseOvrTimeSec;
        private long baseUnixTimeMs;
        
        // State for numerical differentiation
        private OVRPlugin.Vector3f prevVelocity;
        private OVRPlugin.Vector3f prevAngularVelocity;
        private double prevTimestamp;

        private bool IsZero(OVRPlugin.Vector3f v)
        {
            return v.x == 0 && v.y == 0 && v.z == 0;
        }

        private OVRPlugin.Vector3f CalculateDerivative(OVRPlugin.Vector3f current, OVRPlugin.Vector3f prev, double dt)
        {
            float fDt = (float)dt;
            return new OVRPlugin.Vector3f
            {
                x = (current.x - prev.x) / fDt,
                y = (current.y - prev.y) / fDt,
                z = (current.z - prev.z) / fDt
            };
        }

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
                prevVelocity = default;
                prevAngularVelocity = default;
                prevTimestamp = 0;
                
                Debug.Log($"[{Constants.LOG_TAG}] {fileName} - Starting IMU logging thread. Reset base times: OVR={baseOvrTimeSec:F3}s, Unix={baseUnixTimeMs}ms");
                
                var filePath = Path.Combine(Application.persistentDataPath, DirectoryName, fileName);
                writer = new CsvWriter(filePath, HEADER);

                isSampling = true;
                samplingThread = new System.Threading.Thread(SamplingLoop);
                samplingThread.Priority = System.Threading.ThreadPriority.Highest;
                samplingThread.Start();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] Failed to create IMU CsvWriter: {ex.Message}");
                writer = null;
            }
        }

        public void StopLogging()
        {
            isSampling = false;
            if (samplingThread != null)
            {
                if (!samplingThread.Join(200))
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] IMU sampling thread did not stop gracefully.");
                }
                samplingThread = null;
            }

            try
            {
                writer?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] Failed to dispose IMU CsvWriter: {ex.Message}");
            }

            writer = null;
        }

        private void Start()
        {
            baseOvrTimeSec = OVRPlugin.GetTimeInSeconds();
            baseUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (startLoggingOnStart)
            {
                StartLogging();
            }
        }

        private void SamplingLoop()
        {
            int sleepTimeMs = System.Math.Max(1, 1000 / samplingRateHz);
            
            while (isSampling)
            {
                if (writer == null)
                {
                    System.Threading.Thread.Sleep(100);
                    continue;
                }

                // OVRPlugin.GetNodePoseStateImmediate is generally thread-safe as it's a native call wrapper
                var poseState = OVRPlugin.GetNodePoseStateImmediate(node);
                var timestamp = poseState.Time;

                if (timestamp > latestTimestamp)
                {
                    latestTimestamp = timestamp;

                    var acc = poseState.Acceleration;
                    var gyro = poseState.AngularVelocity;
                    var vel = poseState.Velocity;
                    var angAcc = poseState.AngularAcceleration;

                    // If raw acceleration data is missing (OpenXR), calculate it from velocity
                    if (IsZero(acc) && prevTimestamp > 0)
                    {
                        double dt = timestamp - prevTimestamp;
                        if (dt > 0.0001) // Avoid division by zero
                        {
                            acc = CalculateDerivative(vel, prevVelocity, dt);
                            angAcc = CalculateDerivative(gyro, prevAngularVelocity, dt);
                        }
                    }

                    prevVelocity = vel;
                    prevAngularVelocity = gyro;
                    prevTimestamp = timestamp;

                    writer.EnqueueRow(
                        ConvertOvrSecToUnixTimeMs(timestamp), timestamp, MonotonicClock.Nanos(),
                        acc.x, acc.y, acc.z,
                        gyro.x, gyro.y, gyro.z,
                        vel.x, vel.y, vel.z,
                        angAcc.x, angAcc.y, angAcc.z
                    );
                }

                System.Threading.Thread.Sleep(sleepTimeMs);
            }
        }

        private long ConvertOvrSecToUnixTimeMs(double ovrTime)
        {
            var deltaSec = ovrTime - baseOvrTimeSec;
            var deltaMs = (long) (deltaSec * 1000.0);
            return baseUnixTimeMs + deltaMs;
        }

        private void OnDestroy()
        {
            StopLogging();
        }
    }
}
