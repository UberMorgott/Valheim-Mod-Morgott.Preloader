using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Morgott.Preloader.Core;
using HarmonyLib;
using MonoMod.Utils;

namespace Morgott.Preloader.Profiling
{
    /// <summary>
    /// Opt-in (<c>[Profiler] TimeModPatches</c>): splits the "mods' patches" part of the hooked game methods by
    /// owner. After Chainloader.Start every prefix/postfix/finalizer that another Harmony ID put on a
    /// <see cref="GameLifecycleProbe"/> target is itself patched with a First prefix / Last finalizer timing pair.
    /// Category <c>game.patch</c>, name = owner Harmony ID + [assembly] (helper IDs such as
    /// org.bepinex.helpers.ItemManager are shared by many mods), detail = patch method, detail2 = patched game method.
    /// Mixed-assembly IL copies: Harmony/MonoMod detour a method through IL copies of it (DynamicMethods), whose
    /// tokens MonoMod resolves through a static cache keyed by Cecil full name + the assembly of the declaring/element
    /// type only. A generic instantiation over a mod type, e.g. <c>List&lt;PieceManager.BuildPiece&gt;.Enumerator</c>, is
    /// therefore keyed by mscorlib, and the helper libraries bundled into many mods (ItemManager, PieceManager ...)
    /// define the same full names in every mod: the copy of Armory's <c>PieceManager.BuildPiece.Patch_FejdStartup</c>
    /// got a local of Warfare's <c>List&lt;BuildPiece&gt;.Enumerator</c> while its calls return Armory's, and Mono's JIT
    /// aborted the process natively (<c>g_assert (var-&gt;klass == klass)</c>, method-to-ir.c, inside
    /// <c>RuntimeMethodHandle.GetFunctionPointer</c>). So the cache is emptied around each wrap (<see cref="GuardedPatch"/>).
    /// Safety net for any other native crash while hooking: the method being detoured is written to
    /// <c>patch-probe.pending</c> first; if the process dies, the next launch moves it to <c>patch-probe.skip</c> and
    /// never touches it again (<see cref="CrashGuard"/>).
    /// Limits: transpiler cost is inside the game body; a tiny patch method the JIT inlined into an already built
    /// wrapper is not seen (its cost is negligible by definition).
    /// </summary>
    internal static class PatchOwnerProbe
    {
        private static readonly Dictionary<MethodBase, (string Owner, string Target)> Owners =
            new Dictionary<MethodBase, (string, string)>();

        [ThreadStatic] private static SpanStack _spans;

        private static Harmony _harmony;
        private static string _pendingPath;
        private static HashSet<string> _skip;
        private static HarmonyMethod _prefix, _finalizer;

        public static void Install(Harmony harmony)
        {
            _skip = CrashGuard("patch-probe", "Patch owner probe", out _pendingPath);
            if (ResolveCache == null)
            {
                Log.Warning("Patch owner probe: MonoMod.Utils has no ReflectionHelper.ResolveReflectionCache (other version), mod patch methods are not timed");
            }
            _harmony = harmony;
            _prefix = new HarmonyMethod(AccessTools.Method(typeof(PatchOwnerProbe), nameof(Prefix)), Priority.First);
            _finalizer = new HarmonyMethod(AccessTools.Method(typeof(PatchOwnerProbe), nameof(Finalizer)), Priority.Last);
            Log.Info($"Patch owner probe: {Scan()} mod patch methods on game startup/world-load methods and Jotunn event handlers timed, {_skip.Count} skipped");
            // Patches and Jotunn subscriptions added after Chainloader.Start (plugin Start, menu code) are picked up
            // at the main menu, before any world load.
            GameLifecycleProbe.MenuReady += () => Log.Info($"Patch owner probe: {Scan()} more mod patch methods / Jotunn handlers timed at the main menu");
        }

        /// <summary>Native-crash guard of a wrapping pass: <c>&lt;name&gt;.pending</c> left by a launch that died while
        /// patching moves to <c>&lt;name&gt;.skip</c>. Returns the skip set.</summary>
        internal static HashSet<string> CrashGuard(string name, string label, out string pendingPath)
        {
            Directory.CreateDirectory(Profiler.OutputDir);
            pendingPath = Path.Combine(Profiler.OutputDir, name + ".pending");
            string skipPath = Path.Combine(Profiler.OutputDir, name + ".skip");
            var skip = new HashSet<string>(File.Exists(skipPath) ? File.ReadAllLines(skipPath) : new string[0]);
            if (!File.Exists(pendingPath)) return skip;
            string crashed = File.ReadAllText(pendingPath).Trim();
            if (crashed.Length > 0 && skip.Add(crashed))
            {
                File.AppendAllText(skipPath, crashed + Environment.NewLine);
                Log.Warning($"{label}: the last launch died while patching {crashed}; it is skipped from now on");
            }
            File.Delete(pendingPath);
            return skip;
        }

        /// <summary>Patch with <paramref name="id"/> in the pending file and MonoMod's resolve cache emptied before and
        /// after: the IL copies of this method are resolved against an empty cache, so every generic instantiation in
        /// them is built from this method's own assembly, and no later patch (ours or a mod's) inherits them under the shared keys.</summary>
        internal static void GuardedPatch(Harmony harmony, MethodInfo method, string pendingPath, string id, HarmonyMethod prefix, HarmonyMethod finalizer)
        {
            try
            {
                File.WriteAllText(pendingPath, id);
                ClearResolveCache();
                harmony.Patch(method, prefix: prefix, finalizer: finalizer);
            }
            finally
            {
                ClearResolveCache();
            }
        }

        private static int Scan()
        {
            int wrapped = 0;
            foreach (MethodInfo target in GameLifecycleProbe.Targets().Concat(GameLifecycleProbe.WorldTargets()).Where(m => m != null))
            {
                Patches info = Harmony.GetPatchInfo(target);
                if (info == null) continue;
                foreach (Patch patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers))
                {
                    // Morgott.Preloader's own patches stay unwrapped, except WorldGen: its prefixes replace vanilla bodies
                    // (parallel GenerateBiomePoints), so without a name their time read as "untimed mod patches".
                    bool own = patch.owner.StartsWith(PatchGuard.OwnPrefix, StringComparison.Ordinal) && patch.owner != PreloaderPatcher.WorldGenHarmonyId;
                    if (!own && Wrap(patch.PatchMethod, patch.owner, target.DeclaringType?.Name + "." + target.Name)) wrapped++;
                }
            }
            foreach ((MethodInfo method, string eventName) in JotunnEventSubscribers())
            {
                if (Wrap(method, "Jotunn event handler", "Jotunn " + eventName)) wrapped++;
            }
            File.Delete(_pendingPath);
            return wrapped;
        }

        private static bool Wrap(MethodInfo method, string owner, string target)
        {
            string id = Identity(method);
            if (Owners.ContainsKey(method) || _skip.Contains(id) || ResolveCache == null) return false;
            try
            {
                GuardedPatch(_harmony, method, _pendingPath, id, _prefix, _finalizer);
                Owners[method] = (owner + " [" + method.Module.Assembly.GetName().Name + "]", target);
                return true;
            }
            catch (Exception e)
            {
                Log.Warning($"Patch owner probe: cannot time {id} ({owner}): {e.Message}");
                return false;
            }
        }

        /// <summary>MonoMod.Utils 22.01.29 (BepInEx\core) <c>ReflectionHelper.ResolveReflectionCache</c>: Cecil reference
        /// -> reflection member, keyed by full name + the assembly of the declaring/element type only (decompiled
        /// <c>_ResolveReflection</c>). Null when this MonoMod has no such field; wrapping is then off, because the
        /// mixed-up copies below cannot be ruled out.</summary>
        internal static readonly IDictionary ResolveCache =
            AccessTools.Field(typeof(ReflectionHelper), "ResolveReflectionCache")?.GetValue(null) as IDictionary;

        internal static void ClearResolveCache()
        {
            if (ResolveCache == null) return;
            lock (ResolveCache)
            {
                ResolveCache.Clear();
            }
        }

        /// <summary>Handlers subscribed to the Jotunn events raised from Jotunn's patches on the menu and world-load
        /// methods (static <c>event Action</c> backing fields, verified in the installed Jotunn 2.30.2 with ilspycmd).
        /// A missing field (other Jotunn version) is skipped.</summary>
        private static IEnumerable<(MethodInfo, string)> JotunnEventSubscribers()
        {
            Assembly jotunn = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Jotunn");
            if (jotunn == null) yield break;
            foreach ((string type, string[] fields) in new[]
                     {
                         ("PrefabManager", new[] { "OnVanillaPrefabsAvailable", "OnPrefabsRegistered" }),
                         ("ItemManager", new[] { "OnVanillaItemsAvailable", "OnKitbashItemsAvailable", "OnItemsRegisteredFejd", "OnItemsRegistered" }),
                         ("PieceManager", new[] { "OnPiecesRegistered" }),
                         ("CreatureManager", new[] { "OnVanillaCreaturesAvailable", "OnCreaturesRegistered" }),
                         ("ZoneManager", new[] { "OnVanillaLocationsAvailable", "OnLocationsRegistered", "OnVanillaClutterAvailable", "OnClutterRegistered",
                             "OnVanillaVegetationAvailable", "OnVegetationRegistered" }),
                         ("DungeonManager", new[] { "OnVanillaRoomsAvailable", "OnRoomsRegistered" }),
                         ("GUIManager", new[] { "OnCustomGUIAvailable", "OnPixelFixCreated" }),
                         ("LocalizationManager", new[] { "OnLocalizationAdded" }),
                         ("MinimapManager", new[] { "OnVanillaMapAvailable", "OnVanillaMapDataLoaded" }),
                         ("AssetManager", new[] { "OnSoftReferenceableAssetsReady" }),
                     })
            {
                foreach (string field in fields)
                {
                    FieldInfo info = jotunn.GetType("Jotunn.Managers." + type)?.GetField(field, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    if (!(info?.GetValue(null) is Delegate handlers)) continue;
                    foreach (Delegate handler in handlers.GetInvocationList())
                    {
                        if (handler.Method is MethodInfo method && !method.IsAbstract) yield return (method, field);
                    }
                }
            }
        }

        /// <summary>Stable across launches of the same build: assembly name + MVID + metadata token + readable name.</summary>
        internal static string Identity(MethodInfo method) =>
            method.Module.Assembly.GetName().Name + "|" + method.Module.ModuleVersionId.ToString("N") + "|" +
            method.MetadataToken.ToString("x8") + "|" + HarmonyProfiler.Describe(method);

        private static void Prefix() => (_spans ??= new SpanStack()).Push();

        private static void Finalizer(MethodBase __originalMethod)
        {
            long start = _spans?.Pop() ?? 0;
            if (start != 0 && Owners.TryGetValue(__originalMethod, out (string Owner, string Target) info))
                StartupTrace.Complete("game.patch", info.Owner, start, StartupTrace.Now(), HarmonyProfiler.Describe(__originalMethod), info.Target);
        }
    }
}
