using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using BepInEx;
using Morgott.Preloader.Core;
using HarmonyLib;
using UnityEngine;

namespace Morgott.Preloader.ModHotspots
{
    /// <summary>
    /// <c>[Diagnostics] DumpModHotspots</c>: at the main menu, writes the state the ModHotspots replacements produce to
    /// BepInEx\Morgott.Preloader\modhotspots-state.txt, for diffing a toggle off against on. ShaderReplacer: every material
    /// of every registered GameObject (any helper variant) with its shader, as name + index among the loaded shaders of that name.
    /// </summary>
    internal static class ModHotspotsDump
    {
        public static void Install(Harmony harmony)
        {
            Lifecycle.MenuReady += () => Log.Guard("ModHotspots state dump", Write);
            Lifecycle.ChainloaderStarted += () => Log.Guard("ModHotspots VNEI dump", () =>
            {
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(VneiIndexingFix.Guid)) InstallVnei(harmony);
            });
        }

        // VneiDump references VNEI types: only JIT-compiled once VNEI is known to be loaded.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void InstallVnei(Harmony harmony) => VneiDump.Install(harmony);

        private static void Write()
        {
            var sb = new StringBuilder();
            var groups = new Dictionary<Shader, string>();
            var counts = new Dictionary<string, int>();
            foreach (Shader shader in Resources.FindObjectsOfTypeAll<Shader>())
            {
                counts.TryGetValue(shader.name, out int n);
                counts[shader.name] = n + 1;
                groups[shader] = shader.name + "#" + n;
            }
            Patches info = Harmony.GetPatchInfo(AccessTools.DeclaredMethod(typeof(FejdStartup), "Awake"));
            foreach (Patch patch in info?.Postfixes ?? new List<Patch>().AsReadOnly())
            {
                var gameObjects = patch.PatchMethod.Name == "ReplaceShaderPatch"
                    ? AccessTools.DeclaredField(patch.PatchMethod.DeclaringType, "GOToSwap")?.GetValue(null) as List<GameObject>
                    : null;
                if (gameObjects == null) continue;
                sb.Append("== ShaderReplacer ").Append(patch.PatchMethod.DeclaringType?.FullName)
                  .Append(" [").Append(patch.PatchMethod.DeclaringType?.Assembly.GetName().Name).Append("] ")
                  .Append(gameObjects.Count).Append(" objects\n");
                for (int g = 0; g < gameObjects.Count; g++)
                {
                    GameObject gameObject = gameObjects[g];
                    foreach (Renderer renderer in gameObject.GetComponentsInChildren<Renderer>(true))
                    {
                        Material[] materials = renderer.sharedMaterials;
                        for (int m = 0; m < materials.Length; m++)
                        {
                            Material material = materials[m];
                            sb.Append(g).Append(' ').Append(gameObject.name).Append('/').Append(RelPath(renderer.transform, gameObject.transform))
                              .Append(" [").Append(m).Append("] ");
                            if (material == null)
                            {
                                sb.Append("null\n");
                                continue;
                            }
                            Shader shader = material.shader;
                            sb.Append(material.name).Append(" -> ")
                              .Append(shader == null ? "null" : groups.TryGetValue(shader, out string id) ? id + "/" + counts[shader.name] : shader.name + "#?")
                              .Append(" rq=").Append(material.renderQueue)
                              .Append(" kw=").Append(string.Join(",", material.shaderKeywords)).Append('\n');
                        }
                    }
                }
            }
            string path = Path.Combine(Paths.BepInExRootPath, "Morgott.Preloader", "modhotspots-state.txt");
            AtomicFile.WriteAllText(path, sb.ToString());
            Log.Info($"ModHotspots state written to {path}");
        }

        private static string RelPath(Transform t, Transform root)
        {
            string path = t.name;
            for (Transform p = t.parent; t != root && p != null && p != root; p = p.parent) path = p.name + "/" + path;
            return path;
        }
    }
}
