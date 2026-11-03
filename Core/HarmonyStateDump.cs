using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;

namespace Morgott.Preloader.Core
{
    /// <summary>
    /// <c>[Diagnostics] DumpHarmonyState</c>: at the main menu, writes every patched method (sorted) with its patches
    /// per kind in Harmony's own order (owner, priority, patch method) to BepInEx\Morgott.Preloader\harmony-state.txt, so two
    /// setups can be diffed. Morgott.Preloader's own patches (owner <c>morgott.valheim.preloader.*</c>) are left out; a method
    /// patched only by Morgott.Preloader is left out entirely. Patch indices are left out: they count Morgott.Preloader's own
    /// patches too.
    /// </summary>
    internal static class HarmonyStateDump
    {
        private const string OwnPrefix = "morgott.valheim.preloader.";
        private static readonly Regex AutoId = new Regex("harmony-auto-[0-9a-f-]+");

        public static void Install() => Lifecycle.MenuReady += () => Log.Guard("Harmony state dump", Write);

        private static void Write()
        {
            var lines = new List<string>();
            foreach (MethodBase method in Harmony.GetAllPatchedMethods())
            {
                Patches info = Harmony.GetPatchInfo(method);
                if (info == null)
                {
                    continue;
                }
                var sb = new StringBuilder();
                bool any = Kind(sb, "prefix", info.Prefixes) | Kind(sb, "postfix", info.Postfixes) |
                           Kind(sb, "transpiler", info.Transpilers) | Kind(sb, "finalizer", info.Finalizers) |
                           Kind(sb, "ilmanipulator", info.ILManipulators);
                if (any)
                {
                    // HarmonyX names anonymous instances harmony-auto-<new guid>, different on every launch.
                    lines.Add(AutoId.Replace(method.FullDescription() + "\n" + sb, "harmony-auto-*"));
                }
            }
            lines.Sort(System.StringComparer.Ordinal);
            string path = Path.Combine(Paths.BepInExRootPath, "Morgott.Preloader", "harmony-state.txt");
            AtomicFile.WriteAllText(path, string.Join("", lines.ToArray()));
            Log.Info($"Harmony state: {lines.Count} patched methods written to {path}");
        }

        private static bool Kind(StringBuilder sb, string kind, IEnumerable<Patch> patches)
        {
            bool any = false;
            foreach (Patch p in patches.Where(p => !p.owner.StartsWith(OwnPrefix)))
            {
                sb.Append("  ").Append(kind).Append(' ').Append(p.owner).Append(" prio=").Append(p.priority)
                  .Append(' ').Append(p.PatchMethod.FullDescription()).Append('\n');
                any = true;
            }
            return any;
        }
    }
}
