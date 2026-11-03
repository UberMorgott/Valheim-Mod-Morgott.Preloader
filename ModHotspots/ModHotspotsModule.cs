using Morgott.Preloader.Core;
using HarmonyLib;

namespace Morgott.Preloader.ModHotspots
{
    /// <summary>
    /// Faster equivalents of slow startup code in other mods, matched by the shape of the helper they embed (names + IL
    /// fingerprint), each behind its own <c>[ModHotspots]</c> toggle. Installed at the end of <c>Chainloader.Start</c>:
    /// every plugin Awake (where the helpers register) has run, the menu scene (where they execute) has not loaded.
    /// </summary>
    internal static class ModHotspotsModule
    {
        public static void Install(Harmony harmony)
        {
            if (Config.ShaderReplacerFixEnabled.Value)
                Lifecycle.ChainloaderStarted += () => Log.Guard("ModHotspots ShaderReplacer", () => ShaderReplacerFix.Install(harmony));
            if (Config.VneiIndexingEnabled.Value)
                Lifecycle.ChainloaderStarted += () => Log.Guard("ModHotspots VNEI indexing", () => VneiIndexingFix.Install(harmony));
        }
    }
}
