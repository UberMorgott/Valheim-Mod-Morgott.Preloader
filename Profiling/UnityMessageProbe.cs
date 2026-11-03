using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Morgott.Preloader.Core;
using HarmonyLib;
using UnityEngine;

namespace Morgott.Preloader.Profiling
{
    /// <summary>
    /// Opt-in with <c>[Profiler] TimeSpawnWindow</c>: every <c>Awake</c> / <c>Start</c> declared by a MonoBehaviour of
    /// assembly_valheim (the main scene's singletons: ZNet, ZNetScene, ZoneSystem, DungeonDB, ClutterSystem, Hud, ...,
    /// and the per-object ones: ZNetView, Piece, Character ... of everything instantiated before the spawn) gets a
    /// First prefix / Last finalizer pair. Calls, inclusive and self time (nested hooked Awake/Start subtracted) are
    /// aggregated per method and world-load phase (<see cref="GameLifecycleProbe.WorldPhase"/>: freeze, scene loaded ->
    /// first frame, first frame -> spawn); mods' patches on these methods are inside. Written at the first spawn as one
    /// span per (phase, method) on their own lane (duration = self total) and as a summary section. Main thread only;
    /// outside phases 1-3 the hooks only push/pop.
    /// </summary>
    internal static class UnityMessageProbe
    {
        private sealed class Agg
        {
            public long Calls;
            public long Incl;
            public long Self;
        }

        private struct Open
        {
            public long Start;
            public long Child;
        }

        private static readonly Dictionary<MethodBase, Agg[]> Methods = new Dictionary<MethodBase, Agg[]>();
        private static readonly List<Open> Stack = new List<Open>();
        private static int _mainThread;

        public static bool Installed { get; private set; }

        public static void Install(Harmony harmony)
        {
            _mainThread = Environment.CurrentManagedThreadId;
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(UnityMessageProbe), nameof(Prefix)), Priority.First);
            var finalizer = new HarmonyMethod(AccessTools.Method(typeof(UnityMessageProbe), nameof(Finalizer)), Priority.Last);
            int hooked = 0;
            int failed = 0;
            foreach (MethodInfo method in Targets())
            {
                try
                {
                    harmony.Patch(method, prefix: prefix, finalizer: finalizer);
                    Methods[method] = new Agg[GameLifecycleProbe.WorldPhaseNames.Length];
                    hooked++;
                }
                catch (Exception e)
                {
                    failed++;
                    Log.WarningOnce("unity-message-probe:" + method.DeclaringType?.Name + "." + method.Name,
                        $"Unity message probe: cannot hook {method.DeclaringType?.Name}.{method.Name}: {e.Message}");
                }
            }
            Installed = true;
            Log.Info($"Unity message probe: {hooked} Awake/Start methods of assembly_valheim hooked ({failed} failed)");
        }

        private static IEnumerable<MethodInfo> Targets()
        {
            Type[] types;
            try
            {
                types = typeof(ZNet).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t != null).ToArray();
            }
            foreach (Type type in types)
            {
                if (!typeof(MonoBehaviour).IsAssignableFrom(type) || type.ContainsGenericParameters)
                {
                    continue;
                }
                foreach (string name in new[] { "Awake", "Start" })
                {
                    MethodInfo method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                        null, Type.EmptyTypes, null);
                    if (method != null && !method.IsAbstract && method.GetMethodBody() != null)
                    {
                        yield return method;
                    }
                }
            }
        }

        private static void Prefix()
        {
            if (Environment.CurrentManagedThreadId != _mainThread)
            {
                return;
            }
            Stack.Add(new Open { Start = StartupTrace.Recording ? StartupTrace.Now() : 0 });
        }

        private static void Finalizer(MethodBase __originalMethod)
        {
            if (Environment.CurrentManagedThreadId != _mainThread || Stack.Count == 0)
            {
                return;
            }
            Open open = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            if (open.Start == 0)
            {
                return;
            }
            long incl = StartupTrace.Now() - open.Start;
            if (Stack.Count > 0)
            {
                Open parent = Stack[Stack.Count - 1];
                parent.Child += incl;
                Stack[Stack.Count - 1] = parent;
            }
            int phase = GameLifecycleProbe.WorldPhase;
            if (phase < 1 || phase > 3 || !Methods.TryGetValue(__originalMethod, out Agg[] aggs))
            {
                return;
            }
            Agg agg = aggs[phase] ?? (aggs[phase] = new Agg());
            agg.Calls++;
            agg.Incl += incl;
            agg.Self += incl - open.Child;
        }

        /// <summary>First spawn (main thread): one span per (phase, method), duration = self total.</summary>
        public static void Close()
        {
            if (!Installed)
            {
                return;
            }
            long origin = StartupTrace.Now();
            foreach (KeyValuePair<MethodBase, Agg[]> kv in Methods)
            {
                for (int phase = 1; phase <= 3; phase++)
                {
                    Agg agg = kv.Value[phase];
                    if (agg == null)
                    {
                        continue;
                    }
                    StartupTrace.Complete("unitymsg", kv.Key.DeclaringType?.Name + "." + kv.Key.Name, origin, origin + agg.Self,
                        string.Format(CultureInfo.InvariantCulture, "{0} calls, self {1:F1} ms, incl {2:F1} ms", agg.Calls,
                            StartupTrace.DurMs(0, agg.Self), StartupTrace.DurMs(0, agg.Incl)),
                        GameLifecycleProbe.WorldPhaseNames[phase], tid: StartupTrace.LaneUnityMessages);
                }
            }
        }
    }
}
