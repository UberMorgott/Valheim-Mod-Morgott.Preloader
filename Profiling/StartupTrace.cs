using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Morgott.Preloader.Profiling
{
    /// <summary>One recorded event. Times are raw <see cref="Stopwatch.GetTimestamp"/> ticks.</summary>
    internal sealed class TraceEvent
    {
        public string Cat, Name, Detail, Detail2;
        public char Ph; // 'X' = complete span, 'i' = instant
        public long Start, End;
        public int Tid;
        public long Bytes = -1;
        public int Depth; // harmony nesting depth at record time (1 = outermost Harmony API call)
        public string Args; // preformatted JSON object body for rare events (GC snapshots)
    }

    /// <summary>
    /// In-memory monotonic startup trace. Recording is cheap (one lock + one allocation per event) and stops at
    /// the first player spawn (a snapshot is exported at main-menu ready); exports happen off the hot path. Nothing is logged per event.
    /// </summary>
    internal static class StartupTrace
    {
        /// <summary>Synthetic thread ids for async work that does not nest on a real thread.</summary>
        public const int LaneScenes = 1000;
        public const int LaneAsyncBundles = 1001;
        public const int LaneWorldLoad = 1002;
        public const int LaneWorldGen = 1003;

        private static readonly object Lock = new object();
        private static readonly List<TraceEvent> Events = new List<TraceEvent>(16384);

        public static volatile bool Recording;

        /// <summary>Tick value of process start (estimated from <see cref="Process.StartTime"/>).</summary>
        public static long ProcessStart { get; private set; }

        public static bool ProcessStartKnown { get; private set; }

        public static int MainThreadId { get; private set; }

        public static long Now() => Stopwatch.GetTimestamp();

        public static double ToMs(long ticks) => (ticks - ProcessStart) * 1000.0 / Stopwatch.Frequency;

        public static double DurMs(long start, long end) => (end - start) * 1000.0 / Stopwatch.Frequency;

        public static void Start()
        {
            long now = Now();
            ProcessStart = now;
            try
            {
                double sinceStartMs = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
                if (sinceStartMs > 0)
                {
                    ProcessStart = now - (long)(sinceStartMs * Stopwatch.Frequency / 1000.0);
                    ProcessStartKnown = true;
                }
            }
            catch (Exception)
            {
                // Process start unknown: times are then relative to Morgott.Preloader.Initialize.
            }
            MainThreadId = Environment.CurrentManagedThreadId;
            Recording = true;
            if (ProcessStartKnown)
                Complete("lifecycle", "process start -> Morgott.Preloader.Initialize", ProcessStart, now,
                    "Unity boot, Doorstop, BepInEx preloader start, Cecil read of all Managed assemblies");
            Mark("patcher.Initialize");
        }

        public static void Complete(string cat, string name, long start, long end, string detail = null,
            string detail2 = null, long bytes = -1, int tid = -1, int depth = 0)
        {
            if (!Recording) return;
            Add(new TraceEvent
            {
                Cat = cat,
                Name = name,
                Ph = 'X',
                Start = start,
                End = end,
                Tid = tid >= 0 ? tid : Environment.CurrentManagedThreadId,
                Detail = detail,
                Detail2 = detail2,
                Bytes = bytes,
                Depth = depth,
            });
        }

        private static void Add(TraceEvent e)
        {
            lock (Lock)
            {
                Events.Add(e);
            }
        }

        /// <summary>Lifecycle boundary: an instant with GC collection counts and managed heap size.</summary>
        public static void Mark(string name, string detail = null)
        {
            if (!Recording) return;
            long now = Now();
            Add(new TraceEvent
            {
                Cat = "mark",
                Name = name,
                Ph = 'i',
                Start = now,
                End = now,
                Tid = Environment.CurrentManagedThreadId,
                Detail = detail,
                Args = string.Format(CultureInfo.InvariantCulture, "\"gc0\":{0},\"gc1\":{1},\"gc2\":{2},\"heapMB\":{3:F1}",
                    GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalMemory(false) / (1024.0 * 1024.0)),
            });
        }

        /// <summary>Stops recording and returns everything recorded so far.</summary>
        public static List<TraceEvent> StopAndSnapshot()
        {
            Recording = false;
            return Snapshot();
        }

        /// <summary>Copy of everything recorded so far; recording goes on.</summary>
        public static List<TraceEvent> Snapshot()
        {
            lock (Lock)
            {
                return new List<TraceEvent>(Events);
            }
        }
    }
}
