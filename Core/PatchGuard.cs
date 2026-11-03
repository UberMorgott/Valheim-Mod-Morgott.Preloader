using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace Morgott.Preloader.Core
{
    /// <summary>Foreign (non-Morgott.Preloader) Harmony patches on game methods a module replaces or reorders: such a module
    /// falls back to vanilla instead of skipping or reordering another mod's code.</summary>
    internal static class PatchGuard
    {
        public const string OwnPrefix = "morgott.valheim.preloader.";

        public static bool IsOwn(string owner) => owner.StartsWith(OwnPrefix, StringComparison.Ordinal);

        /// <summary>"Type.Method by owner" of the first foreign patch on <paramref name="method"/>, null if none.
        /// <paramref name="bodyOnly"/> = only transpilers / IL manipulators count (prefixes and postfixes still run).</summary>
        public static string Foreign(MethodBase method, bool bodyOnly = false)
        {
            Patches info = method == null ? null : Harmony.GetPatchInfo(method);
            if (info == null) return null;
            string owner = First(info.Transpilers) ?? First(info.ILManipulators) ??
                           (bodyOnly ? null : First(info.Prefixes) ?? First(info.Postfixes) ?? First(info.Finalizers));
            return owner == null ? null : method.DeclaringType?.Name + "." + method.Name + " by " + owner;
        }

        /// <summary>First foreign patch on any of <paramref name="methods"/>, null if none.</summary>
        public static string Foreign(IEnumerable<MethodBase> methods) => methods.Select(m => Foreign(m)).FirstOrDefault(f => f != null);

        private static string First(IEnumerable<Patch> patches) => patches.FirstOrDefault(p => !IsOwn(p.owner))?.owner;
    }
}
