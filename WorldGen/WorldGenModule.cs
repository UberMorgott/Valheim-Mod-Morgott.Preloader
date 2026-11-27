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
        private static bool _early;
        private static float _budget;

        public static void Install(Harmony harmony, bool profiler)
        {
            bool parallel = Config.ParallelBiomeData.Value;
            _early = Config.EarlyReject.Value;
            _budget = Config.LoadingTimeBudget.Value;
            _dump = Config.DumpLocations.Value;
            if (parallel) Log.Guard("ParallelBiomeData install", InstallParallelBiomeData, harmony);
            if (Config.PregenCache.Value)
            {
                PregenCache.Verify = _dump;
                Log.Guard("PregenCache install", PregenCache.Install, harmony);
            }
            if (Config.FastLakes.Value) Log.Guard("FastLakes install", InstallFastLakes, harmony);
            if (Config.ParallelMinimap.Value || Config.ClientMinimapCache.Value)
                Log.Guard("MinimapGen install", InstallMinimapGen, harmony);
            if (_early || profiler) Log.Guard("Location generation hooks install", InstallLocationGeneration, harmony);
            if (_budget > 0f) Log.Guard("LoadingTimeBudget install", InstallLoadingTimeBudget, harmony);
            Log.Guard("WorldGen timing hooks install", InstallTiming, harmony);
            bool prefetchBiome = parallel && Config.PrefetchBiomeData.Value;
            bool prefetchSectors = prefetchBiome && Config.PrefetchSectors.Value;
            Log.Info($"WorldGen: ParallelBiomeData={parallel.ToString()}, PrefetchBiomeData={prefetchBiome.ToString()}, FastLakes={Config.FastLakes.Value.ToString()}, PregenCache={Config.PregenCache.Value.ToString()}, PrefetchSectors={prefetchSectors.ToString()}, ParallelMinimap={Config.ParallelMinimap.Value.ToString()}, ClientMinimapCache={Config.ClientMinimapCache.Value.ToString()}, EarlyReject={_early.ToString()}, LoadingTimeBudget={_budget.ToString(CultureInfo.InvariantCulture)}, DumpLocations={_dump.ToString()}");
        }

        private static void InstallParallelBiomeData(Harmony harmony) =>
            ParallelBiomeData.Install(harmony, Config.PrefetchBiomeData.Value, Config.PrefetchSectors.Value);

        private static void InstallFastLakes(Harmony harmony) => FastLakes.Install(harmony, _dump);

        private static void InstallMinimapGen(Harmony harmony) =>
            MinimapGen.Install(harmony, Config.ParallelMinimap.Value, Config.ClientMinimapCache.Value);

        private static void InstallLocationGeneration(Harmony harmony) => LocationGeneration.Install(harmony, _early);

        private static void InstallLoadingTimeBudget(Harmony harmony) => LoadingTimeBudget.Install(harmony, _budget);

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
            string mode = ParallelBiomeData.LastParallel ? (ParallelBiomeData.LastPrefetched ? "prefetched" : "parallel") : "vanilla";
            string pregen = PregenCache.LastResult.Length > 0 ? PregenCache.LastResult : "not cached";
            string sectorsTag = PrefetchedSectors.LastUsed ? " prefetched" : "";
            Log.Info($"WorldGen: VerifyBiomeData {Ms(total).ToString("F0", CultureInfo.InvariantCulture)} ms (GenerateBiomePoints {Ms(_pointsTicks).ToString("F0", CultureInfo.InvariantCulture)} ms {mode}, GenerateSectors {Ms(_sectorsTicks).ToString("F0", CultureInfo.InvariantCulture)} ms{sectorsTag}) world '{world?.m_name}', pregeneration {pregen}");
            if (_dump) Log.Guard("WorldGen state hashes", LogStateHashes, world);
        }

        private static void LogStateHashes(World world) => Log.Info("WorldGen: state " + LocationsDump.StateHashes(world));

        private static void PointsPrefix() => _pointsStart = Stopwatch.GetTimestamp();

        private static void PointsFinalizer() => _pointsTicks = Stopwatch.GetTimestamp() - _pointsStart;

        private static void SectorsPrefix() => _sectorsStart = Stopwatch.GetTimestamp();

        private static void SectorsFinalizer() => _sectorsTicks = Stopwatch.GetTimestamp() - _sectorsStart;

        private static void LocationsGeneratedPostfix(ZoneSystem __instance, bool value)
        {
            if (!value) return;
            string summary = LocationGeneration.TakeSummary();
            if (summary != null) Log.Info("WorldGen: " + summary);
            if (_dump) Log.Guard("WorldGen locations dump", LocationsDump.Write, __instance);
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }
}
