using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace Morgott.Preloader.WorldGen
{
    /// <summary>Methods whose code decides the world generator's output (biomes, heights, rivers): WorldGenerator, DUtils,
    /// FastNoise, BiomeHelpers (all methods and constructors) and the AltBiomeWorldData helpers the biome map build calls, and the Utils helpers they use (LerpStep, FloorToInt).
    /// A foreign patch on any of them turns the WorldGen replacements and caches off.</summary>
    internal static class GeneratorPath
    {
        private static List<MethodBase> _methods;

        public static IEnumerable<MethodBase> Methods => _methods ?? (_methods = Build());

        private static List<MethodBase> Build()
        {
            var list = new List<MethodBase>();
            foreach (Type type in new[] { typeof(WorldGenerator), typeof(DUtils), typeof(FastNoise), typeof(BiomeHelpers) })
            {
                list.AddRange(AccessTools.GetDeclaredMethods(type).Where(m => !m.IsAbstract && !m.ContainsGenericParameters));
                list.AddRange(AccessTools.GetDeclaredConstructors(type));
            }
            list.AddRange(AccessTools.GetDeclaredMethods(typeof(AltBiomeWorldData))
                .Where(m => m.Name == nameof(AltBiomeWorldData.MapSpaceToWorldSpace) || m.Name == nameof(AltBiomeWorldData.dbStep)));
            list.AddRange(AccessTools.GetDeclaredConstructors(typeof(AltBiomeWorldData)));
            // Utils helpers on the path: LerpStep (WorldGenerator.cs:917), FloorToInt (ZoneSystem.GetZone, ZoneSystem.cs:2969,
            // used by the sector build).
            list.AddRange(AccessTools.GetDeclaredMethods(typeof(Utils)).Where(m => m.Name == "LerpStep" || m.Name == "FloorToInt"));
            return list;
        }
    }
}