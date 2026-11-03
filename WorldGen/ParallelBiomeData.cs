using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Morgott.Preloader.Core;
using HarmonyLib;
using UnityEngine;

namespace Morgott.Preloader.WorldGen
{
    /// <summary>
    /// <c>AltBiomeWorldData.GenerateBiomePoints</c> (ValheimDecompiled-1.0.16 AltBiomeWorldData.cs:99-130) fills the
    /// 2048x2048 biome + height map cell by cell on the main thread on every world load (ZNet.cs:464) and client connect
    /// (:1117). Each cell is <c>GetBiome</c> + <c>GetBiomeHeight</c>: pure functions of the seed (thread-safe native Perlin,
    /// read-only river dictionary + a one-grid cache under <c>m_riverCacheLock</c>; the HeightmapBuilder thread already
    /// calls them concurrently). The replacement runs the same row body on all cores (each row writes only its cells) and
    /// assigns the result like vanilla. Vanilla while a foreign patch is on the generator path or transpiles
    /// GenerateBiomePoints, when another prefix skipped it, or when a worker throws.
    /// </summary>
    internal static class ParallelBiomeData
    {
        private static AccessTools.FieldRef<AltBiomeWorldData, World> _world;
        private static MethodInfo _target;
        private static bool _sectors;
        private static Prefetch _prefetch;
        private static bool _takenSectors;

        public static bool LastParallel;
        public static bool LastPrefetched;

        /// <summary>A biome map build started on worker threads right after the world generator was created.</summary>
        private sealed class Prefetch
        {
            public WorldGenerator Generator;
            public World World;
            public AltBiomeWorldData Data;
            public bool Sectors;
            public Task Task;
        }

        public static void Install(Harmony harmony, bool prefetch, bool sectors)
        {
            _target = AccessTools.DeclaredMethod(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.GenerateBiomePoints), new[] { typeof(World) });
            if (_target == null || AccessTools.DeclaredField(typeof(AltBiomeWorldData), "m_world") == null ||
                AccessTools.DeclaredMethod(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), new[] { typeof(float), typeof(float), typeof(float), typeof(bool) }) == null ||
                AccessTools.DeclaredMethod(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeHeight)) == null)
            {
                Log.Warning("ParallelBiomeData: AltBiomeWorldData.GenerateBiomePoints / WorldGenerator.GetBiome / GetBiomeHeight / m_world not found (game update?), module off");
                return;
            }
            _world = AccessTools.FieldRefAccess<AltBiomeWorldData, World>("m_world");
            harmony.Patch(_target, prefix: new HarmonyMethod(AccessTools.Method(typeof(ParallelBiomeData), nameof(Prefix))));
            if (!prefetch) return;
            MethodInfo initialize = AccessTools.DeclaredMethod(typeof(WorldGenerator), nameof(WorldGenerator.Initialize), new[] { typeof(World) });
            if (initialize == null)
            {
                Log.Warning("ParallelBiomeData: WorldGenerator.Initialize(World) not found (game update?), no prefetch");
                return;
            }
            // Prefix: a running prefetch must end before vanilla clears the old generator's river data
            // (WorldGenerator.cs:189 -> CleanCachedRiverData :230-235, no lock) and creates the next one.
            harmony.Patch(initialize,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ParallelBiomeData), nameof(DrainPrefetch)), Priority.First),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(ParallelBiomeData), nameof(InitializePostfix)), Priority.Last));
            if (sectors)
            {
                PrefetchedSectors.Install(harmony);
                _sectors = true;
            }
        }

        /// <summary>
        /// Prefetch: on a server the map is first needed in ZNet.Start (ZNet.cs:454-464), one frame after
        /// WorldGenerator.Initialize in ZNet.Awake (:381), while the rest of the main scene loads on the main thread. The
        /// same per-row build starts here on workers and VerifyBiomeData takes its result (waiting if needed). Workers only
        /// read the generator, whose state is final after its constructor; every main-thread caller in that window only
        /// reads too. The result is discarded if the generator or world changed, a foreign patch appeared, or a worker
        /// failed. On a client the map is needed right after Initialize (ZNet.cs:1116-1117): it just waits.
        /// </summary>
        private static void InitializePostfix(World world)
        {
            DrainPrefetch();
            WorldGenerator generator = WorldGenerator.instance;
            if (world == null || world.m_menu || generator == null || generator.m_world != world || Foreign() != null) return;
            NormalizeRiverCache(generator);
            var data = new AltBiomeWorldData(AltBiomeWorldData.c_textureSize);
            bool sectors = _sectors && PrefetchedSectors.Allowed();
            _prefetch = new Prefetch
            {
                Generator = generator,
                World = world,
                Data = data,
                Sectors = sectors,
                Task = Task.Run(() =>
                {
                    Parallel.For(0, AltBiomeWorldData.c_textureSize, i => Row(generator, data, i));
                    if (sectors) PrefetchedSectors.Build(data);
                }),
            };
        }

        private static string Foreign() => PatchGuard.Foreign(_target, bodyOnly: true) ?? PatchGuard.Foreign(GeneratorPath.Methods);

        /// <summary>Replacing prefix; postfixes of other mods still run after it.</summary>
        private static bool Prefix(World world, bool __runOriginal)
        {
            LastParallel = false;
            WorldGenerator generator = WorldGenerator.instance;
            if (!__runOriginal || world == null || generator == null) return true;
            string foreign = Foreign();
            if (foreign != null)
            {
                Log.WarningOnce("parallel-biome-foreign:" + foreign, $"ParallelBiomeData: {foreign}, vanilla biome map build used");
                DrainPrefetch();
                return true;
            }
            Prefetch prefetch = Take(log: true);
            bool usable = prefetch != null && prefetch.Generator == generator && prefetch.World == world &&
                          (!prefetch.Sectors || PrefetchedSectors.Allowed());
            _takenSectors = usable && prefetch.Sectors;
            AltBiomeWorldData data = usable ? prefetch.Data : null;
            LastPrefetched = data != null;
            if (data == null)
            {
                data = new AltBiomeWorldData(AltBiomeWorldData.c_textureSize);
                NormalizeRiverCache(generator);
                try
                {
                    Parallel.For(0, AltBiomeWorldData.c_textureSize, i => Row(generator, data, i));
                }
                catch (Exception e)
                {
                    Log.Warning($"ParallelBiomeData: parallel build failed, vanilla rerun: {(e as AggregateException)?.InnerException ?? e}");
                    return true;
                }
            }
            data.dt = DateTime.Now;
            data.PointsGenerated = true;
            world.m_biomeData = data;
            _world(data) = world;
            if (_takenSectors) PrefetchedSectors.MarkReady(data);
            LastParallel = true;
            return false;
        }

        /// <summary>Waits for a running prefetch (any outcome) and drops it. Main thread.</summary>
        private static void DrainPrefetch() => Take(log: false);

        /// <summary>The pending prefetch once its workers finished, null if none or it failed. Main thread.</summary>
        private static Prefetch Take(bool log)
        {
            Prefetch prefetch = _prefetch;
            _prefetch = null;
            try
            {
                prefetch?.Task.Wait();
                return prefetch;
            }
            catch (Exception e)
            {
                if (log) Log.Warning($"ParallelBiomeData: prefetched build failed, built again: {(e as AggregateException)?.InnerException ?? e}");
                return null;
            }
        }

        private static readonly AccessTools.FieldRef<WorldGenerator, Dictionary<Vector2i, WorldGenerator.RiverPoint[]>> RiverPoints =
            AccessTools.FieldRefAccess<WorldGenerator, Dictionary<Vector2i, WorldGenerator.RiverPoint[]>>("m_riverPoints");
        private static readonly AccessTools.FieldRef<WorldGenerator, WorldGenerator.RiverPoint[]> CachedPoints =
            AccessTools.FieldRefAccess<WorldGenerator, WorldGenerator.RiverPoint[]>("m_cachedRiverPoints");
        private static readonly AccessTools.FieldRef<WorldGenerator, Vector2i> CachedGrid =
            AccessTools.FieldRefAccess<WorldGenerator, Vector2i>("m_cachedRiverGrid");
        private static readonly AccessTools.FieldRef<WorldGenerator, ReaderWriterLockSlim> CacheLock =
            AccessTools.FieldRefAccess<WorldGenerator, ReaderWriterLockSlim>("m_riverCacheLock");

        /// <summary>
        /// Before many threads read heights at once: Pregenerate can leave the one-grid river cache holding an array a later
        /// RenderRivers replaced in <c>m_riverPoints</c>, i.e. rivers without the last stream pass. Sequential vanilla
        /// almost surely evicts it with the first lookup; parallel rows could hit it first. So the cache is pointed at the
        /// grid's current array under the generator's write lock, as vanilla's lookups see once evicted.
        /// </summary>
        internal static void NormalizeRiverCache(WorldGenerator generator)
        {
            ReaderWriterLockSlim cacheLock = CacheLock(generator);
            cacheLock.EnterWriteLock();
            try
            {
                Vector2i grid = CachedGrid(generator);
                RiverPoints(generator).TryGetValue(grid, out WorldGenerator.RiverPoint[] current);
                if (ReferenceEquals(CachedPoints(generator), current)) return;
                Log.InfoOnce("river-cache-normalized", $"ParallelBiomeData: river cache of grid {grid.x},{grid.y} held a superseded array, refreshed before the parallel build");
                CachedPoints(generator) = current;
            }
            finally
            {
                cacheLock.ExitWriteLock();
            }
        }

        /// <summary>One vanilla loop row (AltBiomeWorldData.cs:106-124), same calls with the same default arguments.</summary>
        private static void Row(WorldGenerator generator, AltBiomeWorldData data, int i)
        {
            Heightmap.BiomeIndex ocean = Heightmap.Biome.Ocean.ToBiomeIndex();
            for (int j = 0; j < AltBiomeWorldData.c_textureSize; j++)
            {
                var vector = new Vector2 { x = AltBiomeWorldData.MapSpaceToWorldSpace(j), y = AltBiomeWorldData.MapSpaceToWorldSpace(i) };
                if (vector.sqrMagnitude > 110250000f)
                {
                    data.PointBiomes[j, i] = ocean;
                    data.PointHeights[j, i] = -1000f;
                    continue;
                }
                Heightmap.Biome biome = generator.GetBiome(vector.x, vector.y);
                data.PointBiomes[j, i] = biome.ToBiomeIndex();
                data.PointHeights[j, i] = generator.GetBiomeHeight(biome, vector.x, vector.y, out Color _);
            }
        }
    }
}
