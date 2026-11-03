using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
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
    /// <c>RuntimeMethodHandle.GetFunctionPointer</c>). So the cache is emptied around each wrap (<see cref="Wrap"/>).
    /// Safety net for any other native crash while hooking: the method being detoured is written to
    /// <c>patch-probe.pending</c> first; if the process dies, the next launch moves it to <c>patch-probe.skip</c> and
    /// never touches it again.
    /// Limits: transpiler cost is inside the game body; a tiny patch method the JIT inlined into an already built
    /// wrapper is not seen (its cost is negligible by definition).
    /// </summary>
    internal static class PatchOwnerProbe
    {
        private static readonly Dictionary<MethodBase, (string Owner, string Target)> Owners =
            new Dictionary<MethodBase, (string, string)>();

        [ThreadStatic] private static SpanStack _spans;

        private static string Dir => Path.Combine(Paths.BepInExRootPath, "Morgott.Preloader");

        public static void Install(Harmony harmony)
        {
            Directory.CreateDirectory(Dir);
            string pendingPath = Path.Combine(Dir, "patch-probe.pending");
            string skipPath = Path.Combine(Dir, "patch-probe.skip");
            var skip = new HashSet<string>(File.Exists(skipPath) ? File.ReadAllLines(skipPath) : new string[0]);
            if (File.Exists(pendingPath))
            {
                string crashed = File.ReadAllText(pendingPath).Trim();
                if (crashed.Length > 0 && skip.Add(crashed))
                {
                    File.AppendAllText(skipPath, crashed + Environment.NewLine);
                    Log.Warning($"Patch owner probe: the last launch died while hooking {crashed}; it is skipped from now on");
                }
                File.Delete(pendingPath);
            }

            if (ResolveCache == null)
            {
                Log.Warning("Patch owner probe: MonoMod.Utils has no ReflectionHelper.ResolveReflectionCache (other version), mod patch methods are not timed");
            }
            _harmony = harmony;
            _pendingPath = pendingPath;
            _skip = skip;
            int wrapped = Scan();
            Log.Info($"Patch owner probe: {wrapped} mod patch methods on game startup/world-load methods and Jotunn event handlers timed, {skip.Count} skipped");
            // Patches and Jotunn subscriptions added after Chainloader.Start (plugin Start, menu code) are picked up
            // at the main menu, before any world load.
            GameLifecycleProbe.MenuReady += () =>
            {
                int late = Scan();
                Log.Info($"Patch owner probe: {late} more mod patch methods / Jotunn handlers timed at the main menu");
            };
        }

        private static Harmony _harmony;
        private static string _pendingPath;
        private static HashSet<string> _skip;

        private static int Scan()
        {
            Harmony harmony = _harmony;
            string pendingPath = _pendingPath;
            HashSet<string> skip = _skip;
            int wrapped = 0;
            IEnumerable<MethodInfo> targets = GameLifecycleProbe.Targets().Concat(GameLifecycleProbe.WorldTargets());
            foreach (MethodInfo target in targets.Where(m => m != null))
            {
                Patches info = Harmony.GetPatchInfo(target);
                if (info == null)
                {
                    continue;
                }
                foreach (Patch patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers))
                {
                    // Morgott.Preloader's own patches stay unwrapped, except WorldGen: its prefixes replace vanilla bodies
                    // (parallel GenerateBiomePoints), so without a name their time read as "untimed mod patches".
                    bool own = patch.owner.StartsWith(PatchGuard.OwnPrefix, StringComparison.Ordinal) &&
                               patch.owner != PreloaderPatcher.WorldGenHarmonyId;
                    if (!own &&
                        Wrap(harmony, patch.PatchMethod, patch.owner, target.DeclaringType?.Name + "." + target.Name, pendingPath, skip))
                    {
                        wrapped++;
                    }
                }
            }
            foreach ((MethodInfo method, string eventName) in JotunnEventSubscribers())
            {
                if (Wrap(harmony, method, "Jotunn event handler", "Jotunn " + eventName, pendingPath, skip))
                {
                    wrapped++;
                }
            }
            File.Delete(pendingPath);
            return wrapped;
        }

        private static bool Wrap(Harmony harmony, MethodInfo method, string owner, string target, string pendingPath, HashSet<string> skip)
        {
            string id = Identity(method);
            if (Owners.ContainsKey(method) || skip.Contains(id))
            {
                return false;
            }
            if (ResolveCache == null)
            {
                return false;
            }
            try
            {
                File.WriteAllText(pendingPath, id);
                // The IL copies of this method are resolved against an empty cache, so every generic instantiation in
                // them is built from this method's own assembly; emptied again afterwards so no later patch (ours or a
                // mod's) inherits this assembly's instantiations under the shared keys.
                ClearResolveCache();
                harmony.Patch(method,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(PatchOwnerProbe), nameof(Prefix)), Priority.First),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(PatchOwnerProbe), nameof(Finalizer)), Priority.Last));
                Owners[method] = (owner + " [" + method.Module.Assembly.GetName().Name + "]", target);
                return true;
            }
            catch (Exception e)
            {
                Log.Warning($"Patch owner probe: cannot time {id} ({owner}): {e.Message}");
                return false;
            }
            finally
            {
                ClearResolveCache();
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
            if (ResolveCache == null)
            {
                return;
            }
            lock (ResolveCache)
            {
                ResolveCache.Clear();
            }
        }

        /// <summary>Handlers subscribed to the Jotunn events raised from Jotunn's patches on the menu and world-load
        /// methods (static <c>event Action</c> backing fields, verified in the installed Jotunn 2.30.2 with ilspycmd:
        /// PrefabManager/ItemManager/PieceManager/CreatureManager/ZoneManager/DungeonManager/GUIManager/
        /// LocalizationManager/MinimapManager/AssetManager). A missing field (other Jotunn version) is skipped.</summary>
        private static IEnumerable<(MethodInfo, string)> JotunnEventSubscribers()
        {
            Assembly jotunn = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Jotunn");
            if (jotunn == null)
            {
                yield break;
            }
            foreach ((string type, string field) in new[]
                     {
                         ("Jotunn.Managers.PrefabManager", "OnVanillaPrefabsAvailable"),
                         ("Jotunn.Managers.PrefabManager", "OnPrefabsRegistered"),
                         ("Jotunn.Managers.ItemManager", "OnVanillaItemsAvailable"),
                         ("Jotunn.Managers.ItemManager", "OnKitbashItemsAvailable"),
                         ("Jotunn.Managers.ItemManager", "OnItemsRegisteredFejd"),
                         ("Jotunn.Managers.ItemManager", "OnItemsRegistered"),
                         ("Jotunn.Managers.PieceManager", "OnPiecesRegistered"),
                         ("Jotunn.Managers.CreatureManager", "OnVanillaCreaturesAvailable"),
                         ("Jotunn.Managers.CreatureManager", "OnCreaturesRegistered"),
                         ("Jotunn.Managers.ZoneManager", "OnVanillaLocationsAvailable"),
                         ("Jotunn.Managers.ZoneManager", "OnLocationsRegistered"),
                         ("Jotunn.Managers.ZoneManager", "OnVanillaClutterAvailable"),
                         ("Jotunn.Managers.ZoneManager", "OnClutterRegistered"),
                         ("Jotunn.Managers.ZoneManager", "OnVanillaVegetationAvailable"),
                         ("Jotunn.Managers.ZoneManager", "OnVegetationRegistered"),
                         ("Jotunn.Managers.DungeonManager", "OnVanillaRoomsAvailable"),
                         ("Jotunn.Managers.DungeonManager", "OnRoomsRegistered"),
                         ("Jotunn.Managers.GUIManager", "OnCustomGUIAvailable"),
                         ("Jotunn.Managers.GUIManager", "OnPixelFixCreated"),
                         ("Jotunn.Managers.LocalizationManager", "OnLocalizationAdded"),
                         ("Jotunn.Managers.MinimapManager", "OnVanillaMapAvailable"),
                         ("Jotunn.Managers.MinimapManager", "OnVanillaMapDataLoaded"),
                         ("Jotunn.Managers.AssetManager", "OnSoftReferenceableAssetsReady"),
                     })
            {
                FieldInfo info = jotunn.GetType(type)?.GetField(field, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (info?.GetValue(null) is Delegate handlers)
                {
                    foreach (Delegate handler in handlers.GetInvocationList())
                    {
                        if (handler.Method is MethodInfo method && !method.IsAbstract)
                        {
                            yield return (method, field);
                        }
                    }
                }
            }
        }

        /// <summary>Stable across launches of the same build: assembly name + MVID + metadata token + readable name.</summary>
        internal static string Identity(MethodInfo method) =>
            method.Module.Assembly.GetName().Name + "|" + method.Module.ModuleVersionId.ToString("N") + "|" +
            method.MetadataToken.ToString("x8") + "|" + HarmonyProfiler.Describe(method);

        private static void Prefix()
        {
            (_spans ?? (_spans = new SpanStack())).Push();
        }

        private static void Finalizer(MethodBase __originalMethod)
        {
            long start = _spans?.Pop() ?? 0;
            if (start == 0 || !Owners.TryGetValue(__originalMethod, out (string Owner, string Target) info))
            {
                return;
            }
            StartupTrace.Complete("game.patch", info.Owner, start, StartupTrace.Now(), HarmonyProfiler.Describe(__originalMethod), info.Target);
        }
    }
}
