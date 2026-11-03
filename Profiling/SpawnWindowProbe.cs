using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Morgott.Preloader.Core;
using HarmonyLib;

namespace Morgott.Preloader.Profiling
{
    /// <summary>
    /// Opt-in (<c>[Profiler] TimeSpawnWindow</c>): the methods that run between the main scene request and the first
    /// player spawn, most of them every frame (zone creation, object creation, terrain readiness, spawn point search,
    /// teleport). One trace event per call would flood the trace, so each method is aggregated (calls, total, max)
    /// inside the window = <c>FejdStartup.LoadMainScene</c> end -> end of the first <c>Game.SpawnPlayer</c>; the totals go
    /// into the trace as one span per method on their own lane (category <c>spawnwindow</c>) and into a summary section.
    /// Rare one-shot calls (dungeon load, SnappAll, ForceGenerateAll) also get a normal span each. Hooks: First prefix /
    /// Last finalizer, so other mods' patches on these methods (Skidbladnir's replacing prefix on CreateObjects, its
    /// transpilers on FindSpawnPoint / UpdateTeleport, its prefix on PokeLocalZone ...) are inside the measured time;
    /// with TimeModPatches their patch methods are aggregated per owner the same way (<see cref="PatchOwnerProbe"/>).
    /// <c>HeightmapBuilder.Build</c> runs on the builder thread (HeightmapBuilder.cs:110-148): its time is the wait the
    /// main thread sees through IsTerrainReady. Outside the window the hooks only push/pop a per-thread stack entry.
    /// </summary>
    internal static class SpawnWindowProbe
    {
        private sealed class Agg
        {
            public string Name;
            public string Detail2;
            public long Count;
            public long Total;
            public long Max;
        }

        private static readonly Dictionary<MethodBase, Agg> Methods = new Dictionary<MethodBase, Agg>();
        private static readonly Dictionary<string, Agg> Patches = new Dictionary<string, Agg>();
        private static readonly HashSet<MethodBase> Rare = new HashSet<MethodBase>();

        [ThreadStatic] private static List<long> _starts;

        private static volatile bool _open;
        private static long _windowStart;
        private static bool _closed;

        public static bool Installed { get; private set; }

        // Citations: ValheimDecompiled-1.0.16\assembly_valheim\<File>.cs:<line>.
        internal static MethodInfo[] Targets() => new[]
        {
            AccessTools.DeclaredMethod(typeof(ZoneSystem), "CreateLocalZones"),             // ZoneSystem.cs:1234
            AccessTools.DeclaredMethod(typeof(ZoneSystem), "PokeLocalZone"),                // ZoneSystem.cs:1262
            AccessTools.DeclaredMethod(typeof(ZoneSystem), "IsActiveAreaLoaded"),           // ZoneSystem.cs:1295
            AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateDestroyObjects"),          // ZNetScene.cs:360
            AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObjects"),                 // ZNetScene.cs:185
            AccessTools.DeclaredMethod(typeof(ZNetScene), "IsAreaReady"),                   // ZNetScene.cs:156
            AccessTools.DeclaredMethod(typeof(HeightmapBuilder), "IsTerrainReady"),         // HeightmapBuilder.cs:285
            AccessTools.DeclaredMethod(typeof(HeightmapBuilder), "RequestTerrainSync"),     // HeightmapBuilder.cs:249 (main-thread busy wait)
            AccessTools.DeclaredMethod(typeof(HeightmapBuilder), "Build"),                  // HeightmapBuilder.cs:148 (builder thread)
            AccessTools.DeclaredMethod(typeof(Game), "FindSpawnPoint"),                     // Game.cs:533
            AccessTools.DeclaredMethod(typeof(Player), "UpdateTeleport"),                   // Player.cs:5915
            AccessTools.DeclaredMethod(typeof(DungeonGenerator), "Load"),                   // DungeonGenerator.cs:463
            AccessTools.DeclaredMethod(typeof(SnapToGround), nameof(SnapToGround.SnappAll)), // SnapToGround.cs:49
            AccessTools.DeclaredMethod(typeof(Heightmap), nameof(Heightmap.ForceGenerateAll)), // Heightmap.cs:300
        };

        public static void Install(Harmony harmony)
        {
            foreach (MethodInfo method in Targets())
            {
                if (method == null)
                {
                    Log.Warning("Spawn window probe: a target method was not found (game update?), skipped");
                    continue;
                }
                Methods[method] = new Agg { Name = method.DeclaringType?.Name + "." + method.Name };
                if (method.Name == "Load" || method.Name == nameof(SnapToGround.SnappAll) || method.Name == nameof(Heightmap.ForceGenerateAll))
                {
                    Rare.Add(method);
                }
                harmony.Patch(method,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(SpawnWindowProbe), nameof(Prefix)), Priority.First),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(SpawnWindowProbe), nameof(Finalizer)), Priority.Last));
            }
            Installed = true;
            Log.Info($"Spawn window probe: {Methods.Count} world-load methods hooked");
        }

        /// <summary>FejdStartup.LoadMainScene end (main thread).</summary>
        public static void Open()
        {
            if (!Installed || _open || _closed)
            {
                return;
            }
            _windowStart = StartupTrace.Now();
            _open = true;
        }

        /// <summary>End of the first Game.SpawnPlayer, before the world trace is snapshotted (main thread).</summary>
        public static void Close()
        {
            if (!_open)
            {
                return;
            }
            _open = false;
            _closed = true;
            long end = StartupTrace.Now();
            var all = new List<Agg>();
            lock (Methods)
            {
                all.AddRange(Methods.Values.Where(a => a.Count > 0));
                all.AddRange(Patches.Values.Where(a => a.Count > 0));
            }
            foreach (Agg agg in all.OrderByDescending(a => a.Total))
            {
                StartupTrace.Complete("spawnwindow", agg.Name, _windowStart, _windowStart + agg.Total,
                    string.Format(CultureInfo.InvariantCulture, "{0} calls, total {1:F1} ms, max {2:F2} ms, window {3:F0} ms",
                        agg.Count, StartupTrace.DurMs(0, agg.Total), StartupTrace.DurMs(0, agg.Max), StartupTrace.DurMs(_windowStart, end)),
                    agg.Detail2, tid: StartupTrace.LaneSpawnWindow);
            }
        }

        private static void Prefix()
        {
            (_starts ?? (_starts = new List<long>())).Add(_open ? StartupTrace.Now() : 0);
        }

        private static void Finalizer(MethodBase __originalMethod)
        {
            List<long> starts = _starts;
            if (starts == null || starts.Count == 0)
            {
                return;
            }
            long start = starts[starts.Count - 1];
            starts.RemoveAt(starts.Count - 1);
            if (start == 0 || !_open || !Methods.TryGetValue(__originalMethod, out Agg agg))
            {
                return;
            }
            long end = StartupTrace.Now();
            Add(agg, end - start);
            if (Rare.Contains(__originalMethod))
            {
                StartupTrace.Complete("spawnwindow.call", agg.Name, start, end);
            }
        }

        public static bool IsTarget(MethodBase method) => method != null && Methods.ContainsKey(method);

        /// <summary>A timed mod patch method of a spawn-window target (PatchOwnerProbe), aggregated per owner + method.</summary>
        public static void AddPatch(string owner, string patchMethod, string target, long start, long end)
        {
            if (!_open || start == 0)
            {
                return;
            }
            string key = owner + "|" + patchMethod + "|" + target;
            Agg agg;
            lock (Methods)
            {
                if (!Patches.TryGetValue(key, out agg))
                {
                    Patches[key] = agg = new Agg { Name = owner + " :: " + patchMethod, Detail2 = target };
                }
            }
            Add(agg, end - start);
        }

        private static void Add(Agg agg, long ticks)
        {
            lock (agg)
            {
                agg.Count++;
                agg.Total += ticks;
                if (ticks > agg.Max)
                {
                    agg.Max = ticks;
                }
            }
        }
    }
}
