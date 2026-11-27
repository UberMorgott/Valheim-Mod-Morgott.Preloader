using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
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
            var methods = new List<MethodBase>(Harmony.GetAllPatchedMethods());
            for (int i = 0; i < methods.Count; i++)
            {
                MethodBase method = methods[i];
                Patches info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                var sb = new StringBuilder();
                AppendKind(sb, "prefix", info.Prefixes);
                AppendKind(sb, "postfix", info.Postfixes);
                AppendKind(sb, "transpiler", info.Transpilers);
                AppendKind(sb, "finalizer", info.Finalizers);
                AppendKind(sb, "ilmanipulator", info.ILManipulators);
                if (sb.Length > 0) lines.Add(AutoId.Replace(method.FullDescription() + "\n" + sb.ToString(), "harmony-auto-*"));
            }
            lines.Sort(StringComparer.Ordinal);
            string path = Path.Combine(Paths.BepInExRootPath, "Morgott.Preloader", "harmony-state.txt");
            AtomicFile.WriteAllText(path, string.Concat(lines));
            Log.Info($"Harmony state: {lines.Count.ToString(CultureInfo.InvariantCulture)} patched methods written to {path}");
        }

        private static void AppendKind(StringBuilder sb, string kind, IReadOnlyList<Patch> patches)
        {
            for (int i = 0; i < patches.Count; i++)
            {
                Patch p = patches[i];
                if (PatchGuard.IsOwn(p.owner)) continue;
                sb.Append("  ").Append(kind).Append(' ').Append(p.owner).Append(" prio=")
                  .Append(p.priority.ToString(CultureInfo.InvariantCulture)).Append(' ')
                  .Append(p.PatchMethod.FullDescription()).Append('\n');
            }
        }
    }
}
