using System;
using System.IO;
using BepInEx.Configuration;
using Morgott.Preloader.Core;
using HarmonyLib;

namespace Morgott.Preloader.Profiling
{
    /// <summary>
    /// Times every <c>ConfigFile.Save()</c> (BepInEx 5.4.23: rewrites the whole .cfg; <c>Bind</c> and every value
    /// change call it while <c>SaveOnConfigSet</c> is true). Category <c>config</c>, detail = file name. Saves can
    /// come from any thread, hence a thread-static span stack.
    /// </summary>
    internal static class ConfigSaveProfiler
    {
        [ThreadStatic] private static SpanStack _spans;

        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.DeclaredMethod(typeof(ConfigFile), nameof(ConfigFile.Save)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ConfigSaveProfiler), nameof(Prefix)), Priority.First),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(ConfigSaveProfiler), nameof(Finalizer)), Priority.Last));
        }

        private static void Prefix() => (_spans ?? (_spans = new SpanStack())).Push();

        private static void Finalizer(ConfigFile __instance)
        {
            long start = _spans?.Pop() ?? 0;
            if (start != 0)
            {
                StartupTrace.Complete("config", "ConfigFile.Save", start, StartupTrace.Now(), Path.GetFileName(__instance.ConfigFilePath));
            }
        }
    }
}
