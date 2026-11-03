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
    /// HarmonyX 2.9 rebuilds a method's whole replacement (<c>PatchFunctions.UpdateWrapper</c>: IL copy + every patch +
    /// JIT + detour) per registered patch, so a method patched by N classes of one <c>Harmony.PatchAll(assembly)</c> is
    /// built N times. Inside one outermost PatchAll(Assembly) on a thread this module records the method instead and
    /// builds each once when that PatchAll returns (finalizer: also when it throws), from the same <c>PatchInfo</c>.
    /// Registration is untouched; <c>Harmony.Patch</c> / <c>PatchAll(Type)</c> build immediately as before.
    /// Inlining: Mono inlines small callees into a freshly JIT-ed wrapper, and a later detour on such a callee is
    /// bypassed (StartupAccelerator once lost MorgottTweaks' prefix on <c>FejdStartup.TryPlayIntroCinematic</c> this way),
    /// so every recorded method is pinned (<c>DetourHelper.Pin</c>, same as its detour does) at the moment it would
    /// have been detoured.
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
            harmony.Patch(updateWrapper, prefix: Hook(nameof(UpdateWrapperPrefix)));
            // Finalizer First: runs before the profiler's Last finalizer, so the flush is timed inside this PatchAll.
            harmony.Patch(patchAll, prefix: Hook(nameof(PatchAllPrefix)), finalizer: Hook(nameof(PatchAllFinalizer)));
            Lifecycle.MenuReady += () =>
                Log.Info($"HarmonyBatching: {_batches} PatchAll calls, {_deferred} wrapper builds deferred -> {_built} built until the main menu");
        }

        private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(typeof(HarmonyBatcher), name), Priority.First);

        private static void PatchAllPrefix() => _depth++;

        private static Exception PatchAllFinalizer(Exception __exception)
        {
            if (--_depth > 0) return __exception;
            Exception buildError = Flush();
            return __exception ?? buildError;
        }

        private static bool UpdateWrapperPrefix(MethodBase original, ref MethodInfo __result)
        {
            if (_depth == 0 || _flushing || original == null) return true;
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

        /// <summary>Builds every recorded method once, in first-patched order. A failing build does not stop the others;
        /// the first failure is rethrown from PatchAll, where the immediate build would have thrown.</summary>
        private static Exception Flush()
        {
            if (_pending == null || _pending.Count == 0) return null;
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
                            _addReplacementOriginal(method, _updateWrapper(method, method.ToPatchInfo()));
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
                foreach (MethodBase method in pinned) method.Unpin();
            }
            return first;
        }

        private static string Describe(MethodBase method) => (method.DeclaringType?.FullName ?? "?") + "." + method.Name;
    }
}
