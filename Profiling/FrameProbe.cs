using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Morgott.Preloader.Core;
using UnityEngine.LowLevel;

namespace Morgott.Preloader.Profiling
{
    /// <summary>
    /// Opt-in with <c>[Profiler] TimeSpawnWindow</c>: Unity-side frame timing of the world load. A marker system is inserted
    /// at the start of every top-level phase of the Unity player loop (Initialization, EarlyUpdate, FixedUpdate, PreUpdate,
    /// Update, PreLateUpdate, PostLateUpdate) and one at the end of PostLateUpdate, so each frame from the world-start click
    /// to the first spawn is split into its phases: scripts' Update / LateUpdate / FixedUpdate, the async load integration in
    /// EarlyUpdate, and rendering + present in PostLateUpdate. A sync scene load runs inside the frame that requested it.
    /// One span per frame on the "frames" lane (detail = phase split + gen0 GC count), and a summary section with the
    /// longest frames, the hooked spans inside each of them and the unhooked remainder. Only the first marker of a phase
    /// per frame counts (FixedUpdate repeats its subsystems per fixed step). Nothing runs outside the window.
    /// </summary>
    internal static class FrameProbe
    {
        internal sealed class Frame
        {
            public long[] PhaseStart;
            public long End;
            public int Gc0;
            public int WorldPhase;
        }

        private const int MaxFrames = 20000;

        private static readonly List<Frame> Frames = new List<Frame>();
        private static string[] _phaseNames = new string[0];
        private static Frame _current;
        private static bool _open;
        private static bool _closed;
        private static int _lastGc0;

        public static bool Installed { get; private set; }

        public static IReadOnlyList<string> PhaseNames => _phaseNames;

        public static void Install()
        {
            PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
            PlayerLoopSystem[] phases = root.subSystemList;
            if (phases == null || phases.Length == 0)
            {
                Log.Warning("Frame probe: empty Unity player loop, frame timing off");
                return;
            }
            _phaseNames = phases.Select(p => p.type?.Name ?? "?").ToArray();
            for (int p = 0; p < phases.Length; p++)
            {
                int phase = p;
                var list = new List<PlayerLoopSystem>(phases[p].subSystemList ?? new PlayerLoopSystem[0]);
                list.Insert(0, new PlayerLoopSystem { type = typeof(FrameProbe), updateDelegate = () => OnPhase(phase) });
                if (p == phases.Length - 1)
                {
                    list.Add(new PlayerLoopSystem { type = typeof(FrameProbe), updateDelegate = OnFrameEnd });
                }
                phases[p].subSystemList = list.ToArray();
            }
            root.subSystemList = phases;
            PlayerLoop.SetPlayerLoop(root);
            Installed = true;
            GameLifecycleProbe.WorldLoadStarted += () => _open = !_closed;
            Log.Info("Frame probe: player loop markers in " + string.Join(", ", _phaseNames));
        }

        /// <summary>First spawn (main thread): stop recording and write one span per frame on the frames lane.</summary>
        public static void Close()
        {
            if (!_open)
            {
                return;
            }
            _open = false;
            _closed = true;
            if (_current != null)
            {
                _current.End = StartupTrace.Now();
                Frames.Add(_current);
                _current = null;
            }
            var totals = new long[GameLifecycleProbe.WorldPhaseNames.Length, _phaseNames.Length];
            var counts = new int[GameLifecycleProbe.WorldPhaseNames.Length];
            for (int i = 0; i < Frames.Count; i++)
            {
                Frame f = Frames[i];
                StartupTrace.Complete("frame", "frame " + i, Start(f), f.End, Split(f), GameLifecycleProbe.WorldPhaseNames[f.WorldPhase],
                    tid: StartupTrace.LaneFrames);
                counts[f.WorldPhase]++;
                for (int p = 0; p < _phaseNames.Length; p++)
                {
                    totals[f.WorldPhase, p] += PhaseTicks(f, p);
                }
            }
            if (Frames.Count == 0)
            {
                return;
            }
            // Per world phase x player-loop phase totals (aggregated lane: duration = total).
            long origin = Start(Frames[0]);
            for (int w = 0; w < counts.Length; w++)
            {
                for (int p = 0; p < _phaseNames.Length && counts[w] > 0; p++)
                {
                    StartupTrace.Complete("framephase", _phaseNames[p], origin, origin + totals[w, p],
                        counts[w] + " frames", GameLifecycleProbe.WorldPhaseNames[w], tid: StartupTrace.LaneSpawnWindow);
                }
            }
        }

        private static long PhaseTicks(Frame f, int p)
        {
            if (f.PhaseStart[p] == 0)
            {
                return 0;
            }
            for (int q = p + 1; q < f.PhaseStart.Length; q++)
            {
                if (f.PhaseStart[q] != 0)
                {
                    return f.PhaseStart[q] - f.PhaseStart[p];
                }
            }
            return f.End - f.PhaseStart[p];
        }

        private static void OnPhase(int phase)
        {
            if (!_open)
            {
                return;
            }
            long now = StartupTrace.Now();
            if (phase == 0 || _current == null)
            {
                if (_current != null)
                {
                    // PostLateUpdate end marker missing (another loop rewrite): close at the next frame start.
                    _current.End = now;
                    Add(_current);
                }
                _current = new Frame { PhaseStart = new long[_phaseNames.Length], WorldPhase = GameLifecycleProbe.WorldPhase };
            }
            if (_current.PhaseStart[phase] == 0)
            {
                _current.PhaseStart[phase] = now;
            }
        }

        private static void OnFrameEnd()
        {
            if (!_open || _current == null)
            {
                return;
            }
            _current.End = StartupTrace.Now();
            Add(_current);
            _current = null;
        }

        private static void Add(Frame f)
        {
            int gc0 = GC.CollectionCount(0);
            f.Gc0 = _lastGc0 == 0 ? 0 : gc0 - _lastGc0;
            _lastGc0 = gc0;
            if (Frames.Count < MaxFrames)
            {
                Frames.Add(f);
            }
        }

        private static long Start(Frame f)
        {
            foreach (long t in f.PhaseStart)
            {
                if (t != 0)
                {
                    return t;
                }
            }
            return f.End;
        }

        /// <summary>"EarlyUpdate 1.2, Update 30.1, ..., gc0 +1": ms per phase, from its first marker to the next phase's.</summary>
        internal static string Split(Frame f)
        {
            var sb = new StringBuilder();
            for (int p = 0; p < f.PhaseStart.Length; p++)
            {
                double ms = StartupTrace.DurMs(0, PhaseTicks(f, p));
                if (ms >= 0.5)
                {
                    sb.Append(sb.Length > 0 ? ", " : "").Append(_phaseNames[p]).Append(' ').Append(ms.ToString("F1", CultureInfo.InvariantCulture));
                }
            }
            if (f.Gc0 > 0)
            {
                sb.Append(sb.Length > 0 ? ", " : "").Append("gc0 +").Append(f.Gc0);
            }
            return sb.ToString();
        }
    }
}
