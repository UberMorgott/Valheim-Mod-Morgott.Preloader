using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BepInEx;
using Morgott.Preloader.Core;
using HarmonyLib;
using UnityEngine;

namespace Morgott.Preloader.Profiling
{
    /// <summary>
    /// Wires the profiling modules. Phases: <see cref="Begin"/> from the patcher's Initialize (no Unity types
    /// touched), <see cref="InstallRuntimeHooks"/> from Finish (pure managed hooks only: Harmony + Chainloader),
    /// Unity/game hooks from the Chainloader.Initialize postfix once the engine is up. Output on main-menu ready:
    /// BepInEx\Morgott.Preloader\trace.json + summary.txt; on the first player spawn: trace-world.json + summary-world.txt.
    /// </summary>
    internal static class Profiler
    {
        public const string HarmonyId = "morgott.valheim.preloader.profiler";

        private static Harmony _harmony;
        private static long _initialized;
        private static int _menuExported;
        private static int _worldExported;
        private static volatile bool _menuReached;

        private static string OutputDir => Path.Combine(Paths.BepInExRootPath, "Morgott.Preloader");

        public static void Begin()
        {
            StartupTrace.Start();
            _initialized = StartupTrace.Now();
        }

        public static void InstallRuntimeHooks()
        {
            StartupTrace.Complete("lifecycle", "BepInEx preloader: patchers + load patched assemblies", _initialized, StartupTrace.Now());
            StartupTrace.Mark("patcher.Finish");
            _harmony = new Harmony(HarmonyId);
            Log.Guard("Harmony profiler install", () => HarmonyProfiler.Install(_harmony));
            Log.Guard("Plugin profiler install", () => PluginLifecycleProfiler.Install(_harmony));
            Log.Guard("Config save profiler install", () => ConfigSaveProfiler.Install(_harmony));
            PluginLifecycleProfiler.ChainloaderInitialized += OnChainloaderInitialized;
            PluginLifecycleProfiler.ChainloaderStarted += () => Log.Guard("Jotunn probe install", () => JotunnProbe.Install(_harmony));
            if (Config.ProfilerTimeModPatches.Value)
            {
                PluginLifecycleProfiler.ChainloaderStarted += () => Log.Guard("Patch owner probe install", () => PatchOwnerProbe.Install(_harmony));
            }
            Log.Info("Profiler: preloader hooks installed");
        }

        /// <summary>Unity types may only be touched from here on: their static constructors call engine internals
        /// that do not exist yet during the preloader (a TypeInitializationException there breaks the game).</summary>
        private static void OnChainloaderInitialized()
        {
            Log.Guard("Bundle profiler install", () => BundleProfiler.Install(_harmony));
            Log.Guard("Game probe install", () => GameLifecycleProbe.Install(_harmony));
            GameLifecycleProbe.MenuReady += OnMenuReady;
            GameLifecycleProbe.WorldReady += OnWorldReady;
            Application.quitting += OnQuitting;
        }

        /// <summary>Menu profile from a snapshot; recording goes on until the first player spawn.</summary>
        private static void OnMenuReady()
        {
            long now = StartupTrace.Now();
            List<TraceEvent> events = StartupTrace.Snapshot();
            double menuReadyMs = StartupTrace.ToMs(now);
            _menuReached = true;
            Log.Info(TraceReport.OneLine(events, menuReadyMs, "menu ready"));
            // File output off the main thread; the snapshot is a copy.
            ThreadPool.QueueUserWorkItem(_ => Export(events, menuReadyMs, "main menu ready", "menu ready", ""));
        }

        private static void OnWorldReady()
        {
            long now = StartupTrace.Now();
            List<TraceEvent> events = StartupTrace.StopAndSnapshot();
            double spawnMs = StartupTrace.ToMs(now);
            Log.Info(TraceReport.OneLine(events, spawnMs, "first spawn"));
            ThreadPool.QueueUserWorkItem(_ => Export(events, spawnMs, "first player spawn", "first spawn", "-world"));
        }

        /// <summary>Quit before the first spawn (crash-free exit mid-startup): write what was recorded, synchronously.</summary>
        private static void OnQuitting()
        {
            if (!StartupTrace.Recording)
            {
                return;
            }
            long now = StartupTrace.Now();
            bool menu = _menuReached;
            Export(StartupTrace.StopAndSnapshot(), StartupTrace.ToMs(now), menu ? "quit before first spawn (partial trace)" : "quit before main menu (partial trace)",
                "quit", menu ? "-world" : "");
        }

        /// <summary>Menu profile: trace.json + summary.txt; world profile (process start -> first spawn): trace-world.json + summary-world.txt.</summary>
        private static void Export(List<TraceEvent> events, double endMs, string reason, string endLabel, string suffix)
        {
            if (Interlocked.Exchange(ref suffix.Length == 0 ? ref _menuExported : ref _worldExported, 1) != 0)
            {
                return;
            }
            Log.Guard("Trace export", () =>
            {
                string header = $"Morgott.Preloader startup profile - {DateTime.Now:yyyy-MM-dd HH:mm:ss} - {reason} - {events.Count} events";
                AtomicFile.WriteAllText(Path.Combine(OutputDir, "trace" + suffix + ".json"), TraceReport.ChromeTrace(events));
                AtomicFile.WriteAllText(Path.Combine(OutputDir, "summary" + suffix + ".txt"), TraceReport.Summary(events, endMs, endLabel, header));
                Log.Info($"Profile written to {OutputDir} (trace{suffix}.json for chrome://tracing / ui.perfetto.dev, summary{suffix}.txt)");
            });
        }
    }
}
