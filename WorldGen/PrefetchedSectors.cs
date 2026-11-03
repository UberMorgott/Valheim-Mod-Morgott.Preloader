using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Morgott.Preloader.Core;
using HarmonyLib;
using UnityEngine;

namespace Morgott.Preloader.WorldGen
{
    /// <summary>
    /// With <see cref="ParallelBiomeData"/>'s prefetch: the pure part of <c>AltBiomeWorldData.GenerateSectors</c>
    /// (ValheimDecompiled-1.0.16 AltBiomeWorldData.cs:150-256: flood fill of the 2048x2048 map into sectors, edges,
    /// neighbours, centre / bounds / zones / heights) is built on the prefetch worker right after the map, on the
    /// still unpublished data object. It reads only that object's arrays and creates its own BiomeSector objects (whose
    /// constructor only touches the data object, BiomeSector.cs:48-59); <c>ZoneSystem.GetZone</c> is a static pure
    /// function (ZoneSystem.cs:2967). The rest of GenerateSectors (:257-296: <c>SectorsCalculated</c>, the discovered
    /// flags from <c>ZoneSystem.IsZoneLoaded</c>, distance from centre, <c>GenerateAltBiomes</c> with
    /// <c>UnityEngine.Random</c>) depends on main-thread state and still runs at the vanilla moment, on the main thread,
    /// in the vanilla order, through a replacing prefix that only applies to that prefetched object. The code below is
    /// the vanilla loop body with the private <c>tryFill</c> inlined (same visit order, same list appends).
    /// Off while any foreign patch is on VerifyBiomeData, GenerateBiomePoints, GenerateSectors, tryFill, the BiomeSector /
    /// BiomeTypeInfo constructors or ZoneSystem.GetZone (checked when the prefetch starts and again when it is used).
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
            foreach (MethodBase m in _guarded)
            {
                if (m == null)
                {
                    Log.Warning("PrefetchedSectors: a GenerateSectors helper was not found (game update?), sectors are not prefetched");
                    _guarded = null;
                    return;
                }
            }
            harmony.Patch(generateSectors, prefix: new HarmonyMethod(AccessTools.Method(typeof(PrefetchedSectors), nameof(Prefix)), Priority.First));
        }

        /// <summary>Main thread: may sectors be prefetched / used now? Also runs the static constructors the worker needs.</summary>
        public static bool Allowed()
        {
            if (_guarded == null)
            {
                return false;
            }
            foreach (MethodBase m in _guarded)
            {
                if (PatchGuard.Foreign(m) != null)
                {
                    return false;
                }
            }
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
            d.Sectors.Add(new BiomeSector(d, Heightmap.Biome.AshLands));
            dictionary.Add(Heightmap.BiomeIndex.AshLands, d.Sectors[d.Sectors.Count - 1]);
            d.Sectors.Add(new BiomeSector(d, Heightmap.Biome.DeepNorth));
            dictionary.Add(Heightmap.BiomeIndex.DeepNorth, d.Sectors[d.Sectors.Count - 1]);
            d.Sectors.Add(new BiomeSector(d, Heightmap.Biome.Ocean));
            dictionary.Add(Heightmap.BiomeIndex.Ocean, d.Sectors[d.Sectors.Count - 1]);
            for (short num = 0; num < size; num++)
            {
                for (short num2 = 0; num2 < size; num2++)
                {
                    if (dictionary.TryGetValue(d.PointBiomes[num2, num], out BiomeSector value))
                    {
                        d.PointSectors[num2, num] = value;
                        visited[num2 + num * size] = true;
                        d.Biomes[value.Biome].AllPoints.Add(new BiomePointCoordinate(num2, num));
                        if (d.PointHeights[num2, num] >= 30f)
                        {
                            d.Biomes[value.Biome].AllPointsAboveSeaLevel.Add(new BiomePointCoordinate(num2, num));
                        }
                    }
                }
            }
            for (short num3 = 0; num3 < size; num3++)
            {
                for (short num4 = 0; num4 < size; num4++)
                {
                    if (visited[num4 + num3 * size])
                    {
                        continue;
                    }
                    visited[num4 + num3 * size] = true;
                    var biomeSector = new BiomeSector(d, d.PointBiomes[num4, num3].ToBiome());
                    d.Sectors.Add(biomeSector);
                    d.PointSectors[num4, num3] = biomeSector;
                    stack.Push(new BiomePointCoordinate(num4, num3));
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
                    float num5 = d.PointHeights[j, i];
                    BiomeSector biomeSector2 = d.PointSectors[j, i];
                    BiomeSector item;
                    if ((item = d.PointSectors[j - 1, i]) != biomeSector2 || (item = d.PointSectors[j + 1, i]) != biomeSector2 ||
                        (item = d.PointSectors[j, i - 1]) != biomeSector2 || (item = d.PointSectors[j, i + 1]) != biomeSector2)
                    {
                        biomeSector2.EdgeCount++;
                        biomeSector2.Center += new Vector2(j, i);
                        if (j < biomeSector2.Min.x)
                        {
                            biomeSector2.Min.x = j;
                        }
                        if (j > biomeSector2.Max.x)
                        {
                            biomeSector2.Max.x = j;
                        }
                        if (i < biomeSector2.Min.y)
                        {
                            biomeSector2.Min.y = i;
                        }
                        if (i > biomeSector2.Max.y)
                        {
                            biomeSector2.Max.y = i;
                        }
                        if (!biomeSector2.Neighbors.Contains(item))
                        {
                            biomeSector2.Neighbors.Add(item);
                        }
                        if (num5 < biomeSector2.HeightMin)
                        {
                            biomeSector2.HeightMin = num5;
                        }
                        if (num5 > biomeSector2.HeightMax)
                        {
                            biomeSector2.HeightMax = num5;
                        }
                    }
                }
            }
            foreach (KeyValuePair<Heightmap.Biome, BiomeTypeInfo> biome in d.Biomes)
            {
                foreach (BiomeSector sector in biome.Value.Sectors)
                {
                    if (sector.EdgeCount > 0)
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
            }
        }

        /// <summary>AltBiomeWorldData.tryFill (AltBiomeWorldData.cs:132-146).</summary>
        private static void TryFill(AltBiomeWorldData d, bool[] visited, Stack<BiomePointCoordinate> openList, Heightmap.BiomeIndex pBiome,
            BiomeSector pSector, short x, short y)
        {
            int size = d.Size;
            if (x >= 0 && y >= 0 && x < size && y < size && !visited[x + y * size] && d.PointBiomes[x, y] == pBiome)
            {
                visited[x + y * size] = true;
                d.PointSectors[x, y] = pSector;
                d.Biomes[pSector.Biome].AllPoints.Add(new BiomePointCoordinate(x, y));
                if (d.PointHeights[x, y] >= 30f)
                {
                    d.Biomes[pSector.Biome].AllPointsAboveSeaLevel.Add(new BiomePointCoordinate(x, y));
                }
                openList.Push(new BiomePointCoordinate(x, y));
            }
        }

        /// <summary>For the prefetched object only: the main-thread rest of GenerateSectors (AltBiomeWorldData.cs:199-296).</summary>
        private static bool Prefix(AltBiomeWorldData __instance, bool __runOriginal)
        {
            LastUsed = false;
            if (!__runOriginal || __instance == null || !ReferenceEquals(__instance, _ready))
            {
                return true;
            }
            _ready = null;
            __instance.dbStep("Flood");
            __instance.SectorsCalculated = true;
            __instance.dbStep("FindEdgesNeighbors");
            foreach (KeyValuePair<Heightmap.Biome, BiomeTypeInfo> biome2 in __instance.Biomes)
            {
                if (biome2.Value.Biome == Heightmap.Biome.None || biome2.Value.Biome == Heightmap.Biome.Ocean)
                {
                    continue;
                }
                foreach (BiomeSector sector2 in biome2.Value.Sectors)
                {
                    for (int k = sector2.MinZone.y; k < sector2.MaxZone.y; k++)
                    {
                        for (int l = sector2.MinZone.x; l < sector2.MaxZone.x; l++)
                        {
                            if (ZoneSystem.instance.IsZoneLoaded(new Vector3(l, k)))
                            {
                                sector2.IsDiscovered = true;
                                break;
                            }
                        }
                        if (sector2.IsDiscovered)
                        {
                            break;
                        }
                    }
                }
            }
            __instance.dbStep("Loaded");
            foreach (KeyValuePair<Heightmap.Biome, BiomeTypeInfo> biome3 in __instance.Biomes)
            {
                foreach (BiomeSector sector3 in biome3.Value.Sectors)
                {
                    sector3.DistanceFromCenter = Vector2.Distance(sector3.Center, Vector2.zero);
                }
            }
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
