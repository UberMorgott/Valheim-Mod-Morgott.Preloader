using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Morgott.Preloader.Core;
using HarmonyLib;

namespace Morgott.Preloader.WorldGen
{
    /// <summary>
    /// [WorldGen]: faster world load / connect and world creation, all with vanilla's output. Always logs the biome map
    /// timings (<c>VerifyBiomeData</c>, ZNet.cs:464 / :1117) and a location generation summary; <c>DumpLocations</c> writes
    /// the determinism dump (<see cref="LocationsDump"/>). Installed after Chainloader.Initialize (game types); every hook
    /// runs on the Unity main thread.
    /// </summary>
    internal static class WorldGenModule
    {
        private static long _verifyStart;
        private static long _pointsStart;
        private static long _pointsTicks;
        private static long _sectorsStart;
        private static long _sectorsTicks;
        private static bool _dump;

        public static void Install(Harmony harmony, bool profiler)
        {
            bool parallel = Config.ParallelBiomeData.Value;
            bool early = Config.EarlyReject.Value;
            float budget = Config.LoadingTimeBudget.Value;
            _dump = Config.DumpLocations.Value;
            if (parallel) Log.Guard("ParallelBiomeData install", () => ParallelBiomeData.Install(harmony, Config.PrefetchBiomeData.Value, Config.PrefetchSectors.Value));
            if (Config.PregenCache.Value)
            {
                PregenCache.Verify = _dump;
                Log.Guard("PregenCache install", () => PregenCache.Install(harmony));
            }
            if (Config.FastLakes.Value) Log.Guard("FastLakes install", () => FastLakes.Install(harmony, _dump));
            if (Config.ParallelMinimap.Value || Config.ClientMinimapCache.Value)
                Log.Guard("MinimapGen install", () => MinimapGen.Install(harmony, Config.ParallelMinimap.Value, Config.ClientMinimapCache.Value));
            if (early || profiler) Log.Guard("Location generation hooks install", () => LocationGeneration.Install(harmony, early));
            if (budget > 0f) Log.Guard("LoadingTimeBudget install", () => LoadingTimeBudget.Install(harmony, budget));
            Log.Guard("WorldGen timing hooks install", () => InstallTiming(harmony));
            Log.Info($"WorldGen: ParallelBiomeData={parallel}, PrefetchBiomeData={parallel && Config.PrefetchBiomeData.Value}, FastLakes={Config.FastLakes.Value}, PregenCache={Config.PregenCache.Value}, PrefetchSectors={parallel && Config.PrefetchBiomeData.Value && Config.PrefetchSectors.Value}, ParallelMinimap={Config.ParallelMinimap.Value}, ClientMinimapCache={Config.ClientMinimapCache.Value}, EarlyReject={early}, LoadingTimeBudget={budget.ToString(CultureInfo.InvariantCulture)}, DumpLocations={_dump}");
        }

        private static void InstallTiming(Harmony harmony)
        {
            Patch(harmony, nameof(AltBiomeWorldData.VerifyBiomeData), nameof(VerifyPrefix), nameof(VerifyFinalizer));
            Patch(harmony, nameof(AltBiomeWorldData.GenerateBiomePoints), nameof(PointsPrefix), nameof(PointsFinalizer));
            Patch(harmony, nameof(AltBiomeWorldData.GenerateSectors), nameof(SectorsPrefix), nameof(SectorsFinalizer));
            MethodInfo setter = AccessTools.DeclaredPropertySetter(typeof(ZoneSystem), nameof(ZoneSystem.LocationsGenerated));
            if (setter == null)
            {
                Log.Warning("WorldGen: ZoneSystem.LocationsGenerated setter not found (game update?), no location summary / dump");
                return;
            }
            harmony.Patch(setter, postfix: new HarmonyMethod(AccessTools.Method(typeof(WorldGenModule), nameof(LocationsGeneratedPostfix))));
        }

        private static void Patch(Harmony harmony, string method, string prefix, string finalizer)
        {
            MethodInfo target = AccessTools.DeclaredMethod(typeof(AltBiomeWorldData), method);
            if (target == null)
            {
                Log.Warning($"WorldGen: AltBiomeWorldData method for {prefix} not found (game update?), timing skipped");
                return;
            }
            harmony.Patch(target,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(WorldGenModule), prefix), Priority.First),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(WorldGenModule), finalizer), Priority.Last));
        }

        private static void VerifyPrefix()
        {
            _verifyStart = Stopwatch.GetTimestamp();
            _pointsTicks = _sectorsTicks = 0;
        }

        private static void VerifyFinalizer(World world)
        {
            if (_verifyStart == 0) return;
            long total = Stopwatch.GetTimestamp() - _verifyStart;
            _verifyStart = 0;
            Log.Info(string.Format(CultureInfo.InvariantCulture,
                "WorldGen: VerifyBiomeData {0:F0} ms (GenerateBiomePoints {1:F0} ms {2}, GenerateSectors {3:F0} ms{6}) world '{4}', pregeneration {5}",
                Ms(total), Ms(_pointsTicks), ParallelBiomeData.LastParallel ? (ParallelBiomeData.LastPrefetched ? "prefetched" : "parallel") : "vanilla",
                Ms(_sectorsTicks), world?.m_name, PregenCache.LastResult.Length > 0 ? PregenCache.LastResult : "not cached",
                PrefetchedSectors.LastUsed ? " prefetched" : ""));
            if (_dump) Log.Guard("WorldGen state hashes", () => Log.Info("WorldGen: state " + LocationsDump.StateHashes(world)));
        }

        private static void PointsPrefix() => _pointsStart = Stopwatch.GetTimestamp();

        private static void PointsFinalizer() => _pointsTicks = Stopwatch.GetTimestamp() - _pointsStart;

        private static void SectorsPrefix() => _sectorsStart = Stopwatch.GetTimestamp();

        private static void SectorsFinalizer() => _sectorsTicks = Stopwatch.GetTimestamp() - _sectorsStart;

        private static void LocationsGeneratedPostfix(ZoneSystem __instance, bool value)
        {
            if (!value) return;
            string summary = LocationGeneration.TakeSummary();
            if (summary != null) Log.Info("WorldGen: " + summary);
            if (_dump) Log.Guard("WorldGen locations dump", () => LocationsDump.Write(__instance));
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }
}
