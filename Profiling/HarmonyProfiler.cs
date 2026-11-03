using System;
using System.Reflection;
using Morgott.Preloader.Core;
using HarmonyLib;

namespace Morgott.Preloader.Profiling
{
    /// <summary>
    /// Times HarmonyX patching per owner (Harmony ID). Hooks (0Harmony 2.9.0 in BepInEx\core):
    /// <c>Harmony.PatchAll(Assembly)</c>, <c>PatchClassProcessor.Patch()</c>, <c>PatchProcessor.Patch()</c> and the
    /// internal <c>PatchFunctions.UpdateWrapper(MethodBase, PatchInfo)</c> that builds + JITs + detours the
    /// replacement. Finalizers (not postfixes) keep the depth/owner bookkeeping right when a patch throws.
    /// UpdateWrapper calls outside any Harmony API call (e.g. StartupAccelerator's deferred flush) are owned by
    /// <see cref="NoOwner"/>.
    /// </summary>
    internal static class HarmonyProfiler
    {
        public const string NoOwner = "(no Harmony API call: deferred flush)";

        /// <summary>Class processors faster than this are counted in PatchAll but not emitted as own events.</summary>
        private static readonly long MinClassTicks = System.Diagnostics.Stopwatch.Frequency / 20000; // 0.05 ms

        [ThreadStatic] private static int _depth;
        [ThreadStatic] private static string _owner;

        public static void Install(Harmony harmony)
        {
            Hook(harmony, AccessTools.Method(typeof(Harmony), nameof(Harmony.PatchAll), new[] { typeof(Assembly) }), nameof(PatchAllPrefix), nameof(PatchAllFinalizer));
            Hook(harmony, AccessTools.Method(typeof(PatchClassProcessor), nameof(PatchClassProcessor.Patch)), nameof(ProcessorPrefix), nameof(ClassFinalizer));
            Hook(harmony, AccessTools.Method(typeof(PatchProcessor), nameof(PatchProcessor.Patch)), nameof(ProcessorPrefix), nameof(ProcessorFinalizer));
            Hook(harmony, AccessTools.Method(AccessTools.TypeByName("HarmonyLib.PatchFunctions"), "UpdateWrapper"), nameof(WrapperPrefix), nameof(WrapperFinalizer));
        }

        private static void Hook(Harmony harmony, MethodBase target, string prefix, string finalizer)
        {
            if (target == null)
            {
                Log.Warning($"Harmony profiler: target for {finalizer} not found (HarmonyX changed?), skipped");
                return;
            }
            harmony.Patch(target,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(HarmonyProfiler), prefix), Priority.First),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(HarmonyProfiler), finalizer), Priority.Last));
        }

        private static long Enter(string owner)
        {
            if (_depth++ == 0) _owner = owner;
            return StartupTrace.Now();
        }

        /// <summary>Closes the span; never throws into Harmony (a failure is logged).</summary>
        private static void Exit(long start, string name, string owner, string target, bool emit = true)
        {
            try
            {
                long end = StartupTrace.Now();
                int depth = _depth--;
                if (_depth == 0) _owner = null;
                if (emit || depth == 1) StartupTrace.Complete("harmony", name, start, end, owner, target, depth: depth);
            }
            catch (Exception e)
            {
                Log.Error($"Harmony profiler ({name}): {e}");
            }
        }

        private static void PatchAllPrefix(out long __state, Harmony __instance) => __state = Enter(__instance?.Id);

        private static void PatchAllFinalizer(long __state, Harmony __instance, Assembly assembly) =>
            Exit(__state, "PatchAll", __instance?.Id, assembly?.GetName().Name);

        private static void ProcessorPrefix(out long __state, Harmony ___instance) => __state = Enter(___instance?.Id);

        private static void ClassFinalizer(long __state, Harmony ___instance, Type ___containerType) =>
            Exit(__state, "PatchClass", ___instance?.Id, ___containerType?.FullName, StartupTrace.Now() - __state >= MinClassTicks);

        private static void ProcessorFinalizer(long __state, Harmony ___instance, MethodBase ___original) =>
            Exit(__state, "Patch", ___instance?.Id, Describe(___original));

        private static void WrapperPrefix(out long __state) => __state = Enter(NoOwner);

        // A null replacement means the wrapper build was skipped (StartupAccelerator defers it) or failed.
        private static void WrapperFinalizer(long __state, MethodBase original, MethodInfo __result) =>
            Exit(__state, __result == null ? "UpdateWrapper (skipped)" : "UpdateWrapper", _depth == 1 ? NoOwner : _owner, Describe(original));

        public static string Describe(MethodBase method) =>
            method == null ? null : (method.DeclaringType?.FullName ?? "?") + "." + method.Name;
    }
}
