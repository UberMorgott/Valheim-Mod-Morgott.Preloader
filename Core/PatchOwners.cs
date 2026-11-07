using System;

namespace Morgott.Preloader.Core
{
    /// <summary>Which Harmony owners count as foreign for <see cref="PatchGuard"/>.</summary>
    internal static class PatchOwners
    {
        public const string OwnPrefix = "morgott.valheim.preloader.";

        /// <summary>Own-pack owners whose prefixes / postfixes / finalizers on guarded methods never change their results or side effects
        /// (contract kept by the owner; their transpilers / IL manipulators still count as foreign):
        /// morgott.valheim.performance.Terrain = pause / resume of its terrain builder threads around WorldGenerator.Initialize / Deitialize
        /// (Morgott.Performance Perf\TerrainModule.cs ApplyParallel).</summary>
        public static readonly string[] Neutral = { "morgott.valheim.performance.Terrain" };

        public static bool IsOwn(string owner) => owner.StartsWith(OwnPrefix, StringComparison.Ordinal);

        /// <summary>True when a patch by <paramref name="owner"/> counts as foreign; <paramref name="body"/> = transpiler / IL manipulator.</summary>
        public static bool IsForeign(string owner, bool body) => !IsOwn(owner) && (body || Array.IndexOf(Neutral, owner) < 0);
    }
}
