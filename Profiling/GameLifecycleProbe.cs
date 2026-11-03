using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Morgott.Preloader.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Morgott.Preloader.Profiling
{
    /// <summary>
    /// Valheim startup and world-load methods, scene loads, the main-menu-ready and first-spawn triggers. Game types are only touched here,
    /// after Chainloader.Initialize, so the preloader never loads assembly_valheim. Every method gets two hook
    /// pairs: an outer one (First prefix / Last finalizer, category <c>game</c>) around all mods' patches, and an
    /// inner one (Last prefix / First postfix, category <c>game.body</c>) around the original body only (Harmony
    /// runs every postfix before any finalizer, so the inner end must be a postfix); outer minus body = mods'
    /// patch cost. The outer finalizer always runs and rebalances the inner stack when an exception skipped the
    /// postfix. All hooked methods run on the Unity main thread.
    /// </summary>
    internal static class GameLifecycleProbe
    {
        private static readonly SpanStack OuterSpans = new SpanStack();
        private static readonly SpanStack BodySpans = new SpanStack();
        private static readonly List<int> BodyDepths = new List<int>();
        public const string WorldLoadMark = "main scene requested (FejdStartup.LoadMainScene end)";
        public const string WorldReadyMark = "first player spawn (Game.SpawnPlayer end)";

        public const string MainSceneLoadedMark = "main scene loaded (every scene Awake done)";
        public const string FirstFrameMark = "first rendered frame after the main scene load (loading screen visible)";
        public const string ZoneStartMark = "ZoneSystem.Start end";

        private static long _sceneRequest;

        // World-load wall clock (main thread only): click -> LoadMainScene -> main scene Awake block -> first frame.
        private static long _click;
        private static long _loadMainStart;
        private static long _loadMainEnd;
        private static long _menuFrame;
        private static long _mainLoaded;
        private static long _firstFrame;
        private static long _zoneStart;
        private static bool _awaitMainScene;
        private static bool _worldLoadEmitted;
        private static bool _menuReady;
        private static bool _worldReady;

        /// <summary>Raised once, on the main thread, at the end of the first FejdStartup.Start.</summary>
        public static event Action MenuReady;

        /// <summary>Raised once, on the main thread, at the end of the first Game.SpawnPlayer (player in the world).</summary>
        public static event Action WorldReady;

        // Citations: ValheimDecompiled-1.0.15\assembly_valheim\<File>.cs:<line>.
        internal static MethodInfo[] Targets() => new[]
        {
            AccessTools.DeclaredMethod(typeof(FejdStartup), "Awake"),         // FejdStartup.cs:307
            AccessTools.DeclaredMethod(typeof(FejdStartup), "Start"),         // FejdStartup.cs:427 (end = main menu ready)
            AccessTools.DeclaredMethod(typeof(FejdStartup), "SetupGui"),      // FejdStartup.cs:505
            AccessTools.DeclaredMethod(typeof(FejdStartup), "SetupObjectDB"), // FejdStartup.cs:855
            AccessTools.DeclaredMethod(typeof(ObjectDB), "Awake"),            // ObjectDB.cs:35
            AccessTools.DeclaredMethod(typeof(ObjectDB), "CopyOtherDB"),      // ObjectDB.cs:41
            AccessTools.DeclaredMethod(typeof(ObjectDB), "UpdateRegisters"),  // ObjectDB.cs:50
            AccessTools.DeclaredMethod(typeof(ZNetScene), "Awake"),           // ZNetScene.cs:33
            // assembly_guiutils\Localization.cs:506/525: SetupLanguage = one LoadCSV per vanilla CSV (+ mods' patches).
            AccessTools.DeclaredMethod(typeof(Localization), "SetupLanguage"),
            AccessTools.DeclaredMethod(typeof(Localization), "LoadCSV"),
        };

        /// <summary>World load after the menu (world start click -> first player spawn).</summary>
        internal static MethodInfo[] WorldTargets() => new[]
        {
            // OnWorldStart / DLCMan / CollectResources cited from ValheimDecompiled-1.0.16.
            AccessTools.DeclaredMethod(typeof(FejdStartup), "OnWorldStart"),            // FejdStartup.cs:1635 (start = click)
            AccessTools.DeclaredMethod(typeof(FejdStartup), "LoadMainScene"),           // FejdStartup.cs:2957 (sync FastLoadScene)
            AccessTools.DeclaredMethod(typeof(DLCMan), "Awake"),                        // DLCMan.cs:25
            AccessTools.DeclaredMethod(typeof(Game), "CollectResources"),               // Game.cs:320 (UnloadUnusedAssets, from ZNet.LoadWorld)
            AccessTools.DeclaredMethod(typeof(ZNet), "Awake"),                          // ZNet.cs:333
            AccessTools.DeclaredMethod(typeof(ZNet), "Start"),                          // ZNet.cs:435
            AccessTools.DeclaredMethod(typeof(ZNet), "LoadWorld"),                      // ZNet.cs:1949
            AccessTools.DeclaredMethod(typeof(ZoneSystem), "Awake"),                    // ZoneSystem.cs:667
            AccessTools.DeclaredMethod(typeof(ZoneSystem), "Start"),                    // ZoneSystem.cs:685
            AccessTools.DeclaredMethod(typeof(ZoneSystem), "GenerateLocationsIfNeeded"), // ZoneSystem.cs:706
            AccessTools.DeclaredMethod(typeof(Minimap), "Awake"),                       // Minimap.cs:446
            AccessTools.DeclaredMethod(typeof(Minimap), "Start"),                       // Minimap.cs:513
            AccessTools.DeclaredMethod(typeof(EnvMan), "Awake"),                        // EnvMan.cs:222
            AccessTools.DeclaredMethod(typeof(Game), "Awake"),                          // Game.cs:238
            AccessTools.DeclaredMethod(typeof(Game), "Start"),                          // Game.cs:283
            AccessTools.DeclaredMethod(typeof(Game), "SpawnPlayer"),                    // Game.cs:484 (first end = world ready)
            // World generation (ValheimDecompiled-1.0.16): biome map on every load / connect (ZNet.cs:464, :1117), zone spawns.
            AccessTools.DeclaredMethod(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.VerifyBiomeData)),     // AltBiomeWorldData.cs:72
            AccessTools.DeclaredMethod(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.GenerateBiomePoints)), // AltBiomeWorldData.cs:99
            AccessTools.DeclaredMethod(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.GenerateSectors)),     // AltBiomeWorldData.cs:148
            AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnZone"),                                         // ZoneSystem.cs:1312
            AccessTools.DeclaredMethod(typeof(ZoneSystem), "PlaceLocations"),                                    // ZoneSystem.cs:2189
            AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnLocation"),                                     // ZoneSystem.cs:2391
            AccessTools.DeclaredMethod(typeof(DungeonGenerator), nameof(DungeonGenerator.Generate),
                new[] { typeof(int), typeof(ZoneSystem.SpawnMode) }),                                            // DungeonGenerator.cs:193
            // Inside ZNet.Awake (ZNet.cs:333-395): platform init and the world generator (rivers / streams pregeneration,
            // WorldGenerator.cs:187-258) on a server; inside ZNet.LoadWorld (ZNet.cs:1949-1994): the ZDO chunks.
            AccessTools.DeclaredMethod(typeof(SteamManager), nameof(SteamManager.Initialize)),                   // SteamManager.cs:39
            AccessTools.DeclaredMethod(typeof(ZSteamMatchmaking), nameof(ZSteamMatchmaking.Initialize)),
            AccessTools.DeclaredMethod(typeof(ZPlayFabMatchmaking), nameof(ZPlayFabMatchmaking.Initialize)),     // ZPlayFabMatchmaking.cs:139
            AccessTools.DeclaredMethod(typeof(WorldGenerator), nameof(WorldGenerator.Initialize)),               // WorldGenerator.cs:187
            AccessTools.DeclaredMethod(typeof(WorldGenerator), "FindLakes"),                                     // WorldGenerator.cs:275
            AccessTools.DeclaredMethod(typeof(WorldGenerator), "PlaceRivers"),                                   // WorldGenerator.cs:409
            AccessTools.DeclaredMethod(typeof(WorldGenerator), "PlaceStreams"),                                  // WorldGenerator.cs:335
            AccessTools.DeclaredMethod(typeof(WorldGenerator), "RenderRivers"),                                  // WorldGenerator.cs:531
            AccessTools.DeclaredMethod(typeof(ZDOMan), nameof(ZDOMan.LoadChunks)),                               // ZDOMan.cs:463
        };

        public static void Install(Harmony harmony)
        {
            int hooked = 0;
            foreach (MethodInfo method in Targets().Concat(WorldTargets()))
            {
                if (method == null)
                {
                    Log.Warning("Game probe: a target method was not found (game update?), skipped");
                    continue;
                }
                harmony.Patch(method,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(GameLifecycleProbe), nameof(OuterPrefix)), Priority.First),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(GameLifecycleProbe), nameof(OuterFinalizer)), Priority.Last));
                harmony.Patch(method,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(GameLifecycleProbe), nameof(BodyPrefix)), Priority.Last),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(GameLifecycleProbe), nameof(BodyPostfix)), Priority.First));
                hooked++;
            }

            // SceneLoader.Start -> StartLoading -> LoadSceneAsync (SceneLoader.cs:74/118/123): the scene request.
            MethodInfo sceneLoaderStart = AccessTools.DeclaredMethod(typeof(SceneLoader), "Start");
            if (sceneLoaderStart != null)
            {
                harmony.Patch(sceneLoaderStart,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(GameLifecycleProbe), nameof(SceneLoaderStartPrefix))));
            }

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
            Log.Info($"Game probe: {hooked} game methods hooked");
        }

        private static void OuterPrefix()
        {
            BodyDepths.Add(BodySpans.Count);
            OuterSpans.Push();
        }

        private static void BodyPrefix() => BodySpans.Push();

        private static void BodyPostfix(MethodBase __originalMethod) => Finish(__originalMethod, "game.body", BodySpans.Pop());

        private static void OuterFinalizer(MethodBase __originalMethod, Exception __exception)
        {
            int last = BodyDepths.Count - 1;
            if (last >= 0)
            {
                BodySpans.TruncateTo(BodyDepths[last]);
                BodyDepths.RemoveAt(last);
            }
            Finish(__originalMethod, "game", OuterSpans.Pop(), __exception != null);
        }

        private static void Finish(MethodBase original, string cat, long start, bool failed = false)
        {
            if (start == 0)
            {
                return;
            }
            long end = StartupTrace.Now();
            StartupTrace.Complete(cat, original.DeclaringType?.Name + "." + original.Name, start, end);
            if (cat != "game")
            {
                return;
            }
            if (!_menuReady && original.DeclaringType == typeof(FejdStartup) && original.Name == "Start")
            {
                _menuReady = true;
                StartupTrace.Mark("main menu ready (FejdStartup.Start end)");
                Log.Guard("MenuReady handlers", () => MenuReady?.Invoke());
            }
            else if (original.DeclaringType == typeof(FejdStartup) && original.Name == "OnWorldStart")
            {
                // OnWorldStart may return early (cloud warning, popups): the last one before LoadMainScene wins.
                _click = start;
            }
            else if (original.DeclaringType == typeof(FejdStartup) && original.Name == "LoadMainScene")
            {
                StartupTrace.Mark(WorldLoadMark);
                _sceneRequest = StartupTrace.Now();
                _loadMainStart = start;
                _loadMainEnd = end;
                _awaitMainScene = true;
                StartEndOfFrame(FejdStartup.instance, MenuFrameRendered);
            }
            else if (_zoneStart == 0 && _loadMainEnd != 0 && original.DeclaringType == typeof(ZoneSystem) && original.Name == "Start")
            {
                _zoneStart = end;
                StartupTrace.Mark(ZoneStartMark);
                EmitWorldLoadSpans();
            }
            else if (!_worldReady && !failed && original.DeclaringType == typeof(Game) && original.Name == "SpawnPlayer")
            {
                _worldReady = true;
                StartupTrace.Mark(WorldReadyMark);
                Log.Guard("WorldReady handlers", () => WorldReady?.Invoke());
            }
        }

        private static void SceneLoaderStartPrefix()
        {
            _sceneRequest = StartupTrace.Now();
            StartupTrace.Mark("SceneLoader.Start (scene requested)");
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!StartupTrace.Recording)
            {
                return;
            }
            StartupTrace.Mark("scene loaded: " + scene.name, mode.ToString());
            if (_awaitMainScene)
            {
                // Sync LoadScene: every Awake of the new scene ran before sceneLoaded; Start/Update/render follow.
                _awaitMainScene = false;
                _mainLoaded = StartupTrace.Now();
                StartupTrace.Mark(MainSceneLoadedMark, scene.name);
                StartEndOfFrame((MonoBehaviour)Game.instance ?? ZNet.instance, FirstFrameRendered);
            }
            if (_sceneRequest != 0)
            {
                StartupTrace.Complete("scene", "load " + scene.name, _sceneRequest, StartupTrace.Now(),
                    "scene request (SceneLoader.Start / FejdStartup.LoadMainScene) -> sceneLoaded (includes the previous scene's " +
                    "unload, logos, platform init and scene Awake calls)",
                    tid: StartupTrace.LaneScenes);
                _sceneRequest = 0;
            }
        }

        /// <summary>Runs <paramref name="done"/> after the current frame is rendered (all cameras and UI).</summary>
        private static void StartEndOfFrame(MonoBehaviour host, Action done)
        {
            if (host == null || !host.isActiveAndEnabled)
            {
                Log.Warning("Game probe: no active MonoBehaviour to wait for the rendered frame on, world-load frame mark skipped");
                return;
            }
            host.StartCoroutine(EndOfFrame(done));
        }

        private static IEnumerator EndOfFrame(Action done)
        {
            yield return new WaitForEndOfFrame();
            Log.Guard("World-load frame mark", done);
        }

        private static void MenuFrameRendered()
        {
            if (_mainLoaded == 0)
            {
                _menuFrame = StartupTrace.Now();
                StartupTrace.Mark("menu frame rendered after LoadMainScene (menu loading panel)");
            }
        }

        private static void FirstFrameRendered()
        {
            _firstFrame = StartupTrace.Now();
            StartupTrace.Mark(FirstFrameMark);
            EmitWorldLoadSpans();
        }

        /// <summary>Wall-clock world-load spans on their own lane (category <c>worldload</c>), once both the first
        /// rendered frame and ZoneSystem.Start are known.</summary>
        private static void EmitWorldLoadSpans()
        {
            if (_worldLoadEmitted || _firstFrame == 0 || _zoneStart == 0)
            {
                return;
            }
            _worldLoadEmitted = true;
            long click = _click != 0 && _click <= _loadMainStart ? _click : _loadMainStart;
            Span("1 click (OnWorldStart start) -> LoadMainScene end", click, _loadMainEnd,
                "menu side: OnWorldStart, backend selection, LoadMainScene");
            if (_menuFrame != 0)
            {
                Span("2a LoadMainScene end -> menu frame rendered", _loadMainEnd, _menuFrame, "the menu's loading panel can be seen");
            }
            Span("2 LoadMainScene end -> main scene loaded (FREEZE: old scene unload + every main-scene Awake)", _loadMainEnd, _mainLoaded,
                "sync SceneManager.LoadScene; nothing is rendered during it");
            Span("3 main scene loaded -> first rendered frame (Start calls + first Update)", _mainLoaded, _firstFrame, null);
            Span("4 click -> first rendered frame (loading screen visible)", click, _firstFrame, null);
            Span("5 click -> ZoneSystem.Start end", click, _zoneStart, null);
        }

        private static void Span(string name, long start, long end, string detail)
        {
            if (start != 0 && end >= start)
            {
                StartupTrace.Complete("worldload", name, start, end, detail, tid: StartupTrace.LaneWorldLoad);
            }
        }

        private static void OnActiveSceneChanged(Scene from, Scene to)
        {
            if (StartupTrace.Recording)
            {
                StartupTrace.Mark("active scene: " + to.name);
            }
        }
    }
}
