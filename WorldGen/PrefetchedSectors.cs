using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Morgott.Preloader.Core;
using HarmonyLib;
using UnityEngine;

namespace Morgott.Preloader.WorldGen
{
    /// <summary>
    /// With <see cref="ParallelBiomeData"/>'s prefetch: the pure part of <c>AltBiomeWorldData.GenerateSectors</c>
    /// (ValheimDecompiled-1.0.16 AltBiomeWorldData.cs:150-256: flood fill into sectors, edges, neighbours, centre / bounds /
    /// zones / heights) runs on the prefetch worker on the still unpublished data object; it reads only that object and
    /// creates its own BiomeSectors (constructor touches only the data object), <c>ZoneSystem.GetZone</c> is pure. The rest
    /// (:257-296: discovered flags from <c>IsZoneLoaded</c>, distance from centre, <c>GenerateAltBiomes</c> with
    /// <c>UnityEngine.Random</c>) still runs on the main thread at the vanilla moment, through a replacing prefix only for
    /// that object. Below is the vanilla body with <c>tryFill</c> inlined (same visit order, same appends). Off while a
    /// foreign patch is on any <see cref="_guarded"/> method (checked when the prefetch starts and when it is used).
    /// </summary>
    internal static class PrefetchedSectors
    {
        private static MethodBase[] _guarded;
        private static AltBiomeWorldData _ready;

        public static bool LastUsed;

        public static void Install(Harmony harmony)
        {
            MethodInfo generateSectors = AccessTools.DeclaredMethod(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.GenerateSectors));
            _guarded = new MethodBase[]
            {
                AccessTools.DeclaredMethod(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.VerifyBiomeData)),
                AccessTools.DeclaredMethod(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.GenerateBiomePoints)),
                generateSectors,
                AccessTools.DeclaredMethod(typeof(AltBiomeWorldData), "tryFill"),
                AccessTools.DeclaredConstructor(typeof(BiomeSector), new[] { typeof(AltBiomeWorldData), typeof(Heightmap.Biome) }),
                AccessTools.DeclaredConstructor(typeof(BiomeTypeInfo), new[] { typeof(Heightmap.Biome) }),
                AccessTools.DeclaredMethod(typeof(ZoneSystem), nameof(ZoneSystem.GetZone), new[] { typeof(Vector3) }),
            };
            if (_guarded.Contains(null))
            {
                Log.Warning("PrefetchedSectors: a GenerateSectors helper was not found (game update?), sectors are not prefetched");
                _guarded = null;
                return;
            }
            harmony.Patch(generateSectors, prefix: new HarmonyMethod(AccessTools.Method(typeof(PrefetchedSectors), nameof(Prefix)), Priority.First));
        }

        /// <summary>Main thread: may sectors be prefetched / used now? Also runs the static constructors the worker needs.</summary>
        public static bool Allowed()
        {
            if (_guarded == null || _guarded.Any(m => PatchGuard.Foreign(m) != null)) return false;
            RuntimeHelpers.RunClassConstructor(typeof(ZoneSystem).TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(BiomeSector).TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(Utils).TypeHandle);
            return true;
        }

        /// <summary>Main thread, when the prefetched data is published: its sectors are built.</summary>
        public static void MarkReady(AltBiomeWorldData data) => _ready = data;

        /// <summary>Worker thread: AltBiomeWorldData.cs:150-256 on the unpublished <paramref name="d"/> (dbStep calls are no-ops;
        /// a patch on them turns the prefetch off through the generator-path guard).</summary>
        public static void Build(AltBiomeWorldData d)
        {
            int size = d.Size;
            var stack = new Stack<BiomePointCoordinate>(1024);
            var visited = new bool[size * size];
            var dictionary = new Dictionary<Heightmap.BiomeIndex, BiomeSector>(3);
            foreach ((Heightmap.Biome biome, Heightmap.BiomeIndex index) in new[]
                     {
                         (Heightmap.Biome.AshLands, Heightmap.BiomeIndex.AshLands), (Heightmap.Biome.DeepNorth, Heightmap.BiomeIndex.DeepNorth),
                         (Heightmap.Biome.Ocean, Heightmap.BiomeIndex.Ocean),
                     })
            {
                d.Sectors.Add(new BiomeSector(d, biome));
                dictionary.Add(index, d.Sectors[d.Sectors.Count - 1]);
            }
            for (short y = 0; y < size; y++)
            {
                for (short x = 0; x < size; x++)
                {
                    if (!dictionary.TryGetValue(d.PointBiomes[x, y], out BiomeSector value)) continue;
                    d.PointSectors[x, y] = value;
                    visited[x + y * size] = true;
                    d.Biomes[value.Biome].AllPoints.Add(new BiomePointCoordinate(x, y));
                    if (d.PointHeights[x, y] >= 30f) d.Biomes[value.Biome].AllPointsAboveSeaLevel.Add(new BiomePointCoordinate(x, y));
                }
            }
            for (short y = 0; y < size; y++)
            {
                for (short x = 0; x < size; x++)
                {
                    if (visited[x + y * size]) continue;
                    visited[x + y * size] = true;
                    var biomeSector = new BiomeSector(d, d.PointBiomes[x, y].ToBiome());
                    d.Sectors.Add(biomeSector);
                    d.PointSectors[x, y] = biomeSector;
                    stack.Push(new BiomePointCoordinate(x, y));
                    while (stack.Count > 0)
                    {
                        BiomePointCoordinate c = stack.Pop();
                        Heightmap.BiomeIndex pBiome = d.PointBiomes[c.x, c.y];
                        BiomeSector pSector = d.PointSectors[c.x, c.y];
                        TryFill(d, visited, stack, pBiome, pSector, (short)(c.x + 1), c.y);
                        TryFill(d, visited, stack, pBiome, pSector, (short)(c.x - 1), c.y);
                        TryFill(d, visited, stack, pBiome, pSector, c.x, (short)(c.y + 1));
                        TryFill(d, visited, stack, pBiome, pSector, c.x, (short)(c.y - 1));
                    }
                }
            }
            for (int i = 1; i < size - 1; i++)
            {
                for (int j = 1; j < size - 1; j++)
                {
                    float height = d.PointHeights[j, i];
                    BiomeSector s = d.PointSectors[j, i];
                    BiomeSector item;
                    if ((item = d.PointSectors[j - 1, i]) == s && (item = d.PointSectors[j + 1, i]) == s &&
                        (item = d.PointSectors[j, i - 1]) == s && (item = d.PointSectors[j, i + 1]) == s)
                        continue;
                    s.EdgeCount++;
                    s.Center += new Vector2(j, i);
                    // Mathf.Min(a, b) = a < b ? a : b: the vanilla "if (a < cur) cur = a" exactly, NaN included.
                    s.Min.x = Mathf.Min(j, s.Min.x);
                    s.Max.x = Mathf.Max(j, s.Max.x);
                    s.Min.y = Mathf.Min(i, s.Min.y);
                    s.Max.y = Mathf.Max(i, s.Max.y);
                    if (!s.Neighbors.Contains(item)) s.Neighbors.Add(item);
                    s.HeightMin = Mathf.Min(height, s.HeightMin);
                    s.HeightMax = Mathf.Max(height, s.HeightMax);
                }
            }
            foreach (BiomeSector sector in d.Biomes.Values.SelectMany(b => b.Sectors).Where(s => s.EdgeCount > 0))
            {
                sector.Center = new Vector2(AltBiomeWorldData.MapSpaceToWorldSpace(sector.Center.x / sector.EdgeCount),
                    AltBiomeWorldData.MapSpaceToWorldSpace(sector.Center.y / sector.EdgeCount));
                sector.Min = new Vector2(AltBiomeWorldData.MapSpaceToWorldSpace(sector.Min.x), AltBiomeWorldData.MapSpaceToWorldSpace(sector.Min.y));
                sector.Max = new Vector2(AltBiomeWorldData.MapSpaceToWorldSpace(sector.Max.x), AltBiomeWorldData.MapSpaceToWorldSpace(sector.Max.y));
                sector.MinZone = ZoneSystem.GetZone(new Vector3(sector.Min.x, sector.Min.y));
                sector.MaxZone = ZoneSystem.GetZone(new Vector3(sector.Min.x, sector.Min.y));
                sector.HeightAvg = (sector.HeightMin + sector.HeightMax) / 2f;
            }
        }

        /// <summary>AltBiomeWorldData.tryFill (AltBiomeWorldData.cs:132-146).</summary>
        private static void TryFill(AltBiomeWorldData d, bool[] visited, Stack<BiomePointCoordinate> openList, Heightmap.BiomeIndex pBiome,
            BiomeSector pSector, short x, short y)
        {
            int size = d.Size;
            if (x < 0 || y < 0 || x >= size || y >= size || visited[x + y * size] || d.PointBiomes[x, y] != pBiome) return;
            visited[x + y * size] = true;
            d.PointSectors[x, y] = pSector;
            d.Biomes[pSector.Biome].AllPoints.Add(new BiomePointCoordinate(x, y));
            if (d.PointHeights[x, y] >= 30f) d.Biomes[pSector.Biome].AllPointsAboveSeaLevel.Add(new BiomePointCoordinate(x, y));
            openList.Push(new BiomePointCoordinate(x, y));
        }

        /// <summary>For the prefetched object only: the main-thread rest of GenerateSectors (AltBiomeWorldData.cs:199-296).</summary>
        private static bool Prefix(AltBiomeWorldData __instance, bool __runOriginal)
        {
            LastUsed = false;
            if (!__runOriginal || __instance == null || !ReferenceEquals(__instance, _ready)) return true;
            _ready = null;
            __instance.dbStep("Flood");
            __instance.SectorsCalculated = true;
            __instance.dbStep("FindEdgesNeighbors");
            foreach (BiomeTypeInfo info in __instance.Biomes.Values)
            {
                if (info.Biome == Heightmap.Biome.None || info.Biome == Heightmap.Biome.Ocean) continue;
                foreach (BiomeSector sector in info.Sectors)
                {
                    for (int k = sector.MinZone.y; k < sector.MaxZone.y && !sector.IsDiscovered; k++)
                    {
                        for (int l = sector.MinZone.x; l < sector.MaxZone.x; l++)
                        {
                            if (!ZoneSystem.instance.IsZoneLoaded(new Vector3(l, k))) continue;
                            sector.IsDiscovered = true;
                            break;
                        }
                    }
                }
            }
            __instance.dbStep("Loaded");
            foreach (BiomeSector sector in __instance.Biomes.Values.SelectMany(b => b.Sectors))
                sector.DistanceFromCenter = Vector2.Distance(sector.Center, Vector2.zero);
            __instance.dbStep("DistanceFromCenter");
            __instance.dbStep("Color");
            __instance.dbStep("Calcs");
            __instance.GenerateAltBiomes();
            __instance.dbStep("GenerateAltBiomes()");
            LastUsed = true;
            return false;
        }
    }
}
