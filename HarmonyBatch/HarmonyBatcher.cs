using System;
using System.Collections.Generic;
using System.Reflection;
using Morgott.Preloader.Core;
using HarmonyLib;
using HarmonyLib.Public.Patching;
using MonoMod.RuntimeDetour;

namespace Morgott.Preloader.HarmonyBatch
{
    /// <summary>
    /// HarmonyX 2.9 (BepInEx\core\0Harmony.dll) registers a patch in the method's <c>PatchInfo</c> and then rebuilds the
    /// whole replacement at once (<c>PatchFunctions.UpdateWrapper</c>: IL copy + every patch + JIT + detour), so a
    /// method patched by N classes of one <c>Harmony.PatchAll(assembly)</c> is built N times. Inside one outermost
    /// PatchAll call on a thread this module records the method instead of building it and builds each recorded
    /// method once when that PatchAll returns (finalizer: also when it throws), from the same <c>PatchInfo</c> the
    /// last immediate build would have used. Registration (<c>Harmony.GetPatchInfo</c>) is untouched.
    ///
    /// Inlining: a built wrapper is JIT-compiled at once; Mono inlines small callees that are not marked NoInlining
    /// yet, and a detour placed on such a callee later is bypassed by that caller. Deferring builds would reorder
    /// detours relative to caller builds (the root cause of StartupAccelerator dropping MorgottTweaks' prefix on the
    /// iterator factory <c>FejdStartup.TryPlayIntroCinematic</c>: <c>FejdStartup.Start</c>'s wrapper was built first
    /// and inlined it). So every recorded method is pinned (<c>DetourHelper.Pin</c>: MonoMod's NoInlining flag +
    /// prepare, the same call its detour makes) at the moment it would have been detoured, before any later build.
    ///
    /// Scope: only <c>PatchAll(Assembly)</c> (and <c>PatchAll()</c>, which calls it). <c>Harmony.Patch</c>,
    /// <c>PatchAll(Type)</c> and patches made outside PatchAll build immediately, as before.
    /// </summary>
    internal static class HarmonyBatcher
    {
        private static Func<MethodBase, PatchInfo, MethodInfo> _updateWrapper;
        private static Action<MethodBase, MethodInfo> _addReplacementOriginal;
        private static object _patchLock;
        private static int _batches;
        private static int _deferred;
        private static int _built;

        [ThreadStatic] private static int _depth;
        [ThreadStatic] private static bool _flushing;
        [ThreadStatic] private static List<MethodBase> _pending;
        [ThreadStatic] private static HashSet<MethodBase> _pendingSet;
        [ThreadStatic] private static List<MethodBase> _pinned;

        public static void Install(Harmony harmony)
        {
            MethodInfo updateWrapper = AccessTools.DeclaredMethod(AccessTools.TypeByName("HarmonyLib.PatchFunctions"), "UpdateWrapper",
                new[] { typeof(MethodBase), typeof(PatchInfo) });
            MethodInfo addReplacementOriginal = AccessTools.DeclaredMethod(typeof(PatchManager), "AddReplacementOriginal",
                new[] { typeof(MethodBase), typeof(MethodInfo) });
            FieldInfo locker = AccessTools.DeclaredField(typeof(PatchProcessor), "locker");
            MethodInfo patchAll = AccessTools.DeclaredMethod(typeof(Harmony), nameof(Harmony.PatchAll), new[] { typeof(Assembly) });
            if (updateWrapper == null || addReplacementOriginal == null || locker == null || patchAll == null)
            {
                Log.Warning("HarmonyBatching: HarmonyX internals not found (PatchFunctions.UpdateWrapper, PatchManager.AddReplacementOriginal, " +
                            "PatchProcessor.locker), module off");
                return;
            }
            _updateWrapper = AccessTools.MethodDelegate<Func<MethodBase, PatchInfo, MethodInfo>>(updateWrapper);
            _addReplacementOriginal = AccessTools.MethodDelegate<Action<MethodBase, MethodInfo>>(addReplacementOriginal);
            _patchLock = locker.GetValue(null);
            harmony.Patch(updateWrapper,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(HarmonyBatcher), nameof(UpdateWrapperPrefix)), Priority.First));
            // Finalizer First: runs before the profiler's Last finalizer, so the flush is timed inside this PatchAll.
            harmony.Patch(patchAll,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(HarmonyBatcher), nameof(PatchAllPrefix)), Priority.First),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(HarmonyBatcher), nameof(PatchAllFinalizer)), Priority.First));
            Lifecycle.MenuReady += () =>
                Log.Info($"HarmonyBatching: {_batches} PatchAll calls, {_deferred} wrapper builds deferred -> {_built} built until the main menu");
        }

        private static void PatchAllPrefix() => _depth++;

        private static Exception PatchAllFinalizer(Exception __exception)
        {
            if (--_depth > 0)
            {
                return __exception;
            }
            Exception buildError = Flush();
            return __exception ?? buildError;
        }

        private static bool UpdateWrapperPrefix(MethodBase original, ref MethodInfo __result)
        {
            if (_depth == 0 || _flushing || original == null)
            {
                return true;
            }
            if (_pending == null)
            {
                _pending = new List<MethodBase>();
                _pendingSet = new HashSet<MethodBase>();
                _pinned = new List<MethodBase>();
            }
            _deferred++;
            if (_pendingSet.Add(original))
            {
                _pending.Add(original);
                try
                {
                    original.Pin();
                    _pinned.Add(original);
                }
                catch (Exception e)
                {
                    // The build at flush time runs the same pin inside its detour and reports the failure there.
                    Log.Warning($"HarmonyBatching: pinning {Describe(original)} failed: {e.Message}");
                }
            }
            __result = null;
            return false;
        }

        /// <summary>Builds every recorded method once, in the order they were first patched. A failing build does not
        /// stop the others; the first failure is rethrown from PatchAll, where the immediate build would have thrown.</summary>
        private static Exception Flush()
        {
            if (_pending == null || _pending.Count == 0)
            {
                return null;
            }
            MethodBase[] methods = _pending.ToArray();
            MethodBase[] pinned = _pinned.ToArray();
            _pending.Clear();
            _pendingSet.Clear();
            _pinned.Clear();
            _batches++;
            Exception first = null;
            _flushing = true;
            try
            {
                lock (_patchLock)
                {
                    foreach (MethodBase method in methods)
                    {
                        try
                        {
                            MethodInfo replacement = _updateWrapper(method, method.ToPatchInfo());
                            _addReplacementOriginal(method, replacement);
                            _built++;
                        }
                        catch (Exception e)
                        {
                            Log.Error($"HarmonyBatching: building the patched {Describe(method)} failed: {e}");
                            first = first ?? e;
                        }
                    }
                }
            }
            finally
            {
                _flushing = false;
                // Release only our own pin; the detour built above holds its own.
                foreach (MethodBase method in pinned)
                {
                    method.Unpin();
                }
            }
            return first;
        }

        private static string Describe(MethodBase method) => (method.DeclaringType?.FullName ?? "?") + "." + method.Name;
    }
}
