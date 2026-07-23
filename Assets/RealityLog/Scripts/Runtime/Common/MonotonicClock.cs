# nullable enable

using System.Diagnostics;

namespace RealityLog.Common
{
    /// <summary>
    /// The one process-wide monotonic clock, shared by the timesync endpoint and
    /// every recording logger so a host can map recorded samples to its own clock.
    ///
    /// Cristian's algorithm (POST /api/timesync/ping) measures the offset between
    /// this counter and the host's clock; the loggers stamp every row with the
    /// same counter (`mono_time_ns`). The host then computes
    /// host_time(sample) = mono_time_ns + measured_offset — a per-sample mapping
    /// that no longer drifts as the OVR render clock and wall clock diverge over
    /// an episode.
    ///
    /// Backed by a Stopwatch (CLOCK_MONOTONIC on Android), started once and
    /// thread-safe to read. The epoch is arbitrary and irrelevant: only the unit
    /// (nanoseconds) and consistency across readers matter. Earlier attempts used
    /// android.os.SystemClock.elapsedRealtimeNanos via Unity's AndroidJavaClass
    /// wrapper, which silently returned 0 on threadpool threads on Quest 3
    /// (Horizon OS 79); the Stopwatch has none of that off-main-thread hazard.
    /// </summary>
    public static class MonotonicClock
    {
        private static readonly Stopwatch s_stopwatch = Stopwatch.StartNew();

        private static readonly double s_nsPerTick =
            Stopwatch.Frequency > 0 ? 1_000_000_000.0 / Stopwatch.Frequency : 0.0;

        /// <summary>Monotonic nanoseconds since process start. Thread-safe.</summary>
        public static long Nanos()
        {
            if (s_nsPerTick <= 0.0) return 0;
            return (long)(s_stopwatch.ElapsedTicks * s_nsPerTick);
        }
    }
}
