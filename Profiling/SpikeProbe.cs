using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using Morgott.Preloader.Core;
using HarmonyLib;
using Unity.Profiling;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.LowLevel;

namespace Morgott.Preloader.Profiling
{
    /// <summary>
    /// Opt-in with <c>[Profiler] SpikeProbe</c>: in-play frame-time spikes and what was going on in the spike frame, with
    /// signals a release player actually has. Independent of <c>[Profiler] Enabled</c> (no startup trace).
    /// <list type="bullet">
    /// <item>Frame clock + split: at the main menu one marker system is put before every top-level player-loop entry and
    /// before every subsystem of each (FixedUpdate repeats its subsystems per fixed step: accumulated), plus one at the end
    /// of the loop. A frame = first marker to the next frame's first marker (Stopwatch; Time.unscaledDeltaTime is clamped
    /// in long stalls). Each subsystem gets the time until the next marker, the gap after the loop's end marker is
    /// "(outside loop)". Systems other mods add later land inside the marker before them.</item>
    /// <item>Hooks (Harmony prefix First + finalizer Last, observer only, main thread): ZNetScene.CreateObject,
    /// ZoneSystem.SpawnZone / SpawnLocation, DungeonGenerator.PlaceRoom, Heightmap.Regenerate / RebuildRenderMesh /
    /// RebuildCollisionMesh / ForceGenerateAll, AssetBundle sync loads, Texture2D.Apply: count + self time per frame
    /// (nested hooked calls are subtracted from their caller), name of the slowest call.</item>
    /// <item>First seen: the first instance of each net prefab, location and dungeon room is scanned once for its
    /// renderers' shaders and (shader, sorted material keywords, instancing) combinations not seen before in this
    /// session - the candidates for a first-use shader compile. The scan's own time is counted as probe time and taken
    /// out of the hook it ran in.</item>
    /// <item>Release-player counters through ProfilerRecorder (Unity 6000.0 profiler counters reference, column
    /// "Available in release players"): GC Used / GC Reserved / Total Used / System Used Memory, SetPass Calls, Draw
    /// Calls, Batches, Vertex/Index Buffer Upload In Frame Bytes, Render Textures Changes, Shadow Casters, Visible Skinned
    /// Meshes. Plus GC.CollectionCount per generation. Profiler markers (GC.Collect, Shader.CreateGPUProgram, ...) are
    /// tried too and reported as valid / ever non-zero in the summary.</item>
    /// <item>Player context on spike frames only: segment (set by a test driver through <see cref="Segment"/>), speed,
    /// teleporting, interior, zone changed within 1 s, non-tamed creatures within 30 m, combat timer.</item>
    /// </list>
    /// Output: BepInEx\Morgott.Preloader\spikes.tsv (one row per spike frame) and spike-summary.txt (frame-time stats per
    /// segment, causes ranked by total ms, top spike frames, counter availability, probe overhead), written every 30 s
    /// in play (worker thread) and at quit / <see cref="Flush"/>; profiler-available.txt once (every metric
    /// ProfilerRecorderHandle.GetAvailable lists in this player).
    /// </summary>
    public static class SpikeProbe
    {
        public const string HarmonyId = "morgott.valheim.preloader.spikeprobe";

        /// <summary>Free-form label of the current test segment ("walk", "portal", ...); a driver sets it by reflection.</summary>
        public static string Segment = "play";

        private const int SCreate = 0, SZone = 1, SLocation = 2, SRoom = 3, SHeightmap = 4, SForceGen = 5, SBundle = 6, STexApply = 7, SCount = 8;

        private static readonly string[] SignalNames =
            { "create", "zone", "location", "room", "heightmap", "forceGenerateAll", "bundleLoad", "textureApply" };

        // Owner of each cause, for the summary (who to forward a culprit to).
        private static readonly Dictionary<string, string> Owners = new Dictionary<string, string>
        {
            { "create", "vanilla ZNetScene.CreateObject (Instantiate + Awake); budget = Skidbladnir Perf.Streaming" },
            { "zone", "vanilla ZoneSystem.SpawnZone; Skidbladnir Perf.Streaming / ColliderBake" },
            { "location", "vanilla ZoneSystem.SpawnLocation; Skidbladnir Perf.Locations" },
            { "room", "vanilla DungeonGenerator.PlaceRoom; Skidbladnir Perf.Dungeons" },
            { "heightmap", "vanilla Heightmap rebuild; Skidbladnir Terrain / ColliderBake" },
            { "forceGenerateAll", "vanilla Heightmap.ForceGenerateAll (SnapToGround.SnappAll); Skidbladnir Perf.Dungeons" },
            { "bundleLoad", "AssetBundle sync load (vanilla SoftReferenceableAssets / mods)" },
            { "textureApply", "Texture2D.Apply (minimap fog / Jotunn overlays); Skidbladnir Perf.Fog / MapOverlay" },
            { "render+newVariants", "render with first-seen shader variants: shader compile candidate (Skidbladnir PrefabWarm / Morgott.Preloader SVC)" },
            { "render", "render, no first-seen material (GPU / driver / uploads)" },
            { "scripts", "MonoBehaviour Update/LateUpdate/FixedUpdate not in a hook (vanilla + mods; per-method: Skidbladnir StressProf)" },
            { "scripts+gc", "scripts with a GC collection in the frame (Mono GC; Skidbladnir Perf.Memory)" },
            { "physics", "PhysX simulate / sync (vanilla)" },
            { "loading", "async load integration (EarlyUpdate.UpdatePreloading)" },
            { "present", "waiting for the previous frame's present (TimeUpdate / outside the loop): render thread or GPU busy, vsync" },
            { "present+newVariants", "present wait with first-seen shader variants: shader compile on the render thread candidate" },
            { "other", "other engine subsystem (see top subsystems)" },
        };

        private sealed class Spike
        {
            public double T;
            public string Utc;
            public float Ms;
            public string Segment;
            public bool Teleporting, Interior, ZoneChanged, Combat;
            public float Speed;
            public int Mobs;
            public string Pos, Zone;
            public int Gc0, Gc1, Gc2;
            public long[] Counters;
            public int[] Calls;
            public float[] SelfMs;
            public string[] MaxName;
            public int NewPrefabs, NewShaders, NewVariants, PrevNewVariants;
            public string NewShaderNames;
            public string TopSubs;
            public float ScriptMs, RenderMs, PhysicsMs, LoadingMs, OutsideMs, ProbeMs;
            public string Cause;
            public string ScriptTop;
        }

        private sealed class SegStats
        {
            public string Name;
            public long Frames;
            public double TotalMs;
            public float MaxMs;
            public long Spikes;
            public double SpikeMs;
            public readonly int[] Hist = new int[HistBuckets];
        }

        private const int HistBuckets = 4000; // 0.5 ms buckets up to 2 s
        private const int MaxSpikes = 50000;

        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
        private static double _thresholdMs = 33;
        private static int _mainThread;
        private static bool _active;

        // Player loop split.
        private static string[] _subNames = new string[0];
        private static int[] _subKind = new int[0]; // 0 other, 1 scripts, 2 render, 3 physics, 4 loading, 5 outside
        private static long[] _acc = new long[0];
        private static long[] _sessionAcc = new long[0];
        private static int _lastIdx = -1;
        private static long _lastTs;
        private static long _frameStart;
        private static int _outsideIdx;

        // Per-frame hook accumulators.
        private static readonly int[] FCalls = new int[SCount];
        private static readonly long[] FSelf = new long[SCount];
        private static readonly long[] FMax = new long[SCount];
        private static readonly object[] FMaxKey = new object[SCount];
        private static readonly long[] SessionCalls = new long[SCount];
        private static readonly long[] SessionSelf = new long[SCount];
        private static readonly long[] StackStart = new long[64];
        private static readonly long[] StackChild = new long[64];
        private static int _depth;
        private static readonly Dictionary<MethodBase, int> SignalOf = new Dictionary<MethodBase, int>();

        // First seen.
        private static readonly HashSet<int> SeenNetPrefabs = new HashSet<int>();
        private static readonly HashSet<string> SeenNamed = new HashSet<string>();
        private static readonly HashSet<int> SeenShaders = new HashSet<int>();
        private static readonly HashSet<long> SeenMaterials = new HashSet<long>();
        private static readonly HashSet<string> SeenVariants = new HashSet<string>();
        private static readonly List<Renderer> ScanRenderers = new List<Renderer>();
        private static readonly List<Material> ScanMaterials = new List<Material>();
        private static int _fNewPrefabs, _fNewShaders, _fNewVariants, _prevNewVariants;
        private static readonly List<string> FNewShaderNames = new List<string>();
        private static long _fProbeTicks;
        private static long _sessionNewShaders, _sessionNewVariants;

        // Counters.
        private static readonly string[] CounterNames =
        {
            "GC Used Memory", "GC Reserved Memory", "Total Used Memory", "System Used Memory", "SetPass Calls Count", "Draw Calls Count",
            "Batches Count", "Vertex Buffer Upload In Frame Bytes", "Index Buffer Upload In Frame Bytes", "Render Textures Changes Count",
            "Shadow Casters Count", "Visible Skinned Meshes Count",
        };

        // Deltas (memory) vs per-frame values (render): the first four are reported as change since the previous frame.
        private const int MemoryCounters = 4;
        private static ProfilerRecorder[] _counters = new ProfilerRecorder[0];
        private static readonly long[] CounterPrev = new long[CounterNames.Length];
        private static readonly long[] CounterNow = new long[CounterNames.Length];
        private static readonly bool[] CounterSeen = new bool[CounterNames.Length];

        private static readonly string[] MarkerNames =
        {
            "Main Thread", "PlayerLoop", "GC.Collect", "GC.Alloc", "Shader.CreateGPUProgram", "Shader.Parse", "CreateGPUProgram",
            "Loading.ReadObject", "Loading.AwakeFromLoad", "Physics.Simulate", "Gfx.WaitForPresentOnGfxThread", "Instantiate",
        };

        private static ProfilerRecorder[] _markers = new ProfilerRecorder[0];
        private static bool[] _markerSeen = new bool[0];
        private static int _availableMetrics = -1;
        private static bool _frameTimingEnabled;

        // GC, zone, context.
        private static int _gc0, _gc1, _gc2;
        private static Vector2s _zone;
        private static long _zoneChangedAt;
        private static AccessTools.FieldRef<Humanoid, float> _combatTimer;

        // Results.
        private static readonly List<Spike> Spikes = new List<Spike>();
        private static readonly Dictionary<string, SegStats> Segments = new Dictionary<string, SegStats>();
        private static SegStats _seg;
        private static string _segName;
        private static long _playStart;
        private static long _probeTicks, _probeMaxTicks, _probeFrames;
        private static long _lastFlush;
        private static int _flushedSpikes = -1;
        private static int _writing;

        private static string Dir => Path.Combine(Paths.BepInExRootPath, "Morgott.Preloader");

        /// <summary>Chainloader.Initialize postfix (engine up, no plugin Awake yet): hooks now, player loop at the main menu.</summary>
        internal static void Install(Harmony harmony)
        {
            _thresholdMs = Math.Max(1.0, Config.SpikeThresholdMs.Value);
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            _combatTimer = AccessTools.FieldRefAccess<Humanoid, float>("m_lastCombatTimer");
            int hooked = 0;
            hooked += Hook(harmony, AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObject"), SCreate, nameof(CreateFin), nameof(CreatePost));
            hooked += Hook(harmony, AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnZone"), SZone, nameof(ZoneFin), null);
            hooked += Hook(harmony, AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnLocation"), SLocation, nameof(LocationFin), nameof(NamedPost));
            hooked += Hook(harmony, AccessTools.GetDeclaredMethods(typeof(DungeonGenerator)).FirstOrDefault(m => m.Name == "PlaceRoom" && m.ReturnType == typeof(Room)),
                SRoom, nameof(Fin), nameof(RoomPost));
            foreach (string m in new[] { "Regenerate", "RebuildRenderMesh", "RebuildCollisionMesh" })
            {
                hooked += Hook(harmony, AccessTools.DeclaredMethod(typeof(Heightmap), m), SHeightmap, nameof(Fin), null);
            }
            hooked += Hook(harmony, AccessTools.DeclaredMethod(typeof(Heightmap), "ForceGenerateAll"), SForceGen, nameof(Fin), null);
            foreach (string m in new[] { "LoadAsset_Internal", "LoadFromFile_Internal", "LoadFromMemory_Internal", "LoadFromStreamInternal", "LoadAssetWithSubAssets_Internal" })
            {
                hooked += Hook(harmony, AccessTools.DeclaredMethod(typeof(AssetBundle), m), SBundle, nameof(Fin), null);
            }
            hooked += Hook(harmony, AccessTools.DeclaredMethod(typeof(Texture2D), "Apply", new[] { typeof(bool), typeof(bool) }), STexApply, nameof(Fin), null);
            Lifecycle.MenuReady += () => Log.Guard("Spike probe player loop", InstallLoop);
            if (Config.ScriptBreakdown.Value)
            {
                Lifecycle.MenuReady += () => Log.Guard("Script probe install", ScriptProbe.Install);
            }
            Application.quitting += () => Log.Guard("Spike probe flush", () => Write(true));
            Log.Info($"Spike probe: {hooked} hooks, threshold {_thresholdMs:F0} ms; player loop at the main menu");
        }

        private static int Hook(Harmony harmony, MethodBase target, int signal, string finalizer, string postfix)
        {
            if (target == null)
            {
                Log.Warning($"Spike probe: hook target for {SignalNames[signal]} not found, skipped");
                return 0;
            }
            SignalOf[target] = signal;
            harmony.Patch(target,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(SpikeProbe), nameof(Pre)), Priority.First),
                postfix: postfix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(SpikeProbe), postfix), Priority.Last),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(SpikeProbe), finalizer), Priority.Last));
            return 1;
        }

        /// <summary>A hooked call is running (ScriptProbe keeps time spent inside hooks apart).</summary>
        internal static bool InHook => _depth > 0;

        internal static int HookDepth => _depth;

        // ---- hooks ----

        private static void Pre(out long __state)
        {
            if (!_active || _depth >= StackStart.Length || Thread.CurrentThread.ManagedThreadId != _mainThread)
            {
                __state = 0;
                return;
            }
            __state = Stopwatch.GetTimestamp();
            StackStart[_depth] = __state;
            StackChild[_depth] = 0;
            _depth++;
        }

        /// <summary>Self ticks of the call that ends now (hooked children subtracted), or -1 when it was not measured.</summary>
        private static long Exit(long state, int signal)
        {
            if (state == 0 || _depth == 0)
            {
                return -1;
            }
            long total = Stopwatch.GetTimestamp() - state;
            _depth--;
            ScriptProbe.AddChild(total, _depth);
            long self = total - StackChild[_depth];
            if (_depth > 0)
            {
                StackChild[_depth - 1] += total;
            }
            FCalls[signal]++;
            FSelf[signal] += self;
            return self;
        }

        private static void Fin(long __state, MethodBase __originalMethod)
        {
            if (__state != 0 && SignalOf.TryGetValue(__originalMethod, out int s))
            {
                long self = Exit(__state, s);
                if (self > FMax[s])
                {
                    FMax[s] = self;
                    FMaxKey[s] = __originalMethod.Name;
                }
            }
        }

        private static void CreateFin(long __state, ZDO zdo)
        {
            long self = Exit(__state, SCreate);
            if (self > FMax[SCreate])
            {
                FMax[SCreate] = self;
                FMaxKey[SCreate] = zdo != null ? (object)zdo.GetPrefab() : null;
            }
        }

        private static void ZoneFin(long __state, Vector2s zoneID)
        {
            long self = Exit(__state, SZone);
            if (self > FMax[SZone])
            {
                FMax[SZone] = self;
                FMaxKey[SZone] = zoneID;
            }
        }

        private static void LocationFin(long __state, ZoneSystem.ZoneLocation location)
        {
            long self = Exit(__state, SLocation);
            if (self > FMax[SLocation])
            {
                FMax[SLocation] = self;
                FMaxKey[SLocation] = location?.m_prefabName;
            }
        }

        private static void CreatePost(GameObject __result, ZDO zdo)
        {
            if (_active && __result != null && zdo != null && SeenNetPrefabs.Add(zdo.GetPrefab()))
            {
                Scan(__result);
            }
        }

        private static void NamedPost(GameObject __result)
        {
            if (_active && __result != null && SeenNamed.Add("l:" + __result.name))
            {
                Scan(__result);
            }
        }

        private static void RoomPost(Room __result)
        {
            if (_active && __result != null && SeenNamed.Add("r:" + __result.name))
            {
                Scan(__result.gameObject);
            }
        }

        /// <summary>First instance of a prefab: its shaders and (shader, keywords, instancing) combinations not seen yet.</summary>
        private static void Scan(GameObject go)
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThread)
            {
                return;
            }
            long t0 = Stopwatch.GetTimestamp();
            _fNewPrefabs++;
            try
            {
                go.GetComponentsInChildren(true, ScanRenderers);
                foreach (Renderer r in ScanRenderers)
                {
                    if (r == null)
                    {
                        continue;
                    }
                    r.GetSharedMaterials(ScanMaterials);
                    foreach (Material m in ScanMaterials)
                    {
                        // Materials are shared across prefabs and renderers: each (material, renderer kind) is looked at once.
                        int kind = r is SkinnedMeshRenderer ? 1 : r is MeshRenderer ? 0 : 2;
                        if (m == null || !SeenMaterials.Add(((long)m.GetInstanceID() << 2) | (uint)kind))
                        {
                            continue;
                        }
                        Shader shader = m.shader;
                        if (shader == null)
                        {
                            continue;
                        }
                        int id = shader.GetInstanceID();
                        if (SeenShaders.Add(id))
                        {
                            _fNewShaders++;
                            _sessionNewShaders++;
                            if (FNewShaderNames.Count < 6)
                            {
                                FNewShaderNames.Add(shader.name);
                            }
                        }
                        string[] kw = m.shaderKeywords;
                        Array.Sort(kw, StringComparer.Ordinal);
                        string key = id.ToString(CultureInfo.InvariantCulture) + "|" + string.Join(" ", kw) + (m.enableInstancing ? "|i" : "") +
                                     (kind == 1 ? "|s" : kind == 2 ? "|" + r.GetType().Name : "");
                        if (SeenVariants.Add(key))
                        {
                            _fNewVariants++;
                            _sessionNewVariants++;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce("spike-scan", "Spike probe: renderer scan failed: " + e.Message);
            }
            finally
            {
                ScanRenderers.Clear();
                ScanMaterials.Clear();
            }
            long d = Stopwatch.GetTimestamp() - t0;
            _fProbeTicks += d;
            if (_depth > 0)
            {
                StackChild[_depth - 1] += d; // probe time is not the hook's own time
            }
        }

        // ---- player loop ----

        private static void InstallLoop()
        {
            PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
            PlayerLoopSystem[] top = root.subSystemList;
            if (top == null || top.Length == 0)
            {
                Log.Warning("Spike probe: empty Unity player loop, off");
                return;
            }
            var names = new List<string>();
            var kinds = new List<int>();
            var newTop = new List<PlayerLoopSystem>();
            newTop.Add(Marker(Add(names, kinds, "(frame start)", "", 0), true));
            foreach (PlayerLoopSystem phase in top)
            {
                string phaseName = phase.type?.Name ?? "?";
                newTop.Add(Marker(Add(names, kinds, phaseName + ".(start)", phaseName, 0), false));
                PlayerLoopSystem copy = phase;
                if (phase.subSystemList != null && phase.subSystemList.Length > 0)
                {
                    var subs = new List<PlayerLoopSystem>();
                    foreach (PlayerLoopSystem sub in phase.subSystemList)
                    {
                        subs.Add(Marker(Add(names, kinds, phaseName + "." + (sub.type?.Name ?? "?"), phaseName, IsForeign(sub) ? 1 : 0), false));
                        subs.Add(sub);
                    }
                    copy.subSystemList = subs.ToArray();
                }
                newTop.Add(copy);
            }
            _outsideIdx = Add(names, kinds, "(outside loop)", "", 5);
            newTop.Add(Marker(_outsideIdx, false));
            root.subSystemList = newTop.ToArray();
            _subNames = names.ToArray();
            _subKind = kinds.ToArray();
            _acc = new long[_subNames.Length];
            _sessionAcc = new long[_subNames.Length];
            StartRecorders();
            PlayerLoop.SetPlayerLoop(root);
            _active = true;
            Log.Info($"Spike probe: {_subNames.Length} player-loop markers; logical cores Environment={Environment.ProcessorCount} " +
                     $"SystemInfo={SystemInfo.processorCount} JobWorkerCount={JobsUtility.JobWorkerCount}/{JobsUtility.JobWorkerMaximumCount}; ProfilerRecorder metrics available={_availableMetrics}; frame timing stats={_frameTimingEnabled}");
        }

        private static bool IsForeign(PlayerLoopSystem s) => s.type != null && s.type.Assembly != typeof(PlayerLoop).Assembly && s.type.Namespace?.StartsWith("UnityEngine") != true;

        private static int Add(List<string> names, List<int> kinds, string name, string phase, int forced)
        {
            names.Add(name);
            int kind = forced;
            if (kind == 0)
            {
                if (phase == "TimeUpdate" || name.Contains("WaitForLastPresentation") || name.Contains("WaitForTargetFPS"))
                {
                    kind = 5;
                }
                else if (name.Contains("ScriptRun") || name.Contains("Coroutine") || name.Contains("MonoBehaviour"))
                {
                    kind = 1;
                }
                else if (name.Contains("Physics"))
                {
                    kind = 3;
                }
                else if (name.Contains("Preloading") || name.Contains("AsyncUpload") || name.Contains("AsyncRead"))
                {
                    kind = 4;
                }
                else if (phase == "PostLateUpdate")
                {
                    kind = 2;
                }
            }
            kinds.Add(kind);
            return names.Count - 1;
        }

        private static PlayerLoopSystem Marker(int idx, bool frameStart)
        {
            PlayerLoopSystem.UpdateFunction f;
            if (frameStart)
            {
                f = () => FrameStart(idx);
            }
            else
            {
                f = () => Mark(idx);
            }
            return new PlayerLoopSystem { type = typeof(SpikeProbe), updateDelegate = f };
        }

        private static void Mark(int idx)
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastIdx >= 0)
            {
                _acc[_lastIdx] += now - _lastTs;
            }
            _lastIdx = idx;
            _lastTs = now;
        }

        private static void FrameStart(int idx)
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastIdx >= 0)
            {
                _acc[_lastIdx] += now - _lastTs;
            }
            if (_frameStart != 0)
            {
                try
                {
                    EndFrame(now);
                }
                catch (Exception e)
                {
                    Log.WarningOnce("spike-endframe", "Spike probe: frame end failed: " + e);
                }
            }
            Array.Clear(_acc, 0, _acc.Length);
            Array.Clear(FCalls, 0, SCount);
            Array.Clear(FSelf, 0, SCount);
            Array.Clear(FMax, 0, SCount);
            Array.Clear(FMaxKey, 0, SCount);
            _prevNewVariants = _fNewVariants + _fNewShaders;
            _fNewPrefabs = _fNewShaders = _fNewVariants = 0;
            FNewShaderNames.Clear();
            _fProbeTicks = 0;
            _depth = 0;
            _frameStart = Stopwatch.GetTimestamp(); // after the bookkeeping: the probe's own frame-end work is not frame time
            _lastIdx = idx;
            _lastTs = _frameStart;
        }

        private static void StartRecorders()
        {
            _counters = new ProfilerRecorder[CounterNames.Length];
            for (int i = 0; i < CounterNames.Length; i++)
            {
                _counters[i] = StartRecorder(i < MemoryCounters ? ProfilerCategory.Memory : ProfilerCategory.Render, CounterNames[i]);
            }
            _markers = new ProfilerRecorder[MarkerNames.Length];
            _markerSeen = new bool[MarkerNames.Length];
            ProfilerCategory[] cats = { ProfilerCategory.Internal, ProfilerCategory.Render, ProfilerCategory.Memory, ProfilerCategory.Loading, ProfilerCategory.Physics, ProfilerCategory.Scripts };
            for (int i = 0; i < MarkerNames.Length; i++)
            {
                foreach (ProfilerCategory c in cats)
                {
                    ProfilerRecorder r = StartRecorder(c, MarkerNames[i]);
                    if (r.Valid)
                    {
                        _markers[i] = r;
                        break;
                    }
                    r.Dispose();
                }
            }
            try
            {
                _frameTimingEnabled = FrameTimingManager.IsFeatureEnabled();
                var list = new List<ProfilerRecorderHandle>();
                ProfilerRecorderHandle.GetAvailable(list);
                _availableMetrics = list.Count;
                var sb = new StringBuilder();
                foreach (ProfilerRecorderHandle h in list)
                {
                    ProfilerRecorderDescription d = ProfilerRecorderHandle.GetDescription(h);
                    sb.Append(d.Category.Name).Append('\t').Append(d.Name).Append('\t').Append(d.UnitType).Append('\t').Append(d.Flags).Append('\n');
                }
                Directory.CreateDirectory(Dir);
                AtomicFile.WriteAllText(Path.Combine(Dir, "profiler-available.txt"), sb.ToString());
            }
            catch (Exception e)
            {
                Log.Warning("Spike probe: metric listing failed: " + e.Message);
            }
        }

        private static ProfilerRecorder StartRecorder(ProfilerCategory cat, string name)
        {
            try
            {
                return ProfilerRecorder.StartNew(cat, name, 1);
            }
            catch (Exception)
            {
                return default;
            }
        }

        private static void EndFrame(long now)
        {
            long t0 = Stopwatch.GetTimestamp();
            double ms = (now - _frameStart) * MsPerTick;
            for (int i = 0; i < _counters.Length; i++)
            {
                long v = _counters[i].Valid ? _counters[i].LastValue : 0;
                CounterNow[i] = i < MemoryCounters ? v - CounterPrev[i] : v;
                CounterPrev[i] = v;
                if (v != 0)
                {
                    CounterSeen[i] = true;
                }
            }
            for (int i = 0; i < _markers.Length; i++)
            {
                if (_markers[i].Valid && _markers[i].LastValue != 0)
                {
                    _markerSeen[i] = true;
                }
            }
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            int d0 = g0 - _gc0, d1 = g1 - _gc1, d2 = g2 - _gc2;
            _gc0 = g0;
            _gc1 = g1;
            _gc2 = g2;
            for (int i = 0; i < _acc.Length; i++)
            {
                _sessionAcc[i] += _acc[i];
            }
            for (int s = 0; s < SCount; s++)
            {
                SessionCalls[s] += FCalls[s];
                SessionSelf[s] += FSelf[s];
            }
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                ScriptProbe.EndFrame(false, false, ms);
                return;
            }
            if (_playStart == 0)
            {
                _playStart = now;
                _lastFlush = now;
            }
            Vector3 pos = player.transform.position;
            Vector2s zone = ZoneSystem.GetZone(pos);
            if (!zone.Equals(_zone))
            {
                _zone = zone;
                _zoneChangedAt = now;
            }
            string seg = Segment ?? "play";
            if (_seg == null || !ReferenceEquals(seg, _segName))
            {
                _segName = seg;
                if (!Segments.TryGetValue(seg, out _seg))
                {
                    _seg = new SegStats { Name = seg };
                    Segments[seg] = _seg;
                }
            }
            _seg.Frames++;
            _seg.TotalMs += ms;
            _seg.Hist[Math.Min(HistBuckets - 1, (int)(ms * 2))]++;
            if (ms > _seg.MaxMs)
            {
                _seg.MaxMs = (float)ms;
            }
            bool spike = ms >= _thresholdMs;
            string scriptTop = ScriptProbe.EndFrame(true, spike, ms);
            if (spike)
            {
                _seg.Spikes++;
                _seg.SpikeMs += ms;
                if (Spikes.Count < MaxSpikes)
                {
                    Spike s = Record(player, pos, zone, now, ms, d0, d1, d2);
                    s.ScriptTop = scriptTop;
                    Spikes.Add(s);
                }
            }
            long spent = Stopwatch.GetTimestamp() - t0;
            _probeTicks += spent + _fProbeTicks;
            _probeMaxTicks = Math.Max(_probeMaxTicks, spent + _fProbeTicks);
            _probeFrames++;
            if ((now - _lastFlush) * MsPerTick > 30000)
            {
                _lastFlush = now;
                Write(false);
            }
        }

        private static Spike Record(Player player, Vector3 pos, Vector2s zone, long now, double ms, int d0, int d1, int d2)
        {
            var s = new Spike
            {
                T = (now - _playStart) * MsPerTick / 1000.0,
                Utc = DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                Ms = (float)ms,
                Segment = _segName,
                Teleporting = player.IsTeleporting(),
                Interior = player.InInterior(),
                ZoneChanged = (now - _zoneChangedAt) * MsPerTick < 1000,
                Speed = player.GetVelocity().magnitude,
                Pos = ((int)pos.x).ToString(CultureInfo.InvariantCulture) + "," + ((int)pos.y).ToString(CultureInfo.InvariantCulture) + "," +
                      ((int)pos.z).ToString(CultureInfo.InvariantCulture),
                Zone = zone.x.ToString(CultureInfo.InvariantCulture) + "," + zone.y.ToString(CultureInfo.InvariantCulture),
                Gc0 = d0,
                Gc1 = d1,
                Gc2 = d2,
                Counters = (long[])CounterNow.Clone(),
                Calls = (int[])FCalls.Clone(),
                SelfMs = FSelf.Select(t => (float)(t * MsPerTick)).ToArray(),
                MaxName = new string[SCount],
                NewPrefabs = _fNewPrefabs,
                NewShaders = _fNewShaders,
                NewVariants = _fNewVariants,
                PrevNewVariants = _prevNewVariants,
                NewShaderNames = string.Join(";", FNewShaderNames.ToArray()),
                ProbeMs = (float)(_fProbeTicks * MsPerTick),
            };
            try
            {
                s.Combat = _combatTimer(player) < 5f;
                foreach (Character c in Character.GetAllCharacters())
                {
                    if (c != null && c != player && !c.IsPlayer() && !c.IsTamed() && !c.IsDead() && (c.transform.position - pos).sqrMagnitude < 900f)
                    {
                        s.Mobs++;
                    }
                }
            }
            catch (Exception)
            {
                // context only
            }
            for (int i = 0; i < SCount; i++)
            {
                object key = FMaxKey[i];
                if (key == null || FMax[i] * MsPerTick < 0.5)
                {
                    continue;
                }
                string name = key as string;
                if (key is int hash)
                {
                    GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(hash) : null;
                    name = prefab != null ? prefab.name : hash.ToString(CultureInfo.InvariantCulture);
                }
                else if (key is Vector2s z)
                {
                    name = z.x.ToString(CultureInfo.InvariantCulture) + "," + z.y.ToString(CultureInfo.InvariantCulture);
                }
                s.MaxName[i] = name + " " + (FMax[i] * MsPerTick).ToString("F1", CultureInfo.InvariantCulture);
            }
            var top = new List<(int i, long t)>();
            for (int i = 0; i < _acc.Length; i++)
            {
                double m = _acc[i] * MsPerTick;
                switch (_subKind[i])
                {
                    case 1: s.ScriptMs += (float)m; break;
                    case 2: s.RenderMs += (float)m; break;
                    case 3: s.PhysicsMs += (float)m; break;
                    case 4: s.LoadingMs += (float)m; break;
                    case 5: s.OutsideMs += (float)m; break;
                }
                if (m >= 1.0)
                {
                    top.Add((i, _acc[i]));
                }
            }
            s.TopSubs = string.Join("; ", top.OrderByDescending(x => x.t).Take(4)
                .Select(x => _subNames[x.i] + " " + (x.t * MsPerTick).ToString("F1", CultureInfo.InvariantCulture)).ToArray());
            s.Cause = Classify(s, top);
            return s;
        }

        /// <summary>Script subsystem time no hook covers (minus the probe's own time).</summary>
        private static float Residual(Spike s) => Math.Max(0f, s.ScriptMs - s.SelfMs.Sum() - s.ProbeMs);

        private static string Classify(Spike s, List<(int i, long t)> top)
        {
            string best = null;
            float bestMs = 0;
            for (int i = 0; i < SCount; i++)
            {
                if (s.SelfMs[i] > bestMs)
                {
                    bestMs = s.SelfMs[i];
                    best = SignalNames[i];
                }
            }
            float scripts = Residual(s);
            void Consider(string name, float v)
            {
                if (v > bestMs)
                {
                    bestMs = v;
                    best = name;
                }
            }
            bool gc = s.Gc0 + s.Gc1 + s.Gc2 > 0;
            Consider(gc ? "scripts+gc" : "scripts", scripts);
            Consider(s.NewVariants + s.PrevNewVariants + s.NewShaders > 0 ? "render+newVariants" : "render", s.RenderMs);
            Consider("physics", s.PhysicsMs);
            Consider("loading", s.LoadingMs);
            Consider(s.NewVariants + s.PrevNewVariants + s.NewShaders > 0 ? "present+newVariants" : "present", s.OutsideMs);
            float otherMs = 0;
            foreach ((int i, long t) in top)
            {
                if (_subKind[i] == 0)
                {
                    otherMs = Math.Max(otherMs, (float)(t * MsPerTick));
                }
            }
            Consider("other", otherMs);
            return best ?? "other";
        }

        // ---- output ----

        /// <summary>Writes spikes.tsv + spike-summary.txt now (main thread, synchronous); for a test driver before it quits.</summary>
        public static void Flush() => Write(true);

        private static void Write(bool sync)
        {
            if (!_active || _playStart == 0)
            {
                return;
            }
            if (!sync && Spikes.Count == _flushedSpikes)
            {
                return;
            }
            _flushedSpikes = Spikes.Count;
            Spike[] spikes = Spikes.ToArray();
            SegStats[] segs = Segments.Values.Select(Copy).ToArray();
            long[] sessionAcc = (long[])_sessionAcc.Clone();
            long[] calls = (long[])SessionCalls.Clone();
            long[] self = (long[])SessionSelf.Clone();
            bool[] counterSeen = (bool[])CounterSeen.Clone();
            bool[] markerValid = _markers.Select(m => m.Valid).ToArray();
            bool[] markerSeen = (bool[])_markerSeen.Clone();
            string probe = string.Format(CultureInfo.InvariantCulture, "probe overhead: mean {0:F1} us/frame, max {1:F2} ms over {2} play frames (frame-end bookkeeping + first-seen scans)",
                _probeFrames > 0 ? _probeTicks * MsPerTick * 1000.0 / _probeFrames : 0, _probeMaxTicks * MsPerTick, _probeFrames);
            object scripts = ScriptProbe.Snapshot();
            string firstSeen = $"first seen this session: {SeenNetPrefabs.Count} net prefabs, {SeenNamed.Count} locations/rooms, {_sessionNewShaders} shaders, {_sessionNewVariants} shader/keyword combinations";
            void Run()
            {
                if (Interlocked.Exchange(ref _writing, 1) != 0)
                {
                    return;
                }
                try
                {
                    Directory.CreateDirectory(Dir);
                    AtomicFile.WriteAllText(Path.Combine(Dir, "spikes.tsv"), Tsv(spikes));
                    AtomicFile.WriteAllText(Path.Combine(Dir, "spike-summary.txt"),
                        Summary(spikes, segs, sessionAcc, calls, self, counterSeen, markerValid, markerSeen, probe, firstSeen));
                    ScriptProbe.Write(scripts, spikes.Sum(Residual), spikes.Sum(s => s.ScriptMs));
                }
                catch (Exception e)
                {
                    Log.Warning("Spike probe: write failed: " + e.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref _writing, 0);
                }
            }
            if (sync)
            {
                while (Volatile.Read(ref _writing) != 0)
                {
                    Thread.Sleep(10);
                }
                Run();
            }
            else
            {
                ThreadPool.QueueUserWorkItem(_ => Run());
            }
        }

        private static SegStats Copy(SegStats s)
        {
            var c = new SegStats { Name = s.Name, Frames = s.Frames, TotalMs = s.TotalMs, MaxMs = s.MaxMs, Spikes = s.Spikes, SpikeMs = s.SpikeMs };
            Array.Copy(s.Hist, c.Hist, HistBuckets);
            return c;
        }

        private static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

        private static string Tsv(Spike[] spikes)
        {
            var sb = new StringBuilder();
            sb.Append("t\tutc\tms\tcause\tsegment\tteleporting\tinterior\tzoneChanged\tspeed\tmobs\tcombat\tpos\tzone\tgc0\tgc1\tgc2");
            foreach (string c in CounterNames)
            {
                sb.Append('\t').Append(c.Replace(' ', '_'));
            }
            foreach (string n in SignalNames)
            {
                sb.Append('\t').Append(n).Append("_n\t").Append(n).Append("_ms\t").Append(n).Append("_max");
            }
            sb.Append("\tnewPrefabs\tnewShaders\tnewVariants\tprevNewVariants\tnewShaderNames\tscriptMs\trenderMs\tphysicsMs\tloadingMs\toutsideMs\tprobeMs\ttopSubsystems\tscriptTop\n");
            foreach (Spike s in spikes)
            {
                sb.Append(s.T.ToString("F2", CultureInfo.InvariantCulture)).Append('\t').Append(s.Utc).Append('\t').Append(F(s.Ms)).Append('\t').Append(s.Cause)
                    .Append('\t').Append(s.Segment).Append('\t').Append(s.Teleporting ? 1 : 0).Append('\t').Append(s.Interior ? 1 : 0).Append('\t')
                    .Append(s.ZoneChanged ? 1 : 0).Append('\t').Append(F(s.Speed)).Append('\t').Append(s.Mobs).Append('\t').Append(s.Combat ? 1 : 0)
                    .Append('\t').Append(s.Pos).Append('\t').Append(s.Zone).Append('\t').Append(s.Gc0).Append('\t').Append(s.Gc1).Append('\t').Append(s.Gc2);
                for (int i = 0; i < s.Counters.Length; i++)
                {
                    sb.Append('\t').Append(i < MemoryCounters ? (s.Counters[i] / 1024).ToString(CultureInfo.InvariantCulture) + "KB" : s.Counters[i].ToString(CultureInfo.InvariantCulture));
                }
                for (int i = 0; i < SCount; i++)
                {
                    sb.Append('\t').Append(s.Calls[i]).Append('\t').Append(F(s.SelfMs[i])).Append('\t').Append(s.MaxName[i] ?? "");
                }
                sb.Append('\t').Append(s.NewPrefabs).Append('\t').Append(s.NewShaders).Append('\t').Append(s.NewVariants).Append('\t').Append(s.PrevNewVariants)
                    .Append('\t').Append(s.NewShaderNames).Append('\t').Append(F(s.ScriptMs)).Append('\t').Append(F(s.RenderMs)).Append('\t').Append(F(s.PhysicsMs))
                    .Append('\t').Append(F(s.LoadingMs)).Append('\t').Append(F(s.OutsideMs)).Append('\t').Append(s.ProbeMs.ToString("F2", CultureInfo.InvariantCulture))
                    .Append('\t').Append(s.TopSubs).Append('\t').Append(s.ScriptTop ?? "").Append('\n');
            }
            return sb.ToString();
        }

        private static double Pct(int[] hist, long n, double q)
        {
            long want = (long)Math.Ceiling(n * q);
            long seen = 0;
            for (int i = 0; i < hist.Length; i++)
            {
                seen += hist[i];
                if (seen >= want && want > 0)
                {
                    return (i + 1) * 0.5;
                }
            }
            return 0;
        }

        private static string Summary(Spike[] spikes, SegStats[] segs, long[] sessionAcc, long[] calls, long[] self, bool[] counterSeen,
            bool[] markerValid, bool[] markerSeen, string probe, string firstSeen)
        {
            var sb = new StringBuilder();
            sb.Append("Morgott.Preloader spike probe - ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" - threshold ")
                .Append(F(_thresholdMs)).Append(" ms - logical cores Environment=").Append(Environment.ProcessorCount).Append(" SystemInfo=")
                .Append(SystemInfo.processorCount).Append(" JobWorkerCount=").Append(JobsUtility.JobWorkerCount).Append('/').Append(JobsUtility.JobWorkerMaximumCount).Append('\n').Append(probe).Append('\n').Append(firstSeen).Append("\n\n");
            sb.Append("== frame time per segment (play frames, p = upper bound of a 0.5 ms bucket)\n");
            sb.Append(string.Format(CultureInfo.InvariantCulture, "{0,-18} {1,7} {2,7} {3,7} {4,7} {5,7} {6,8} {7,6} {8,9}\n", "segment", "frames", "avg", "p50", "p99", "p99.9", "max", "spikes", "spikeMs"));
            var all = new SegStats { Name = "ALL" };
            foreach (SegStats s in segs)
            {
                all.Frames += s.Frames;
                all.TotalMs += s.TotalMs;
                all.MaxMs = Math.Max(all.MaxMs, s.MaxMs);
                all.Spikes += s.Spikes;
                all.SpikeMs += s.SpikeMs;
                for (int i = 0; i < HistBuckets; i++)
                {
                    all.Hist[i] += s.Hist[i];
                }
            }
            foreach (SegStats s in segs.Concat(new[] { all }))
            {
                sb.Append(string.Format(CultureInfo.InvariantCulture, "{0,-18} {1,7} {2,7:F1} {3,7:F1} {4,7:F1} {5,7:F1} {6,8:F1} {7,6} {8,9:F0}\n", s.Name, s.Frames,
                    s.Frames > 0 ? s.TotalMs / s.Frames : 0, Pct(s.Hist, s.Frames, 0.5), Pct(s.Hist, s.Frames, 0.99), Pct(s.Hist, s.Frames, 0.999), s.MaxMs, s.Spikes, s.SpikeMs));
            }
            double p50 = Pct(all.Hist, all.Frames, 0.5);
            sb.Append("\n== causes ranked by total spike ms (cause = largest of: hook self times, script residual, render, physics, loading, outside, other)\n");
            sb.Append(string.Format(CultureInfo.InvariantCulture, "{0,-20} {1,6} {2,9} {3,9} {4,8} {5,6} {6,6}  {7}\n", "cause", "count", "totalMs", "excessMs", "maxMs", "telep", "newVar", "owner"));
            foreach (var g in spikes.GroupBy(s => s.Cause).OrderByDescending(g => g.Sum(s => s.Ms)))
            {
                Owners.TryGetValue(g.Key, out string owner);
                sb.Append(string.Format(CultureInfo.InvariantCulture, "{0,-20} {1,6} {2,9:F0} {3,9:F0} {4,8:F1} {5,6} {6,6}  {7}\n", g.Key, g.Count(), g.Sum(s => s.Ms),
                    g.Sum(s => Math.Max(0, s.Ms - p50)), g.Max(s => s.Ms), g.Count(s => s.Teleporting), g.Count(s => s.NewVariants + s.PrevNewVariants > 0), owner));
            }
            sb.Append("\n== causes per segment\n");
            foreach (var seg in spikes.GroupBy(s => s.Segment))
            {
                sb.Append(seg.Key).Append(": ").Append(string.Join(", ", seg.GroupBy(s => s.Cause).OrderByDescending(g => g.Sum(s => s.Ms))
                    .Select(g => g.Key + " " + g.Count() + "x/" + F(g.Sum(s => s.Ms)) + "ms").ToArray())).Append('\n');
            }
            sb.Append("\n== hook signals over all spike frames (self ms) and over the whole session\n");
            for (int i = 0; i < SCount; i++)
            {
                int idx = i;
                sb.Append(string.Format(CultureInfo.InvariantCulture, "{0,-18} spikes: {1,6} calls {2,9:F1} ms   session: {3,8} calls {4,9:F1} ms\n", SignalNames[i],
                    spikes.Sum(s => s.Calls[idx]), spikes.Sum(s => s.SelfMs[idx]), calls[i], self[i] * MsPerTick));
            }
            int withNew = spikes.Count(s => s.NewVariants + s.PrevNewVariants + s.NewShaders > 0);
            sb.Append(string.Format(CultureInfo.InvariantCulture, "\n== first-seen shader correlation: {0} of {1} spike frames had first-seen shader variants in the frame or the one before ({2:F0} of {3:F0} ms)\n",
                withNew, spikes.Length, spikes.Where(s => s.NewVariants + s.PrevNewVariants + s.NewShaders > 0).Sum(s => s.Ms), spikes.Sum(s => s.Ms)));
            sb.Append("\n== session time per player-loop subsystem (top 25, all frames incl. menu/loading)\n");
            foreach (int i in Enumerable.Range(0, sessionAcc.Length).OrderByDescending(i => sessionAcc[i]).Take(25))
            {
                sb.Append(string.Format(CultureInfo.InvariantCulture, "{0,-60} {1,10:F0} ms\n", _subNames[i], sessionAcc[i] * MsPerTick));
            }
            sb.Append("\n== top 40 spike frames\n");
            foreach (Spike s in spikes.OrderByDescending(s => s.Ms).Take(40))
            {
                sb.Append(string.Format(CultureInfo.InvariantCulture, "{0,8:F1} ms t={1,7:F1}s {2} [{3}] {4}{5}{6} spd {7:F1} mobs {8} gc {9}/{10}/{11} | ", s.Ms, s.T, s.Cause, s.Segment,
                    s.Teleporting ? "TP " : "", s.Interior ? "IN " : "", s.ZoneChanged ? "ZONE " : "", s.Speed, s.Mobs, s.Gc0, s.Gc1, s.Gc2));
                for (int i = 0; i < SCount; i++)
                {
                    if (s.Calls[i] > 0 && s.SelfMs[i] >= 0.5f)
                    {
                        sb.Append(SignalNames[i]).Append(' ').Append(s.Calls[i]).Append("x ").Append(F(s.SelfMs[i])).Append("ms");
                        if (s.MaxName[i] != null)
                        {
                            sb.Append(" (max ").Append(s.MaxName[i]).Append(')');
                        }
                        sb.Append("; ");
                    }
                }
                sb.Append("new ").Append(s.NewPrefabs).Append(" prefabs/").Append(s.NewShaders).Append(" shaders/").Append(s.NewVariants).Append('+').Append(s.PrevNewVariants)
                    .Append(" var | script ").Append(F(s.ScriptMs)).Append(" render ").Append(F(s.RenderMs)).Append(" phys ").Append(F(s.PhysicsMs)).Append(" | ")
                    .Append(s.TopSubs);
                if (s.NewShaderNames.Length > 0)
                {
                    sb.Append(" | shaders ").Append(s.NewShaderNames);
                }
                sb.Append(" | setpass ").Append(s.Counters[4]).Append(" draws ").Append(s.Counters[5]).Append(" vbKB ").Append(s.Counters[7] / 1024)
                    .Append(" gcUsedKB ").Append(s.Counters[0] / 1024).Append(" totalUsedKB ").Append(s.Counters[2] / 1024).Append('\n');
            }
            sb.Append("\n== ProfilerRecorder in this player (valid = handle resolved, nonzero = ever reported a value; profiler-available.txt lists every metric)\n");
            for (int i = 0; i < CounterNames.Length; i++)
            {
                sb.Append("counter ").Append(CounterNames[i]).Append(": valid=").Append(i < _counters.Length && _counters[i].Valid).Append(" nonzero=").Append(counterSeen[i]).Append('\n');
            }
            for (int i = 0; i < MarkerNames.Length && i < markerValid.Length; i++)
            {
                sb.Append("marker ").Append(MarkerNames[i]).Append(": valid=").Append(markerValid[i]).Append(" nonzero=").Append(markerSeen[i]).Append('\n');
            }
            sb.Append("metrics available=").Append(_availableMetrics).Append(" frameTimingStats=").Append(_frameTimingEnabled).Append('\n');
            return sb.ToString();
        }
    }
}
