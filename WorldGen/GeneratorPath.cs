using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace Morgott.Preloader.WorldGen
{
    /// <summary>Methods whose code decides the world generator's output (biomes, heights, rivers): all of WorldGenerator,
    /// DUtils, FastNoise, BiomeHelpers, the AltBiomeWorldData helpers of the biome map build and the Utils helpers on the
    /// path (LerpStep WorldGenerator.cs:917, FloorToInt via ZoneSystem.GetZone in the sector build). A foreign patch on any
    /// of them turns the WorldGen replacements and caches off.</summary>
    internal static class GeneratorPath
    {
        private static List<MethodBase> _methods;

        public static IEnumerable<MethodBase> Methods => _methods ?? (_methods = new[] { typeof(WorldGenerator), typeof(DUtils), typeof(FastNoise), typeof(BiomeHelpers) }
            .SelectMany(t => AccessTools.GetDeclaredMethods(t).Where(m => !m.IsAbstract && !m.ContainsGenericParameters).Cast<MethodBase>()
                .Concat(AccessTools.GetDeclaredConstructors(t)))
            .Concat(AccessTools.GetDeclaredMethods(typeof(AltBiomeWorldData))
                .Where(m => m.Name == nameof(AltBiomeWorldData.MapSpaceToWorldSpace) || m.Name == nameof(AltBiomeWorldData.dbStep)))
            .Concat(AccessTools.GetDeclaredConstructors(typeof(AltBiomeWorldData)))
            .Concat(AccessTools.GetDeclaredMethods(typeof(Utils)).Where(m => m.Name == "LerpStep" || m.Name == "FloorToInt"))
            .ToList());
    }
}
