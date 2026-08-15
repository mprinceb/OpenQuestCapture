# nullable enable

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RealityLog.Common
{
    /// <summary>
    /// The one process-wide monotonic clock, shared by the timesync endpoint and
    /// every recording logger so a host can map recorded samples to its own clock.
    ///
    /// Cristian's algorithm (POST /api/timesync/ping) measures the offset between
    /// this counter and the host's clock; the loggers stamp every row with the
    /// same counter (`mono_time_ns`). The host maps every sample through the
    /// episode's measured affine Quest-monotonic-to-host-monotonic fit.
    ///
    /// Android builds read CLOCK_MONOTONIC directly from bionic libc. This keeps
    /// the counter independent of Unity/IL2CPP Stopwatch scaling and works on the
    /// HTTP worker thread without JNI. The epoch is arbitrary and irrelevant:
    /// only the unit (nanoseconds) and consistency across readers matter.
    ///
    /// Non-Android/editor builds retain Stopwatch as a development fallback; they
    /// are never accepted by the managed hardware capture contract.
    /// </summary>
    public static class MonotonicClock
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        private const int ClockMonotonic = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct Timespec
        {
            public long Seconds;
            public long Nanoseconds;
        }

        [DllImport("libc", EntryPoint = "clock_gettime", SetLastError = true)]
        private static extern int ClockGetTime(int clockId, out Timespec value);
#else
        private static readonly double s_nsPerTick =
            Stopwatch.Frequency > 0 ? 1_000_000_000.0 / Stopwatch.Frequency : 0.0;
#endif

        /// <summary>Monotonic nanoseconds since process start. Thread-safe.</summary>
        public static long Nanos()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (ClockGetTime(ClockMonotonic, out var value) != 0)
            {
                throw new InvalidOperationException(
                    $"clock_gettime(CLOCK_MONOTONIC) failed with errno {Marshal.GetLastWin32Error()}"
                );
            }
            if (value.Seconds < 0 || value.Nanoseconds < 0 || value.Nanoseconds >= 1_000_000_000)
            {
                throw new InvalidOperationException("clock_gettime returned an invalid timespec");
            }
            return checked(value.Seconds * 1_000_000_000L + value.Nanoseconds);
#else
            if (s_nsPerTick <= 0.0) return 0;
            return (long)(Stopwatch.GetTimestamp() * s_nsPerTick);
#endif
        }
    }
}
