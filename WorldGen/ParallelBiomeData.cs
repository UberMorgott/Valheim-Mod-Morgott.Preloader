using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Morgott.Preloader.Core;
using HarmonyLib;
using UnityEngine;

namespace Morgott.Preloader.WorldGen
{
    /// <summary>
    /// <c>AltBiomeWorldData.GenerateBiomePoints</c> (ValheimDecompiled-1.0.16 AltBiomeWorldData.cs:99-130) fills a 2048x2048
    /// biome + height map cell by cell on the main thread, on every server world load (ZNet.cs:464) and every client
    /// connect (ZNet.cs:1117), through <c>VerifyBiomeData</c>. Each cell is <c>WorldGenerator.GetBiome(x, y)</c> +
    /// <c>GetBiomeHeight(biome, x, y)</c>: pure functions of the world seed (Perlin noise via the thread-safe native
    /// <c>Mathf.PerlinNoise</c>, FastNoise reading only its settings, river lookup through a read-only dictionary plus a
    /// one-grid cache under <c>m_riverCacheLock</c>, WorldGenerator.cs:620; the HeightmapBuilder thread already calls
    /// them concurrently with the main thread, HeightmapBuilder.cs:159-199). <c>GetBiomeSector</c> inside GetBiomeHeight
    /// only reads the previous map, which is replaced at the end as in vanilla.
    /// The replacement runs the same loop body per row on all cores (each row writes only its own cells) and assigns the
    /// result exactly like vanilla. Falls back to vanilla while a foreign patch sits on any method of the generator path
    /// (WorldGenerator, DUtils, FastNoise, BiomeHelpers, the AltBiomeWorldData helpers), a foreign transpiler on
    /// GenerateBiomePoints, or when another prefix already skipped it; any exception in a worker reruns vanilla.
    /// </summary>
    internal static class ParallelBiomeData
    {
        private static AccessTools.FieldRef<AltBiomeWorldData, World> _world;
        private static MethodInfo _target;

        public static long LastMs = -1;
        public static bool LastParallel;

        public static void Install(Harmony harmony, bool prefetch, bool sectors)
        {
            _target = AccessTools.DeclaredMethod(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.GenerateBiomePoints), new[] { typeof(World) });
            MethodInfo getBiome = AccessTools.DeclaredMethod(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome),
                new[] { typeof(float), typeof(float), typeof(float), typeof(bool) });
            MethodInfo getBiomeHeight = AccessTools.DeclaredMethod(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeHeight));
            if (_target == null || getBiome == null || getBiomeHeight == null || AccessTools.DeclaredField(typeof(AltBiomeWorldData), "m_world") == null)
            {
                Log.Warning("ParallelBiomeData: AltBiomeWorldData.GenerateBiomePoints / WorldGenerator.GetBiome / GetBiomeHeight / m_world not found (game update?), module off");
                return;
            }
            _world = AccessTools.FieldRefAccess<AltBiomeWorldData, World>("m_world");
            harmony.Patch(_target, prefix: new HarmonyMethod(AccessTools.Method(typeof(ParallelBiomeData), nameof(Prefix))));
            if (prefetch)
            {
                MethodInfo initialize = AccessTools.DeclaredMethod(typeof(WorldGenerator), nameof(WorldGenerator.Initialize), new[] { typeof(World) });
                if (initialize == null)
                {
                    Log.Warning("ParallelBiomeData: WorldGenerator.Initialize(World) not found (game update?), no prefetch");
                    return;
                }
                // Prefix: a still running prefetch must end before vanilla clears the old generator's river data
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
        }

        private static bool _sectors;

        /// <summary>A biome map build started on worker threads right after the world generator was created.</summary>
        private sealed class Prefetch
        {
            public WorldGenerator Generator;
            public World World;
            public AltBiomeWorldData Data;
            public bool Sectors;
            public Task Task;
        }

        private static Prefetch _prefetch;
        private static bool _takenSectors;

        public static bool LastPrefetched;

        /// <summary>
        /// Prefetch: on a server the map is first needed in ZNet.Start (ServerLoadWorld -> VerifyBiomeData, ZNet.cs:454-464),
        /// one frame after WorldGenerator.Initialize in ZNet.Awake (:381); in between the rest of the main scene's Awake
        /// calls (ObjectDB, ZNetScene, ...), the native scene load and the world file read run on the main thread. The
        /// same per-row build starts here on worker threads and VerifyBiomeData takes its result (waiting if needed).
        /// The workers only read the generator: its state is final once the constructor returned (rivers, streams, lakes,
        /// offsets), its one-grid river cache is lock-protected (WorldGenerator.cs:620) and every main-thread caller in
        /// that window only reads it too (the HeightmapBuilder thread does the same concurrently in vanilla).
        /// <c>world.m_biomeData</c> is only assigned at consumption, as in vanilla. The result is discarded (and built again
        /// the normal way) if the generator or world changed, a foreign patch appeared on the generator path in between,
        /// or a worker failed. On a client the map is needed right after Initialize (ZNet.cs:1116-1117): it just waits.
        /// </summary>
        private static void InitializePostfix(World world)
        {
            DrainPrefetch();
            WorldGenerator generator = WorldGenerator.instance;
            if (world == null || world.m_menu || generator == null || generator.m_world != world)
            {
                return;
            }
            if (PatchGuard.Foreign(_target, bodyOnly: true) != null || PatchGuard.Foreign(GeneratorPath.Methods) != null)
            {
                return;
            }
            NormalizeRiverCache(generator);
            var data = new AltBiomeWorldData(AltBiomeWorldData.c_textureSize);
            bool sectors = _sectors && PrefetchedSectors.Allowed();
            var prefetch = new Prefetch { Generator = generator, World = world, Data = data, Sectors = sectors };
            prefetch.Task = Task.Run(() =>
            {
                Parallel.For(0, AltBiomeWorldData.c_textureSize, i => Row(generator, data, i));
                if (sectors)
                {
                    PrefetchedSectors.Build(data);
                }
            });
            _prefetch = prefetch;
        }

        /// <summary>Replacing prefix; postfixes of other mods still run after it.</summary>
        private static bool Prefix(World world, bool __runOriginal)
        {
            LastParallel = false;
            WorldGenerator generator = WorldGenerator.instance;
            if (!__runOriginal || world == null || generator == null)
            {
                return true;
            }
            string foreign = PatchGuard.Foreign(_target, bodyOnly: true) ?? PatchGuard.Foreign(GeneratorPath.Methods);
            if (foreign != null)
            {
                Log.WarningOnce("parallel-biome-foreign:" + foreign, $"ParallelBiomeData: {foreign}, vanilla biome map build used");
                DrainPrefetch();
                return true;
            }
            Stopwatch watch = Stopwatch.StartNew();
            const int size = AltBiomeWorldData.c_textureSize;
            _takenSectors = false;
            AltBiomeWorldData data = TakePrefetch(generator, world);
            LastPrefetched = data != null;
            if (data == null)
            {
                data = new AltBiomeWorldData(size);
                NormalizeRiverCache(generator);
                try
                {
                    Parallel.For(0, size, i => Row(generator, data, i));
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
            if (_takenSectors)
            {
                PrefetchedSectors.MarkReady(data);
            }
            LastMs = watch.ElapsedMilliseconds;
            LastParallel = true;
            return false;
        }

        /// <summary>Waits for a running prefetch (any outcome) and drops it. Main thread.</summary>
        private static void DrainPrefetch()
        {
            Prefetch prefetch = _prefetch;
            _prefetch = null;
            if (prefetch == null)
            {
                return;
            }
            try
            {
                prefetch.Task.Wait();
            }
            catch (Exception)
            {
                // Discarded anyway.
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
        /// Before many threads read heights at once: Pregenerate can leave the one-grid river cache holding an array that a
        /// later RenderRivers replaced in <c>m_riverPoints</c> (lookup during the stream search :344-347, then the merge
        /// :559-568 does not touch the cache), i.e. a cache hit there returns rivers without the last stream pass. In the
        /// sequential vanilla build the first in-world cell lookup almost surely lands in another grid and evicts it; with
        /// parallel rows any worker could hit it first. So the cache is pointed at the grid's current array (or none) under
        /// the generator's own write lock: every lookup then returns what the dictionary holds, as vanilla's do once evicted.
        /// </summary>
        internal static void NormalizeRiverCache(WorldGenerator generator)
        {
            ReaderWriterLockSlim cacheLock = CacheLock(generator);
            cacheLock.EnterWriteLock();
            try
            {
                Vector2i grid = CachedGrid(generator);
                RiverPoints(generator).TryGetValue(grid, out WorldGenerator.RiverPoint[] current);
                if (!ReferenceEquals(CachedPoints(generator), current))
                {
                    Log.InfoOnce("river-cache-normalized", $"ParallelBiomeData: river cache of grid {grid.x},{grid.y} held a superseded array, refreshed before the parallel build");
                    CachedPoints(generator) = current;
                }
            }
            finally
            {
                cacheLock.ExitWriteLock();
            }
        }

        /// <summary>The prefetched map for this generator + world, waiting for the workers if they are still busy; null if
        /// there is none or it cannot be used.</summary>
        private static AltBiomeWorldData TakePrefetch(WorldGenerator generator, World world)
        {
            Prefetch prefetch = _prefetch;
            _prefetch = null;
            if (prefetch == null)
            {
                return null;
            }
            try
            {
                prefetch.Task.Wait();
            }
            catch (Exception e)
            {
                Log.Warning($"ParallelBiomeData: prefetched build failed, built again: {(e as AggregateException)?.InnerException ?? e}");
                return null;
            }
            if (prefetch.Generator != generator || prefetch.World != world || (prefetch.Sectors && !PrefetchedSectors.Allowed()))
            {
                return null;
            }
            _takenSectors = prefetch.Sectors;
            return prefetch.Data;
        }

        /// <summary>One vanilla loop row (AltBiomeWorldData.cs:106-124), same calls with the same default arguments.</summary>
        private static void Row(WorldGenerator generator, AltBiomeWorldData data, int i)
        {
            Heightmap.BiomeIndex ocean = Heightmap.Biome.Ocean.ToBiomeIndex();
            for (int j = 0; j < AltBiomeWorldData.c_textureSize; j++)
            {
                Vector2 vector = new Vector2
                {
                    x = AltBiomeWorldData.MapSpaceToWorldSpace(j),
                    y = AltBiomeWorldData.MapSpaceToWorldSpace(i),
                };
                if (vector.sqrMagnitude > 110250000f)
                {
                    data.PointBiomes[j, i] = ocean;
                    data.PointHeights[j, i] = -1000f;
                }
                else
                {
                    Heightmap.Biome biome = generator.GetBiome(vector.x, vector.y);
                    data.PointBiomes[j, i] = biome.ToBiomeIndex();
                    data.PointHeights[j, i] = generator.GetBiomeHeight(biome, vector.x, vector.y, out Color _);
                }
            }
        }
    }
}
