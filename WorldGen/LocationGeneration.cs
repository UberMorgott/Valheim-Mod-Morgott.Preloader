using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Morgott.Preloader.Core;
using Morgott.Preloader.Profiling;
using HarmonyLib;
using SoftReferenceableAssets;
using UnityEngine;

namespace Morgott.Preloader.WorldGen
{
    /// <summary>
    /// Location placement of a new world: <c>ZoneSystem.GenerateLocationsTimeSliced(ZoneLocation, Stopwatch, ZPackage)</c>
    /// (ValheimDecompiled-1.0.16 ZoneSystem.cs:1871-2136), 6 points per zone. A point passing the cheap checks gets
    /// <c>WorldGenerator.GetTerrainDelta</c> (:1995; 10 x <c>Random.insideUnitCircle</c> + 10 x <c>GetHeight</c>), then the
    /// RNG-free checks similar (:2002), not-similar (:2008), vegetation (:2014-2026) and alt-biome (:2027-2037).
    /// The enumerator is wrapped so the current location is known. EarlyReject: a GetTerrainDelta prefix, only inside that
    /// wrapper and for the location's exterior radius, evaluates those later checks first (pure reads, same arguments);
    /// when one fails vanilla reaches the same <c>continue</c> whatever the delta, so it draws <c>insideUnitCircle</c>
    /// 10 times (same RNG state) and returns delta = +Infinity, which fails :1996. Not used for a non-finite
    /// <c>m_maxTerrainDelta</c> or while a foreign patch sits on any involved method. The stateful surround vegetation check
    /// (:2038) is never run early. Only debug-build error counters differ. Per-location counters go to the log summary
    /// and, with the profiler on, one span per location.
    /// </summary>
    internal static class LocationGeneration
    {
        private delegate bool HaveLocationInRangeFn(ZoneSystem zs, AssetID assetID, string group, Vector3 p, float radius, bool maxGroup);

        private static HaveLocationInRangeFn _haveLocationInRange;
        private static List<MethodBase> _involved;
        private static bool _earlyReject;

        /// <summary>Wrapper of the location enumerator currently running on the main thread (null outside).</summary>
        private static Tracked _current;

        // Run totals since the last LocationsGenerated (main thread).
        private static long _firstStart;
        private static long _busyTicks;
        private static long _reachDelta;
        private static long _deltaRejects;
        private static long _earlyRejects;
        private static int _locations;
        private static int _earlyOff;

        public static void Install(Harmony harmony, bool earlyReject)
        {
            MethodInfo generate = AccessTools.DeclaredMethod(typeof(ZoneSystem), "GenerateLocationsTimeSliced",
                new[] { typeof(ZoneSystem.ZoneLocation), typeof(Stopwatch), typeof(ZPackage) });
            MethodInfo terrainDelta = AccessTools.DeclaredMethod(typeof(WorldGenerator), nameof(WorldGenerator.GetTerrainDelta));
            MethodInfo haveInRange = AccessTools.DeclaredMethod(typeof(ZoneSystem), "HaveLocationInRange");
            if (generate == null || terrainDelta == null || haveInRange == null)
            {
                Log.Warning("WorldGen: ZoneSystem.GenerateLocationsTimeSliced / HaveLocationInRange / WorldGenerator.GetTerrainDelta not found (game update?), location hooks off");
                return;
            }
            _earlyReject = earlyReject;
            _haveLocationInRange = AccessTools.MethodDelegate<HaveLocationInRangeFn>(haveInRange);
            _involved = new List<MethodBase>
            {
                generate,
                AccessTools.EnumeratorMoveNext(generate),
                terrainDelta,
                haveInRange,
                AccessTools.DeclaredMethod(typeof(WorldGenerator), nameof(WorldGenerator.GetHeight), new[] { typeof(float), typeof(float), typeof(Color).MakeByRefType() }),
                AccessTools.DeclaredMethod(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), new[] { typeof(float), typeof(float), typeof(float), typeof(bool) }),
                AccessTools.DeclaredMethod(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeHeight)),
                AccessTools.DeclaredMethod(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeSector), new[] { typeof(Vector3), typeof(bool) }),
                AccessTools.DeclaredMethod(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeSector), new[] { typeof(int), typeof(int), typeof(bool) }),
                AccessTools.DeclaredPropertyGetter(typeof(ZoneSystem.ZoneLocation), nameof(ZoneSystem.ZoneLocation.AltBiomeParent)),
            };
            if (_involved.Contains(null))
            {
                Log.Warning("WorldGen: a method EarlyReject depends on was not found (game update?), EarlyReject off");
                _earlyReject = false;
            }
            harmony.Patch(generate, postfix: new HarmonyMethod(AccessTools.Method(typeof(LocationGeneration), nameof(GeneratePostfix))));
            harmony.Patch(terrainDelta,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(LocationGeneration), nameof(TerrainDeltaPrefix)), Priority.Last),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(LocationGeneration), nameof(TerrainDeltaPostfix)), Priority.First));
        }

        private static void GeneratePostfix(ZoneSystem.ZoneLocation location, ref IEnumerator __result)
        {
            if (__result == null || location == null) return;
            bool early = _earlyReject;
            if (early)
            {
                string foreign = PatchGuard.Foreign(_involved);
                if (foreign != null)
                {
                    Log.WarningOnce("early-reject-foreign:" + foreign, $"EarlyReject: {foreign}, vanilla location checks used");
                    early = false;
                }
                else if (!(location.m_maxTerrainDelta < float.PositiveInfinity))
                {
                    _earlyOff++;
                    early = false;
                }
            }
            __result = new Tracked(__result, location, early);
        }

        /// <summary>Last prefix: an earlier prefix of another mod that skipped the original is seen via __runOriginal.</summary>
        private static bool TerrainDeltaPrefix(Vector3 center, float radius, ref float delta, ref Vector3 slopeDirection, bool __runOriginal)
        {
            Tracked ctx = _current;
            if (ctx == null) return true;
            ctx.LastEarly = false;
            if (!__runOriginal || radius != ctx.Location.m_exteriorRadius) return true;
            ctx.ReachDelta++;
            if (!ctx.EarlyReject || !RejectedByLaterChecks(ctx.Location, center)) return true;
            // WorldGenerator.cs:1418-1420: the 10 samples' RNG draws, nothing else of the original touches the RNG.
            for (int i = 0; i < 10; i++) _ = UnityEngine.Random.insideUnitCircle;
            delta = float.PositiveInfinity;
            slopeDirection = Vector3.zero;
            ctx.LastEarly = true;
            ctx.EarlyRejects++;
            return false;
        }

        private static void TerrainDeltaPostfix(float radius, float delta)
        {
            Tracked ctx = _current;
            if (ctx == null || ctx.LastEarly || radius != ctx.Location.m_exteriorRadius) return;
            if (delta > ctx.Location.m_maxTerrainDelta || delta < ctx.Location.m_minTerrainDelta) ctx.DeltaRejects++;
        }

        /// <summary>ZoneSystem.cs:2002-2037 in order, minus the stateful surround check. True = vanilla continues.</summary>
        private static bool RejectedByLaterChecks(ZoneSystem.ZoneLocation location, Vector3 p)
        {
            ZoneSystem zs = ZoneSystem.instance;
            WorldGenerator generator = WorldGenerator.instance;
            if (location.m_minDistanceFromSimilar > 0f &&
                _haveLocationInRange(zs, location.m_prefab.m_assetID, location.m_group, p, location.m_minDistanceFromSimilar, false))
                return true;
            if (location.m_maxDistanceFromSimilar > 0f &&
                !_haveLocationInRange(zs, location.m_prefab.m_assetID, location.m_groupMax, p, location.m_maxDistanceFromSimilar, true))
                return true;
            if (location.m_minimumVegetation > 0f || location.m_maximumVegetation < 1f)
            {
                generator.GetHeight(p.x, p.z, out Color mask);
                if ((location.m_minimumVegetation > 0f && mask.a <= location.m_minimumVegetation) ||
                    (location.m_maximumVegetation < 1f && mask.a >= location.m_maximumVegetation))
                    return true;
            }
            BiomeSector sector = generator.GetBiomeSector(p);
            string parent = location.AltBiomeParent;
            if (parent != null && !sector.AltBiomes.Any(x => x.m_name == parent)) return true;
            return sector.AltBiomes.Any(x => x.m_blockLocationNames.Contains(location.m_name));
        }

        /// <summary>Log line (and per-run reset) at LocationsGenerated; null when no location was generated.</summary>
        public static string TakeSummary()
        {
            if (_locations == 0) return null;
            string line = string.Format(CultureInfo.InvariantCulture,
                "{0} location types in {1:F0} ms wall ({2:F0} ms in generation steps); {3} terrain-delta calls, {4} rejected by the delta, " +
                "{5} skipped by EarlyReject{6}",
                _locations, StartupTrace.DurMs(_firstStart, Stopwatch.GetTimestamp()), _busyTicks * 1000.0 / Stopwatch.Frequency,
                _reachDelta, _deltaRejects, _earlyRejects, _earlyOff > 0 ? $" ({_earlyOff} types with no finite max delta not eligible)" : "");
            _locations = _earlyOff = 0;
            _busyTicks = _reachDelta = _deltaRejects = _earlyRejects = 0;
            return line;
        }

        /// <summary>The vanilla enumerator with the current-location context around each step.</summary>
        private sealed class Tracked : IEnumerator, IDisposable
        {
            private readonly IEnumerator _inner;
            private long _start;
            private long _busy;

            public readonly ZoneSystem.ZoneLocation Location;
            public readonly bool EarlyReject;
            public bool LastEarly;
            public long ReachDelta;
            public long DeltaRejects;
            public long EarlyRejects;

            public Tracked(IEnumerator inner, ZoneSystem.ZoneLocation location, bool earlyReject)
            {
                _inner = inner;
                Location = location;
                EarlyReject = earlyReject;
            }

            public object Current => _inner.Current;

            public bool MoveNext()
            {
                long t0 = Stopwatch.GetTimestamp();
                if (_start == 0)
                {
                    _start = t0;
                    if (_locations == 0) _firstStart = t0;
                }
                Tracked outer = _current;
                _current = this;
                bool more = false;
                try
                {
                    more = _inner.MoveNext();
                    return more;
                }
                finally
                {
                    _current = outer;
                    _busy += Stopwatch.GetTimestamp() - t0;
                    if (!more) Finish();
                }
            }

            public void Reset() => _inner.Reset();

            public void Dispose() => (_inner as IDisposable)?.Dispose();

            private void Finish()
            {
                long end = Stopwatch.GetTimestamp();
                _locations++;
                _busyTicks += _busy;
                _reachDelta += ReachDelta;
                _deltaRejects += DeltaRejects;
                _earlyRejects += EarlyRejects;
                if (!StartupTrace.Recording) return;
                StartupTrace.Complete("worldgen", Location.m_prefab.Name, _start, end, string.Format(CultureInfo.InvariantCulture,
                        "busy {0:F1} ms, {1} terrain-delta calls, {2} delta rejects, {3} early rejects{4}",
                        _busy * 1000.0 / Stopwatch.Frequency, ReachDelta, DeltaRejects, EarlyRejects, EarlyReject ? "" : " (EarlyReject not used)"),
                    tid: StartupTrace.LaneWorldGen);
            }
        }
    }
}
