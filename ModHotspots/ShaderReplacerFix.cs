using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Morgott.Preloader.Core;
using HarmonyLib;
using UnityEngine;

namespace Morgott.Preloader.ModHotspots
{
    /// <summary>
    /// blacks7ar's ShaderReplacer helper (embedded in OreMines and others): its <c>ReplaceShaderPatch</c> postfix on
    /// <c>FejdStartup.Awake</c> calls <c>Resources.FindObjectsOfTypeAll&lt;Shader&gt;()</c> per material of the registered
    /// prefabs (OreMines 1.x: 2.25 s). Replacement: the same walk and assignments (every shader of the material's shader
    /// name, in FindObjectsOfTypeAll order, last wins) with the shader list read once and grouped by name. Only for a
    /// <c>ReplaceShaderPatch</c> whose IL fingerprint is in <see cref="KnownSlow"/>; the later dictionary variant (SeedBed)
    /// has another fingerprint and is left alone.
    /// </summary>
    internal static class ShaderReplacerFix
    {
        /// <summary>IL fingerprints (<see cref="IlShape"/>) of the per-material variant, verified by decompiling: OreMines 1.x.</summary>
        private static readonly HashSet<string> KnownSlow = new HashSet<string> { "f1cf1862d4ff62924edea4d4" };

        private static readonly Dictionary<MethodBase, FieldInfo> Targets = new Dictionary<MethodBase, FieldInfo>();

        public static void Install(Harmony harmony)
        {
            Patches info = Harmony.GetPatchInfo(AccessTools.DeclaredMethod(typeof(FejdStartup), "Awake"));
            if (info == null) return;
            foreach (Patch patch in info.Postfixes)
            {
                MethodInfo method = patch.PatchMethod;
                if (method.Name != "ReplaceShaderPatch" || Targets.ContainsKey(method)) continue;
                string mod = method.DeclaringType?.Assembly.GetName().Name;
                FieldInfo list = AccessTools.DeclaredField(method.DeclaringType, "GOToSwap");
                if (!method.IsStatic || method.ReturnType != typeof(void) || method.GetParameters().Length != 0 ||
                    list == null || !list.IsStatic || list.FieldType != typeof(List<GameObject>))
                {
                    Log.Info($"ModHotspots: ShaderReplacer in {mod} has another shape, left as is");
                    continue;
                }
                string fingerprint = IlShape.Fingerprint(method);
                if (!KnownSlow.Contains(fingerprint))
                {
                    Log.Info($"ModHotspots: ShaderReplacer in {mod} (IL {fingerprint}) is not the known slow variant, left as is");
                    continue;
                }
                harmony.Patch(method, prefix: new HarmonyMethod(AccessTools.Method(typeof(ShaderReplacerFix), nameof(Prefix))));
                Targets[method] = list;
                Log.Info($"ModHotspots: ShaderReplacer in {mod} replaced");
            }
        }

        private static bool Prefix(MethodBase __originalMethod)
        {
            if (!Targets.TryGetValue(__originalMethod, out FieldInfo list)) return true;
            ILookup<string, Shader> byName = null;
            foreach (GameObject gameObject in (List<GameObject>)list.GetValue(null))
            {
                foreach (Renderer renderer in gameObject.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null) continue;
                    foreach (Material material in renderer.sharedMaterials)
                    {
                        if (material == null) continue;
                        // Read once, on the first material like the original's first call; this walk creates no shaders.
                        byName = byName ?? Resources.FindObjectsOfTypeAll<Shader>().ToLookup(s => s.name, StringComparer.Ordinal);
                        foreach (Shader shader in byName[material.shader.name]) material.shader = shader;
                    }
                }
            }
            return false;
        }
    }
}
