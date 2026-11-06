using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using Morgott.Preloader.Core;
using HarmonyLib;

namespace Morgott.Preloader.ConfigSave
{
    /// <summary>
    /// Unity's Mono on Windows backs <c>FileSystemWatcher</c> with the managed polling <c>System.IO.DefaultWatcher</c>
    /// (game System.dll: one thread, <c>Monitor</c> sleeps 750 ms, then <c>UpdateDataAndDispatch</c> -> <c>DoFiles</c> /
    /// <c>IterateAndModifyFilesData</c> per watcher). Mods' config watchers mostly scan the same BepInEx\config folder;
    /// their file-system reads of one pass are served once through <see cref="WatchCycleCache"/>. The first scan of a
    /// new watcher (<c>StartDispatching</c>, dispatch false) reads the disk directly. Poll rate is unchanged.
    /// </summary>
    internal static class ConfigWatcherCache
    {
        private static FieldInfo _watches;
        private static long _ticks;
        private static int _cycles;

        public static void Install(Harmony harmony)
        {
            Type watcher = AccessTools.TypeByName("System.IO.DefaultWatcher");
            MethodInfo update = watcher == null ? null : AccessTools.DeclaredMethod(watcher, "UpdateDataAndDispatch");
            MethodInfo doFiles = watcher == null ? null : AccessTools.DeclaredMethod(watcher, "DoFiles");
            MethodInfo iterate = watcher == null ? null : AccessTools.DeclaredMethod(watcher, "IterateAndModifyFilesData");
            MethodInfo create = watcher == null ? null : AccessTools.DeclaredMethod(watcher, "CreateFileData");
            MethodInfo monitor = watcher == null ? null : AccessTools.DeclaredMethod(watcher, "Monitor");
            _watches = watcher == null ? null : AccessTools.DeclaredField(watcher, "watches");
            if (update == null || doFiles == null || iterate == null || create == null || monitor == null || update.ReturnType != typeof(bool) ||
                Array.FindIndex(update.GetParameters(), p => p.Name == "dispatch" && p.ParameterType == typeof(bool)) < 0)
            {
                Log.Info("ConfigWatcherCache: this Mono has no DefaultWatcher of the expected shape, watchers poll as vanilla");
                return;
            }
            var transpiler = new HarmonyMethod(AccessTools.Method(typeof(ConfigWatcherCache), nameof(Transpiler)));
            harmony.Patch(doFiles, transpiler: transpiler);
            harmony.Patch(iterate, transpiler: transpiler);
            // New files' baseline stats must come from the same pass as the compare, else a false Modified fires.
            harmony.Patch(create, transpiler: transpiler);
            // Its Thread.Sleep(750) at the top of every pass ends the previous pass's cache.
            harmony.Patch(monitor, transpiler: transpiler);
            harmony.Patch(update, prefix: new HarmonyMethod(AccessTools.Method(typeof(ConfigWatcherCache), nameof(UpdatePrefix))),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(ConfigWatcherCache), nameof(UpdateFinalizer))));
            Lifecycle.MenuReady += () => LogStats("main menu");
            Lifecycle.WorldSpawned += () => LogStats("world spawn");
        }

        private static readonly Dictionary<MethodInfo, MethodInfo> Shims = new Dictionary<MethodInfo, MethodInfo>
        {
            [AccessTools.Method(typeof(Directory), nameof(Directory.Exists), new[] { typeof(string) })] = Shim(nameof(WatchCycleCache.DirectoryExists)),
            [AccessTools.Method(typeof(File), nameof(File.Exists), new[] { typeof(string) })] = Shim(nameof(WatchCycleCache.FileExists)),
            [AccessTools.Method(typeof(Directory), nameof(Directory.GetDirectories), new[] { typeof(string) })] = Shim(nameof(WatchCycleCache.GetDirectories)),
            [AccessTools.Method(typeof(Directory), nameof(Directory.GetFileSystemEntries), new[] { typeof(string), typeof(string) })] = Shim(nameof(WatchCycleCache.GetFileSystemEntries)),
            [AccessTools.Method(typeof(File), nameof(File.GetCreationTime), new[] { typeof(string) })] = Shim(nameof(WatchCycleCache.GetCreationTime)),
            [AccessTools.Method(typeof(File), nameof(File.GetLastWriteTime), new[] { typeof(string) })] = Shim(nameof(WatchCycleCache.GetLastWriteTime)),
            [AccessTools.Method(typeof(Thread), nameof(Thread.Sleep), new[] { typeof(int) })] = Shim(nameof(WatchCycleCache.Sleep)),
        };

        private static MethodInfo Shim(string name) => AccessTools.Method(typeof(WatchCycleCache), name);

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction code in instructions)
            {
                if (code.operand is MethodInfo called && Shims.TryGetValue(called, out MethodInfo shim)) code.operand = shim;
                yield return code;
            }
        }

        private static void UpdatePrefix(object data, bool dispatch, out long __state)
        {
            __state = 0;
            if (!dispatch || data == null) return;
            WatchCycleCache.Enter();
            __state = Stopwatch.GetTimestamp();
        }

        private static Exception UpdateFinalizer(Exception __exception, long __state)
        {
            if (__state != 0)
            {
                WatchCycleCache.Exit();
                Interlocked.Add(ref _ticks, Stopwatch.GetTimestamp() - __state);
                Interlocked.Increment(ref _cycles);
            }
            return __exception;
        }

        private static void LogStats(string when)
        {
            (long real, long cached) = WatchCycleCache.Counts;
            int watchers = (_watches?.GetValue(null) as Hashtable)?.Count ?? 0;
            Log.Info($"ConfigWatcherCache at {when}: {watchers} watchers, {_cycles} watcher scans in " +
                     $"{Interlocked.Read(ref _ticks) * 1000.0 / Stopwatch.Frequency:F0} ms, file-system calls {real} real + {cached} shared");
        }
    }
}
