using System;
using System.Diagnostics;
using System.Globalization;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace Morgott.Preloader.Core
{
    /// <summary>
    /// Startup milestones for modules that do not depend on the profiler. <see cref="ChainloaderInitialized"/> =
    /// end of <c>Chainloader.Initialize</c> (Unity is up, game assemblies resolvable, no plugin loaded yet);
    /// <see cref="ChainloaderStarted"/> = end of <c>Chainloader.Start</c> (every plugin's Awake has run);
    /// <see cref="MenuReady"/> = end of the first <c>FejdStartup.Start</c> (FejdStartup.cs:427, builds the main menu).
    /// All are raised on the Unity main thread.
    /// </summary>
    internal static class Lifecycle
    {
        private static Harmony _harmony;
        private static bool _menuReady;

        public static event Action ChainloaderInitialized;

        public static event Action ChainloaderStarted;

        public static event Action MenuReady;

        /// <summary>Patcher Finish: patches only a BepInEx method, so no Unity/game type initializer runs yet.</summary>
        public static void Install(Harmony harmony)
        {
            _harmony = harmony;
            harmony.Patch(AccessTools.Method(typeof(Chainloader), nameof(Chainloader.Initialize)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(Lifecycle), nameof(InitializePostfix))));
        }

        private static void InitializePostfix()
        {
            Log.Guard("Chainloader.Start hook", () => _harmony.Patch(AccessTools.Method(typeof(Chainloader), nameof(Chainloader.Start)),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(Lifecycle), nameof(StartFinalizer)), Priority.Last)));
            Log.Guard("FejdStartup.Start hook", () => _harmony.Patch(AccessTools.DeclaredMethod(typeof(FejdStartup), "Start"),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(Lifecycle), nameof(FejdStartupPostfix)))));
            Log.Guard("World load timing hooks", InstallWorldLoadTiming);
            Log.Guard("ChainloaderInitialized handlers", () => ChainloaderInitialized?.Invoke());
        }

        // Finalizer, Priority.Last: after the other Chainloader.Start postfixes/finalizers, also when it throws.
        private static void StartFinalizer()
        {
            Log.Guard("ChainloaderStarted handlers", () => ChainloaderStarted?.Invoke());
        }

        private static void FejdStartupPostfix()
        {
            if (_menuReady)
            {
                return;
            }
            _menuReady = true;
            Log.Guard("Menu ready time", () =>
                Log.Info(string.Format(CultureInfo.InvariantCulture, "menu ready {0:F2} s after process start",
                    (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds)));
            Log.Guard("MenuReady handlers", () => MenuReady?.Invoke());
        }

        // World-load wall clock, profiler off too (main thread): click = last FejdStartup.OnWorldStart start before
        // LoadMainScene (FejdStartup.cs:1635), LoadMainScene end (:2957, sync scene load request), main scene loaded
        // (SceneManager.sceneLoaded: every main-scene Awake done), end of the first Game.SpawnPlayer (Game.cs:484).
        private static long _click;
        private static long _loadMainEnd;
        private static long _mainLoaded;
        private static bool _awaitMain;
        private static bool _spawned;

        private static void InstallWorldLoadTiming()
        {
            _harmony.Patch(AccessTools.DeclaredMethod(typeof(FejdStartup), "OnWorldStart"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(Lifecycle), nameof(OnWorldStartPrefix)), Priority.First));
            _harmony.Patch(AccessTools.DeclaredMethod(typeof(FejdStartup), "LoadMainScene"),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(Lifecycle), nameof(LoadMainSceneFinalizer)), Priority.Last));
            _harmony.Patch(AccessTools.DeclaredMethod(typeof(Game), "SpawnPlayer"),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(Lifecycle), nameof(SpawnPlayerFinalizer)), Priority.Last));
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (_awaitMain)
                {
                    _awaitMain = false;
                    _mainLoaded = Stopwatch.GetTimestamp();
                }
            };
        }

        private static void OnWorldStartPrefix()
        {
            if (_loadMainEnd == 0)
            {
                _click = Stopwatch.GetTimestamp();
            }
        }

        private static void LoadMainSceneFinalizer()
        {
            if (_loadMainEnd == 0)
            {
                _loadMainEnd = Stopwatch.GetTimestamp();
                _awaitMain = true;
            }
        }

        private static void SpawnPlayerFinalizer(Exception __exception)
        {
            if (_spawned || __exception != null || _loadMainEnd == 0)
            {
                return;
            }
            _spawned = true;
            long now = Stopwatch.GetTimestamp();
            long click = _click != 0 ? _click : _loadMainEnd;
            Log.Info(string.Format(CultureInfo.InvariantCulture,
                "world load {0:F2} s (click -> first spawn): click -> LoadMainScene end {1:F2} s, freeze (-> main scene loaded) {2:F2} s, -> spawn {3:F2} s",
                Sec(click, now), Sec(click, _loadMainEnd), _mainLoaded != 0 ? Sec(_loadMainEnd, _mainLoaded) : -1,
                _mainLoaded != 0 ? Sec(_mainLoaded, now) : -1));
        }

        private static double Sec(long from, long to) => (to - from) / (double)Stopwatch.Frequency;
    }
}
