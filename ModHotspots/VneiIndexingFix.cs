using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using Morgott.Preloader.Core;
using HarmonyLib;

namespace Morgott.Preloader.ModHotspots
{
    /// <summary>
    /// VNEI (MSchmoecker, <c>com.maxsch.valheim.vnei</c>): <c>Plugin.Update</c> runs <c>Indexing.IndexAll</c> in the
    /// first frame that has a local player, i.e. the first frame of play (1.0-1.5 s here: 3026 items, 4383 recipes, one
    /// Jotunn <c>RenderManager.Render</c> per icon-less prefab = PNG read + decode from the icon cache, localization,
    /// recipe graph). Replacement (<see cref="VneiIndexer"/>): the same index, built by the same calls in the same
    /// order, a few ms per frame: items while the loading screen waits for the spawn (<c>Game.WaitingForRespawn</c>),
    /// recipes in the frames from the spawn on (other mods' recipe patches need the local player). Only applied when every reimplemented method has the IL
    /// fingerprint of VNEI 0.17.6 (<see cref="Known"/>); this class touches no VNEI type, so it is safe without VNEI.
    /// </summary>
    internal static class VneiIndexingFix
    {
        public const string Guid = "com.maxsch.valheim.vnei";

        /// <summary>IL fingerprints (<see cref="IlShape"/>) of the reimplemented methods, VNEI 0.17.6 (verified by
        /// decompiling VNEI.dll, 174080 B).</summary>
        private static readonly Dictionary<string, string> Known = new Dictionary<string, string>
        {
            ["Plugin.Update"] = "78e01205398097d8349a7133",
            ["Indexing.IndexAll"] = "1e1377d73b183cb7fca69ed3",
            ["Indexing.HasIndexed"] = "a7de1f8bb1ba94a4436746e7",
            ["Indexing.GetPrefabs"] = "e4999b88b4eb362978b04f00",
            ["Indexing.IndexItems"] = "46a0d8dbe76ad2c1f331f6c2",
            ["Indexing.DisableItems"] = "42132c9acf6ef6a0b1ecc6bf",
            ["Indexing.DisableArray"] = "44aa1b39310bd21c90c9040a",
            ["Indexing.DisableEffectList"] = "fcec064f6230fd4d0defa329",
            ["Indexing.IndexRecipes"] = "76d6cdf3b0ec494ae9fb2db8",
            ["Indexing.IndexItemRecipes"] = "6f707e5feac0598766b491a6",
            ["Indexing.TryAddItem"] = "rawa1a03dc8302ef481bdfd1cd2",
            ["Indexing.TryAddRecipeToItems"] = "rawf25c875aeb9d0db4dfa4611c",
            ["Indexing.TryAddRecipeToItemsForEach"] = "raw013d4ef0fcd727403bf9fb9a",
        };

        public static void Install(Harmony harmony)
        {
            if (!Chainloader.PluginInfos.TryGetValue(Guid, out PluginInfo info) || info.Instance == null)
            {
                return;
            }
            Assembly vnei = info.Instance.GetType().Assembly;
            var mismatches = new List<string>();
            foreach (KeyValuePair<string, string> entry in Known)
            {
                string[] parts = entry.Key.Split('.');
                Type type = vnei.GetType(parts[0] == "Plugin" ? "VNEI.Plugin" : "VNEI.Logic." + parts[0]);
                MethodInfo method = type == null ? null : AccessTools.DeclaredMethod(type, parts[1]);
                string fingerprint = method == null ? "missing" : method.IsGenericMethodDefinition ? RawIl(method) : IlShape.Fingerprint(method);
                if (fingerprint != entry.Value)
                {
                    mismatches.Add(entry.Key + " " + fingerprint);
                }
                // Another mod's patch on a method the copy replaces would be bypassed by it.
                Patches patches = method == null ? null : Harmony.GetPatchInfo(method);
                if (patches != null)
                {
                    foreach (string owner in patches.Owners)
                    {
                        if (!owner.StartsWith("morgott.valheim.preloader.", StringComparison.Ordinal))
                        {
                            mismatches.Add(entry.Key + " patched by " + owner);
                        }
                    }
                }
            }
            if (mismatches.Count > 0)
            {
                Log.Info($"ModHotspots: VNEI {info.Metadata.Version} indexing is not the known 0.17.6 code, left as is " +
                         $"({string.Join(", ", mismatches.ToArray())})");
                return;
            }
            Apply(harmony);
            Log.Info($"ModHotspots: VNEI {info.Metadata.Version} indexing moved behind the loading screen");
        }

        /// <summary>Harmony cannot read a generic method definition's instructions (NotSupportedException): its raw IL
        /// bytes instead, tokens included, which pins the exact VNEI build.</summary>
        private static string RawIl(MethodInfo method)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(method.GetMethodBody()?.GetILAsByteArray() ?? new byte[0]);
                var sb = new System.Text.StringBuilder("raw");
                for (int i = 0; i < 12; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }

        // Separate method: VneiIndexer references VNEI types, so it is only JIT-compiled (and its class loaded) once
        // VNEI is known to be loaded.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void Apply(Harmony harmony) => VneiIndexer.Install(harmony);
    }
}
