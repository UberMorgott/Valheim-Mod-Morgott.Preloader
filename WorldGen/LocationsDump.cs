using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using Morgott.Preloader.Core;

namespace Morgott.Preloader.WorldGen
{
    /// <summary>
    /// <c>[WorldGen] DumpLocations</c>: at LocationsGenerated writes <c>BepInEx\Morgott.Preloader\diag\locations-&lt;seed&gt;.txt</c>:
    /// the SHA-256 of the biome map (<c>PointBiomes</c> as int32, <c>PointHeights</c> as float bits, both in [x, y] order)
    /// and every <c>ZoneSystem.m_locationInstances</c> entry sorted by prefab name, then zone, with the position as raw
    /// float bits. Nothing in it depends on timing, so two runs of the same seed must give the same file byte for byte.
    /// </summary>
    internal static class LocationsDump
    {
        public static void Write(ZoneSystem zs)
        {
            World world = ZNet.World;
            WorldGenerator generator = WorldGenerator.instance;
            if (world == null || generator == null)
            {
                return;
            }
            var sb = new StringBuilder();
            sb.Append("world ").Append(world.m_name).Append(" seed ").Append(world.m_seedName).Append(' ')
                .Append(generator.GetSeed().ToString(CultureInfo.InvariantCulture)).Append(" worldgen ")
                .Append(world.m_worldGenVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
            AltBiomeWorldData data = world.m_biomeData;
            if (data?.PointBiomes != null && data.PointHeights != null)
            {
                sb.Append("biomes ").Append(HashBiomes(data.PointBiomes)).Append('\n');
                sb.Append("heights ").Append(HashHeights(data.PointHeights)).Append('\n');
            }
            else
            {
                sb.Append("biome map missing\n");
            }
            // World generator pregeneration (WorldGenerator.cs:252-258): lakes, rivers, streams as float bits.
            List<UnityEngine.Vector2> lakes = generator.GetLakes() ?? new List<UnityEngine.Vector2>();
            var lakeText = new StringBuilder();
            foreach (UnityEngine.Vector2 p in lakes)
            {
                lakeText.Append(Bits(p.x).ToString("x8")).Append(',').Append(Bits(p.y).ToString("x8")).Append(';');
            }
            sb.Append("lakes ").Append(lakes.Count.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(Sha(Encoding.ASCII.GetBytes(lakeText.ToString()))).Append('\n');
            sb.Append("rivers ").Append(HashRivers(generator.GetRivers())).Append('\n');
            sb.Append("streams ").Append(HashRivers(generator.GetStreams())).Append('\n');
            var rows = new List<(string Name, short X, short Y, string Line)>();
            foreach (KeyValuePair<Vector2s, ZoneSystem.LocationInstance> entry in zs.m_locationInstances)
            {
                ZoneSystem.LocationInstance instance = entry.Value;
                string name = instance.m_location?.m_prefab.Name ?? "<null>";
                rows.Add((name, entry.Key.x, entry.Key.y, string.Format(CultureInfo.InvariantCulture,
                    "{0}\t{1},{2}\t{3:x8},{4:x8},{5:x8}\t{6}", name, entry.Key.x, entry.Key.y, Bits(instance.m_position.x),
                    Bits(instance.m_position.y), Bits(instance.m_position.z), instance.m_placed ? "placed" : "-")));
            }
            sb.Append("locations ").Append(rows.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var row in rows.OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.X).ThenBy(r => r.Y))
            {
                sb.Append(row.Line).Append('\n');
            }
            string path = Path.Combine(Paths.BepInExRootPath, "Morgott.Preloader", "diag",
                "locations-" + generator.GetSeed().ToString(CultureInfo.InvariantCulture) + ".txt");
            AtomicFile.WriteAllText(path, sb.ToString());
            Log.Info($"WorldGen: {rows.Count} location instances dumped to {path}");
        }

        /// <summary>One line for the log at VerifyBiomeData end: SHA-256 (first 16 hex) of the biome map, heights, lakes,
        /// rivers, streams, every <c>m_riverPoints</c> entry in enumeration order and the one-grid river cache.</summary>
        public static string StateHashes(World world)
        {
            WorldGenerator generator = WorldGenerator.instance;
            if (generator == null)
            {
                return "no generator";
            }
            var points = new StringBuilder();
            var riverPoints = (Dictionary<Vector2i, WorldGenerator.RiverPoint[]>)HarmonyLib.AccessTools.Field(typeof(WorldGenerator), "m_riverPoints").GetValue(generator);
            int count = 0;
            foreach (KeyValuePair<Vector2i, WorldGenerator.RiverPoint[]> kv in riverPoints)
            {
                points.Append(kv.Key.x).Append(',').Append(kv.Key.y).Append(':');
                foreach (WorldGenerator.RiverPoint p in kv.Value)
                {
                    points.Append(Bits(p.p.x).ToString("x8")).Append(Bits(p.p.y).ToString("x8")).Append(Bits(p.w).ToString("x8")).Append(Bits(p.w2).ToString("x8"));
                    count++;
                }
                points.Append(';');
            }

            var lakes = new StringBuilder();
            foreach (UnityEngine.Vector2 p in generator.GetLakes() ?? new List<UnityEngine.Vector2>())
            {
                lakes.Append(Bits(p.x).ToString("x8")).Append(Bits(p.y).ToString("x8"));
            }
            AltBiomeWorldData data = world?.m_biomeData;
            return "sectors " + HashSectors(data) + " " + string.Format(CultureInfo.InvariantCulture,
                "biomes {0} heights {1} lakes {2} {3} rivers {4} streams {5} riverPoints keys {6} points {7} sha {8} riverCache now {9}",
                data?.PointBiomes != null ? HashBiomes(data.PointBiomes).Substring(0, 16) : "-",
                data?.PointHeights != null ? HashHeights(data.PointHeights).Substring(0, 16) : "-",
                generator.GetLakes()?.Count ?? 0, Sha(Encoding.ASCII.GetBytes(lakes.ToString())).Substring(0, 16),
                HashRivers(generator.GetRivers()), HashRivers(generator.GetStreams()),
                riverPoints.Count, count, Sha(Encoding.ASCII.GetBytes(points.ToString())).Substring(0, 16),
                RiverCacheHash(generator));
        }

        /// <summary>Sector graph of the biome map: every sector in list order (fields, alt biomes, neighbours as list
        /// indices), each biome's point lists, and the per-cell sector index.</summary>
        private static string HashSectors(AltBiomeWorldData data)
        {
            if (data?.Sectors == null || data.PointSectors == null)
            {
                return "-";
            }
            var index = new Dictionary<BiomeSector, int>();
            for (int i = 0; i < data.Sectors.Count; i++)
            {
                index[data.Sectors[i]] = i;
            }
            var bytes = new List<byte>(1 << 24);
            void Int(int v) => bytes.AddRange(BitConverter.GetBytes(v));
            void Flt(float v) => bytes.AddRange(BitConverter.GetBytes(v));
            foreach (BiomeSector s in data.Sectors)
            {
                Int((int)s.Biome); Int(s.EdgeCount); Flt(s.Center.x); Flt(s.Center.y); Flt(s.Min.x); Flt(s.Min.y); Flt(s.Max.x); Flt(s.Max.y);
                Int(s.MinZone.x); Int(s.MinZone.y); Int(s.MaxZone.x); Int(s.MaxZone.y); Flt(s.HeightMin); Flt(s.HeightMax); Flt(s.HeightAvg);
                Int(s.IsDiscovered ? 1 : 0); Flt(s.DistanceFromCenter);
                foreach (AltBiome alt in s.AltBiomes)
                {
                    bytes.AddRange(Encoding.UTF8.GetBytes(alt.m_name ?? ""));
                }
                foreach (BiomeSector n in s.Neighbors)
                {
                    Int(n != null && index.TryGetValue(n, out int k) ? k : -1);
                }
                Int(-2);
            }
            foreach (KeyValuePair<Heightmap.Biome, BiomeTypeInfo> b in data.Biomes)
            {
                Int((int)b.Key); Int(b.Value.Sectors.Count);
                foreach (BiomePointCoordinate p in b.Value.AllPoints)
                {
                    Int((ushort)p.x | (p.y << 16));
                }
                Int(-3);
                foreach (BiomePointCoordinate p in b.Value.AllPointsAboveSeaLevel)
                {
                    Int((ushort)p.x | (p.y << 16));
                }
                Int(-4);
            }
            foreach (BiomeSector s in data.PointSectors)
            {
                Int(s != null && index.TryGetValue(s, out int k) ? k : -1);
            }
            return data.Sectors.Count.ToString(CultureInfo.InvariantCulture) + " " + Sha(bytes.ToArray()).Substring(0, 16);
        }

        /// <summary>The generator's one-grid river cache (m_cachedRiverGrid + contents of m_cachedRiverPoints). Only meaningful
        /// right after Pregenerate: every later height lookup (any thread) moves it.</summary>
        public static string RiverCacheHash(WorldGenerator generator)
        {
            var grid = (Vector2i)HarmonyLib.AccessTools.Field(typeof(WorldGenerator), "m_cachedRiverGrid").GetValue(generator);
            var cached = (WorldGenerator.RiverPoint[])HarmonyLib.AccessTools.Field(typeof(WorldGenerator), "m_cachedRiverPoints").GetValue(generator);
            var cache = new StringBuilder().Append(grid.x).Append(',').Append(grid.y).Append(cached == null ? " null" : " " + cached.Length);
            foreach (WorldGenerator.RiverPoint p in cached ?? new WorldGenerator.RiverPoint[0])
            {
                cache.Append(Bits(p.p.x).ToString("x8")).Append(Bits(p.p.y).ToString("x8")).Append(Bits(p.w).ToString("x8")).Append(Bits(p.w2).ToString("x8"));
            }
            return grid.x + "," + grid.y + " " + (cached?.Length ?? -1) + " " + Sha(Encoding.ASCII.GetBytes(cache.ToString())).Substring(0, 16);
        }

        private static uint Bits(float value) => BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);

        private static string HashBiomes(Heightmap.BiomeIndex[,] map)
        {
            int w = map.GetLength(0), h = map.GetLength(1);
            var bytes = new byte[w * h * 4];
            int k = 0;
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    int v = (int)map[x, y];
                    bytes[k++] = (byte)v;
                    bytes[k++] = (byte)(v >> 8);
                    bytes[k++] = (byte)(v >> 16);
                    bytes[k++] = (byte)(v >> 24);
                }
            }
            return Sha(bytes);
        }

        private static string HashRivers(List<WorldGenerator.River> rivers)
        {
            var text = new StringBuilder();
            foreach (WorldGenerator.River r in rivers ?? new List<WorldGenerator.River>())
            {
                foreach (float v in new[] { r.p0.x, r.p0.y, r.p1.x, r.p1.y, r.center.x, r.center.y, r.widthMin, r.widthMax, r.curveWidth, r.curveWavelength })
                {
                    text.Append(Bits(v).ToString("x8")).Append(',');
                }
                text.Append(';');
            }
            return (rivers?.Count ?? 0).ToString(CultureInfo.InvariantCulture) + " " + Sha(Encoding.ASCII.GetBytes(text.ToString()));
        }

        private static string HashHeights(float[,] map)
        {
            var bytes = new byte[map.Length * 4];
            Buffer.BlockCopy(map, 0, bytes, 0, bytes.Length);
            return Sha(bytes);
        }

        private static string Sha(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
