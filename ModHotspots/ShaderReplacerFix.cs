using System;
using System.Collections.Generic;
using System.Reflection;
using Morgott.Preloader.Core;
using HarmonyLib;
using UnityEngine;

namespace Morgott.Preloader.ModHotspots
{
    /// <summary>
    /// blacks7ar's ShaderReplacer helper (Harmony ID <c>blacks7ar.utilities.ShaderReplacer</c>, embedded in OreMines
    /// and others): its <c>ReplaceShaderPatch</c> postfix on <c>FejdStartup.Awake</c> walks every material of the
    /// registered prefabs and, per material, calls <c>Resources.FindObjectsOfTypeAll&lt;Shader&gt;()</c> and compares
    /// names against every loaded shader (OreMines 1.x: 2.25 s here).
    /// Replacement: the same walk (same GameObjects, <c>GetComponentsInChildren&lt;Renderer&gt;(true)</c>, same null
    /// checks, same order) and the same assignments (every shader whose name equals the material's shader name, in
    /// <c>FindObjectsOfTypeAll</c> order, so the last one wins), with the shader list read once and grouped by name.
    /// Only applied to a <c>ReplaceShaderPatch</c> whose IL fingerprint is one of <see cref="KnownSlow"/>; the later
    /// variant of the helper that already builds a name dictionary (SeedBed) has another fingerprint and is left alone.
    /// </summary>
    internal static class ShaderReplacerFix
    {
        /// <summary>IL fingerprints (<see cref="IlShape"/>) of the per-material FindObjectsOfTypeAll variant, verified
        /// by decompiling: OreMines 1.x (<c>OreMines.Functions.ShaderReplacer</c>).</summary>
        private static readonly HashSet<string> KnownSlow = new HashSet<string>
        {
            "f1cf1862d4ff62924edea4d4",
        };

        private static readonly Dictionary<MethodBase, FieldInfo> Targets = new Dictionary<MethodBase, FieldInfo>();

        public static void Install(Harmony harmony)
        {
            MethodInfo awake = AccessTools.DeclaredMethod(typeof(FejdStartup), "Awake");
            Patches info = Harmony.GetPatchInfo(awake);
            if (info == null)
            {
                return;
            }
            foreach (Patch patch in info.Postfixes)
            {
                MethodInfo method = patch.PatchMethod;
                if (method.Name != "ReplaceShaderPatch" || Targets.ContainsKey(method))
                {
                    continue;
                }
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
            if (!Targets.TryGetValue(__originalMethod, out FieldInfo list))
            {
                return true;
            }
            Replace((List<GameObject>)list.GetValue(null));
            return false;
        }

        private static void Replace(List<GameObject> gameObjects)
        {
            Dictionary<string, List<Shader>> byName = null;
            foreach (GameObject gameObject in gameObjects)
            {
                foreach (Renderer renderer in gameObject.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null)
                    {
                        continue;
                    }
                    foreach (Material material in renderer.sharedMaterials)
                    {
                        if (material == null)
                        {
                            continue;
                        }
                        // Read once, on the first material, like the original's first call; nothing in this walk
                        // creates or destroys shaders.
                        byName = byName ?? GroupByName(Resources.FindObjectsOfTypeAll<Shader>());
                        if (byName.TryGetValue(material.shader.name, out List<Shader> shaders))
                        {
                            foreach (Shader shader in shaders)
                            {
                                material.shader = shader;
                            }
                        }
                    }
                }
            }
        }

        private static Dictionary<string, List<Shader>> GroupByName(Shader[] shaders)
        {
            var byName = new Dictionary<string, List<Shader>>(StringComparer.Ordinal);
            foreach (Shader shader in shaders)
            {
                string name = shader.name;
                if (!byName.TryGetValue(name, out List<Shader> list))
                {
                    byName[name] = list = new List<Shader>();
                }
                list.Add(shader);
            }
            return byName;
        }
    }
}
