using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;

namespace Morgott.Preloader.Core
{
    /// <summary>
    /// <c>[Diagnostics] DumpHarmonyState</c>: at the main menu, writes every patched method (sorted) with its foreign
    /// patches per kind in Harmony's order (owner, priority, patch method) to BepInEx\Morgott.Preloader\harmony-state.txt,
    /// so two setups can be diffed. Own patches and patch indices (they count own patches too) are left out.
    /// </summary>
    internal static class HarmonyStateDump
    {
        // HarmonyX names anonymous instances harmony-auto-<new guid>, different on every launch.
        private static readonly Regex AutoId = new Regex("harmony-auto-[0-9a-f-]+");

        public static void Install() => Lifecycle.MenuReady += () => Log.Guard("Harmony state dump", Write);

        private static void Write()
        {
            var lines = new List<string>();
            foreach (MethodBase method in Harmony.GetAllPatchedMethods())
            {
                Patches info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                string patches = Kind("prefix", info.Prefixes) + Kind("postfix", info.Postfixes) + Kind("transpiler", info.Transpilers) +
                                 Kind("finalizer", info.Finalizers) + Kind("ilmanipulator", info.ILManipulators);
                if (patches.Length > 0) lines.Add(AutoId.Replace(method.FullDescription() + "\n" + patches, "harmony-auto-*"));
            }
            lines.Sort(StringComparer.Ordinal);
            string path = Path.Combine(Paths.BepInExRootPath, "Morgott.Preloader", "harmony-state.txt");
            AtomicFile.WriteAllText(path, string.Concat(lines));
            Log.Info($"Harmony state: {lines.Count} patched methods written to {path}");
        }

        private static string Kind(string kind, IEnumerable<Patch> patches) => string.Concat(patches.Where(p => !PatchGuard.IsOwn(p.owner))
            .Select(p => "  " + kind + " " + p.owner + " prio=" + p.priority + " " + p.PatchMethod.FullDescription() + "\n"));
    }
}
