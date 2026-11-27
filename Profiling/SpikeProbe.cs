using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
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
            public string Utc, Segment, Pos, Zone, NewShaderNames, TopSubs, Cause, ScriptTop;
            public float Ms, Speed, ScriptMs, RenderMs, PhysicsMs, LoadingMs, OutsideMs, ProbeMs;
            public bool Teleporting, Interior, ZoneChanged, Combat;
            public int Mobs, Gc0, Gc1, Gc2, NewPrefabs, NewShaders, NewVariants, PrevNewVariants;
            public long[] Counters;
            public int[] Calls;
            public float[] SelfMs;
            public string[] MaxName;
            public bool NewVariantsNear => NewVariants + PrevNewVariants + NewShaders > 0;
        }

        private sealed class SegStats
        {
            public string Name;
            public long Frames, Spikes;
            public double TotalMs, SpikeMs;
            public float MaxMs;
            public int[] Hist = new int[HistBuckets];

            public SegStats Copy()
            {
                var c = (SegStats)MemberwiseClone();
                c.Hist = (int[])Hist.Clone();
                return c;
            }
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
        private static long _lastTs, _frameStart;

        // Per-frame hook accumulators.
        private static readonly int[] FCalls = new int[SCount];
        private static readonly long[] FSelf = new long[SCount];
        private static readonly long[] FMax = new long[SCount];
        private static readonly string[] FMaxName = new string[SCount];
        private static int _fMaxPrefab;
        private static bool _fMaxHasPrefab;
        private static Vector2s _fMaxZone;
        private static bool _fMaxHasZone;
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

        private static string Dir => Profiler.OutputDir;

        /// <summary>Chainloader.Initialize postfix (engine up, no plugin Awake yet): hooks now, player loop at the main menu.</summary>
        internal static void Install(Harmony harmony)
        {
            _thresholdMs = Math.Max(1.0, Config.SpikeThresholdMs.Value);
            _mainThread = Environment.CurrentManagedThreadId;
            _combatTimer = AccessTools.FieldRefAccess<Humanoid, float>("m_lastCombatTimer");
            int hooked = Hook(harmony, AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObject"), SCreate, nameof(CreateFin), nameof(CreatePost))
                         + Hook(harmony, AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnZone"), SZone, nameof(ZoneFin), null)
                         + Hook(harmony, AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnLocation"), SLocation, nameof(LocationFin), nameof(NamedPost))
                         + Hook(harmony, AccessTools.GetDeclaredMethods(typeof(DungeonGenerator)).FirstOrDefault(m => m.Name == "PlaceRoom" && m.ReturnType == typeof(Room)),
                             SRoom, nameof(Fin), nameof(RoomPost))
                         + Hook(harmony, AccessTools.DeclaredMethod(typeof(Heightmap), "ForceGenerateAll"), SForceGen, nameof(Fin), null)
                         + Hook(harmony, AccessTools.DeclaredMethod(typeof(Texture2D), "Apply", new[] { typeof(bool), typeof(bool) }), STexApply, nameof(Fin), null);
            foreach (string m in new[] { "Regenerate", "RebuildRenderMesh", "RebuildCollisionMesh" })
                hooked += Hook(harmony, AccessTools.DeclaredMethod(typeof(Heightmap), m), SHeightmap, nameof(Fin), null);
            foreach (string m in new[] { "LoadAsset_Internal", "LoadFromFile_Internal", "LoadFromMemory_Internal", "LoadFromStreamInternal", "LoadAssetWithSubAssets_Internal" })
                hooked += Hook(harmony, AccessTools.DeclaredMethod(typeof(AssetBundle), m), SBundle, nameof(Fin), null);
            Lifecycle.MenuReady += () => Log.Guard("Spike probe player loop", InstallLoop);
            if (Config.ScriptBreakdown.Value)
                Lifecycle.MenuReady += () => Log.Guard("Script probe install", ScriptProbe.Install);
            Application.quitting += () => Log.Guard("Spike probe flush", () => Write(true));
            Log.Info($"Spike probe: {hooked.ToString(CultureInfo.InvariantCulture)} hooks, threshold {_thresholdMs.ToString("F0", CultureInfo.InvariantCulture)} ms; player loop at the main menu");
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
            __state = 0;
            if (!_active || _depth >= StackStart.Length || Environment.CurrentManagedThreadId != _mainThread) return;
            __state = Stopwatch.GetTimestamp();
            StackStart[_depth] = __state;
            StackChild[_depth] = 0;
            _depth++;
        }

        /// <summary>Self ticks of the call that ends now (hooked children subtracted), or -1 when it was not measured.</summary>
        private static long Exit(long state, int signal)
        {
            if (state == 0 || _depth == 0) return -1;
            long total = Stopwatch.GetTimestamp() - state;
            _depth--;
            ScriptProbe.AddChild(total, _depth);
            long self = total - StackChild[_depth];
            if (_depth > 0) StackChild[_depth - 1] += total;
            FCalls[signal]++;
            FSelf[signal] += self;
            return self;
        }

        /// <summary>Ends the hooked call; true when it is the slowest of its signal this frame (caller then names it).</summary>
        private static bool Slowest(long state, int s)
        {
            long self = Exit(state, s);
            if (self <= FMax[s]) return false;
            FMax[s] = self;
            return true;
        }

        private static void Fin(long __state, MethodBase __originalMethod)
        {
            if (__state != 0 && SignalOf.TryGetValue(__originalMethod, out int s) && Slowest(__state, s)) FMaxName[s] = __originalMethod.Name;
        }

        private static void CreateFin(long __state, ZDO zdo)
        {
            if (!Slowest(__state, SCreate)) return;
            _fMaxHasPrefab = zdo != null;
            if (zdo != null) _fMaxPrefab = zdo.GetPrefab();
        }

        private static void ZoneFin(long __state, Vector2s zoneID)
        {
            if (!Slowest(__state, SZone)) return;
            _fMaxHasZone = true;
            _fMaxZone = zoneID;
        }

        private static void LocationFin(long __state, ZoneSystem.ZoneLocation location)
        {
            if (Slowest(__state, SLocation)) FMaxName[SLocation] = location?.m_prefabName;
        }

        private static void CreatePost(GameObject __result, ZDO zdo)
        {
            if (_active && __result != null && zdo != null && SeenNetPrefabs.Add(zdo.GetPrefab())) Scan(__result);
        }

        private static void NamedPost(GameObject __result)
        {
            if (_active && __result != null && SeenNamed.Add("l:" + __result.name)) Scan(__result);
        }

        private static void RoomPost(Room __result)
        {
            if (_active && __result != null && SeenNamed.Add("r:" + __result.name)) Scan(__result.gameObject);
        }

        /// <summary>First instance of a prefab: its shaders and (shader, keywords, instancing) combinations not seen yet.</summary>
        private static void Scan(GameObject go)
        {
            if (Environment.CurrentManagedThreadId != _mainThread) return;
            long t0 = Stopwatch.GetTimestamp();
            _fNewPrefabs++;
            try
            {
                go.GetComponentsInChildren(true, ScanRenderers);
                foreach (Renderer r in ScanRenderers)
                {
                    if (r == null) continue;
                    r.GetSharedMaterials(ScanMaterials);
                    foreach (Material m in ScanMaterials)
                    {
                        // Materials are shared across prefabs and renderers: each (material, renderer kind) is looked at once.
                        int kind = r is SkinnedMeshRenderer ? 1 : r is MeshRenderer ? 0 : 2;
                        if (m == null || !SeenMaterials.Add(((long)m.GetInstanceID() << 2) | (uint)kind)) continue;
                        Shader shader = m.shader;
                        if (shader == null) continue;
                        int id = shader.GetInstanceID();
                        if (SeenShaders.Add(id))
                        {
                            _fNewShaders++;
                            _sessionNewShaders++;
                            if (FNewShaderNames.Count < 6) FNewShaderNames.Add(shader.name);
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
            if (_depth > 0) StackChild[_depth - 1] += d; // probe time is not the hook's own time
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
            var newTop = new List<PlayerLoopSystem> { Marker(Add(names, kinds, "(frame start)", "", 0), true) };
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
            newTop.Add(Marker(Add(names, kinds, "(outside loop)", "", 5), false));
            root.subSystemList = newTop.ToArray();
            _subNames = names.ToArray();
            _subKind = kinds.ToArray();
            _acc = new long[_subNames.Length];
            _sessionAcc = new long[_subNames.Length];
            StartRecorders();
            PlayerLoop.SetPlayerLoop(root);
            _active = true;
            Log.Info($"Spike probe: {_subNames.Length.ToString(CultureInfo.InvariantCulture)} player-loop markers; logical cores Environment={Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)} " +
                     $"SystemInfo={SystemInfo.processorCount.ToString(CultureInfo.InvariantCulture)} JobWorkerCount={JobsUtility.JobWorkerCount.ToString(CultureInfo.InvariantCulture)}/{JobsUtility.JobWorkerMaximumCount.ToString(CultureInfo.InvariantCulture)}; ProfilerRecorder metrics available={_availableMetrics.ToString(CultureInfo.InvariantCulture)}; frame timing stats={_frameTimingEnabled.ToString()}");
        }

        private static bool IsForeign(PlayerLoopSystem s) => s.type != null && s.type.Assembly != typeof(PlayerLoop).Assembly && s.type.Namespace?.StartsWith("UnityEngine") != true;

        private static int Add(List<string> names, List<int> kinds, string name, string phase, int forced)
        {
            names.Add(name);
            kinds.Add(forced != 0 ? forced
                : phase == "TimeUpdate" || name.Contains("WaitForLastPresentation") || name.Contains("WaitForTargetFPS") ? 5
                : name.Contains("ScriptRun") || name.Contains("Coroutine") || name.Contains("MonoBehaviour") ? 1
                : name.Contains("Physics") ? 3
                : name.Contains("Preloading") || name.Contains("AsyncUpload") || name.Contains("AsyncRead") ? 4
                : phase == "PostLateUpdate" ? 2 : 0);
            return names.Count - 1;
        }

        private sealed class LoopMarker(int idx, bool frameStart)
        {
            public void Tick()
            {
                if (frameStart) FrameStart(idx);
                else Mark(idx);
            }
        }

        private static PlayerLoopSystem Marker(int idx, bool frameStart) => new PlayerLoopSystem
        {
            type = typeof(SpikeProbe),
            updateDelegate = new LoopMarker(idx, frameStart).Tick,
        };

        private static long Mark(int idx)
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastIdx >= 0) _acc[_lastIdx] += now - _lastTs;
            _lastIdx = idx;
            _lastTs = now;
            return now;
        }

        private static void FrameStart(int idx)
        {
            long now = Mark(idx);
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
            Array.Clear(FMaxName, 0, SCount);
            _fMaxHasPrefab = false;
            _fMaxHasZone = false;
            _prevNewVariants = _fNewVariants + _fNewShaders;
            _fNewPrefabs = _fNewShaders = _fNewVariants = 0;
            FNewShaderNames.Clear();
            _fProbeTicks = 0;
            _depth = 0;
            _frameStart = _lastTs = Stopwatch.GetTimestamp(); // after the bookkeeping: the probe's own frame-end work is not frame time
        }

        private static void StartRecorders()
        {
            _counters = CounterNames.Select((n, i) => StartRecorder(i < MemoryCounters ? ProfilerCategory.Memory : ProfilerCategory.Render, n)).ToArray();
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
                    sb.Append(d.Category.Name).Append('\t').Append(d.Name).Append('\t').Append(UnitTypeName(d.UnitType)).Append('\t');
                    AppendMarkerFlags(sb, d.Flags);
                    sb.Append('\n');
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
                if (v != 0) CounterSeen[i] = true;
            }
            for (int i = 0; i < _markers.Length; i++)
                if (_markers[i].Valid && _markers[i].LastValue != 0) _markerSeen[i] = true;
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            int d0 = g0 - _gc0, d1 = g1 - _gc1, d2 = g2 - _gc2;
            (_gc0, _gc1, _gc2) = (g0, g1, g2);
            for (int i = 0; i < _acc.Length; i++) _sessionAcc[i] += _acc[i];
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
            if (_playStart == 0) _playStart = _lastFlush = now;
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
                if (!Segments.TryGetValue(seg, out _seg)) Segments[seg] = _seg = new SegStats { Name = seg };
            }
            _seg.Frames++;
            _seg.TotalMs += ms;
            _seg.Hist[Math.Min(HistBuckets - 1, (int)(ms * 2))]++;
            _seg.MaxMs = Math.Max(_seg.MaxMs, (float)ms);
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
            long spent = Stopwatch.GetTimestamp() - t0 + _fProbeTicks;
            _probeTicks += spent;
            _probeMaxTicks = Math.Max(_probeMaxTicks, spent);
            _probeFrames++;
            if ((now - _lastFlush) * MsPerTick > 30000)
            {
                _lastFlush = now;
                Write(false);
            }
        }

        private static string UnitTypeName(ProfilerMarkerDataUnit unit) => unit switch
        {
            ProfilerMarkerDataUnit.Undefined => nameof(ProfilerMarkerDataUnit.Undefined),
            ProfilerMarkerDataUnit.TimeNanoseconds => nameof(ProfilerMarkerDataUnit.TimeNanoseconds),
            ProfilerMarkerDataUnit.Bytes => nameof(ProfilerMarkerDataUnit.Bytes),
            ProfilerMarkerDataUnit.Count => nameof(ProfilerMarkerDataUnit.Count),
            ProfilerMarkerDataUnit.Percent => nameof(ProfilerMarkerDataUnit.Percent),
            ProfilerMarkerDataUnit.FrequencyHz => nameof(ProfilerMarkerDataUnit.FrequencyHz),
            _ => ((int)unit).ToString(CultureInfo.InvariantCulture),
        };

        private static void AppendMarkerFlags(StringBuilder sb, Unity.Profiling.LowLevel.MarkerFlags flags)
        {
            int v = (int)flags;
            if (v == 0)
            {
                sb.Append(nameof(Unity.Profiling.LowLevel.MarkerFlags.Default));
                return;
            }
            bool any = false;
            void Flag(int bit, string name)
            {
                if ((v & bit) == 0) return;
                if (any) sb.Append(", ");
                sb.Append(name);
                any = true;
                v &= ~bit;
            }
            Flag(2, nameof(Unity.Profiling.LowLevel.MarkerFlags.Script));
            Flag(4, nameof(Unity.Profiling.LowLevel.MarkerFlags.AvailabilityEditor));
            Flag(8, nameof(Unity.Profiling.LowLevel.MarkerFlags.AvailabilityNonDevelopment));
            Flag(16, nameof(Unity.Profiling.LowLevel.MarkerFlags.Warning));
            Flag(32, nameof(Unity.Profiling.LowLevel.MarkerFlags.ScriptInvoke));
            Flag(64, nameof(Unity.Profiling.LowLevel.MarkerFlags.ScriptDeepProfiler));
            Flag(128, nameof(Unity.Profiling.LowLevel.MarkerFlags.Counter));
            Flag(256, nameof(Unity.Profiling.LowLevel.MarkerFlags.SampleGPU));
            if (v != 0)
            {
                if (any) sb.Append(", ");
                sb.Append(v);
            }
        }

        private static Spike Record(Player player, Vector3 pos, Vector2s zone, long now, double ms, int d0, int d1, int d2)
        {
            var selfMs = new float[SCount];
            for (int i = 0; i < SCount; i++) selfMs[i] = (float)(FSelf[i] * MsPerTick);
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
                Pos = ((int)pos.x).ToString(CultureInfo.InvariantCulture) + "," + ((int)pos.y).ToString(CultureInfo.InvariantCulture) + "," + ((int)pos.z).ToString(CultureInfo.InvariantCulture),
                Zone = zone.x.ToString(CultureInfo.InvariantCulture) + "," + zone.y.ToString(CultureInfo.InvariantCulture),
                Gc0 = d0,
                Gc1 = d1,
                Gc2 = d2,
                Counters = (long[])CounterNow.Clone(),
                Calls = (int[])FCalls.Clone(),
                SelfMs = selfMs,
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
                List<Character> chars = Character.GetAllCharacters();
                int mobs = 0;
                for (int i = 0; i < chars.Count; i++)
                {
                    Character c = chars[i];
                    if (c != null && c != player && !c.IsPlayer() && !c.IsTamed() && !c.IsDead() &&
                        (c.transform.position - pos).sqrMagnitude < 900f)
                        mobs++;
                }
                s.Mobs = mobs;
            }
            catch (Exception)
            {
                // context only
            }
            for (int i = 0; i < SCount; i++)
            {
                if (FMax[i] * MsPerTick < 0.5) continue;
                string name;
                if (i == SCreate)
                {
                    if (!_fMaxHasPrefab) continue;
                    GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(_fMaxPrefab) : null;
                    name = prefab != null ? prefab.name : _fMaxPrefab.ToString(CultureInfo.InvariantCulture);
                }
                else if (i == SZone)
                {
                    if (!_fMaxHasZone) continue;
                    name = _fMaxZone.x.ToString(CultureInfo.InvariantCulture) + "," + _fMaxZone.y.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    name = FMaxName[i];
                    if (name == null) continue;
                }
                s.MaxName[i] = name + " " + F(FMax[i] * MsPerTick);
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
                if (m >= 1.0) top.Add((i, _acc[i]));
            }
            top.Sort(CompareSubTicksDesc);
            var topSb = new StringBuilder();
            int topTake = Math.Min(4, top.Count);
            for (int i = 0; i < topTake; i++)
            {
                if (i > 0) topSb.Append("; ");
                topSb.Append(_subNames[top[i].i]).Append(' ').Append(F(top[i].t * MsPerTick));
            }
            s.TopSubs = topSb.ToString();
            s.Cause = Classify(s, top);
            return s;
        }

        private static int CompareSubTicksDesc((int i, long t) a, (int i, long t) b) => b.t.CompareTo(a.t);

        /// <summary>Script subsystem time no hook covers (minus the probe's own time).</summary>
        private static float Residual(Spike s)
        {
            float sum = 0f;
            for (int i = 0; i < s.SelfMs.Length; i++) sum += s.SelfMs[i];
            return Math.Max(0f, s.ScriptMs - sum - s.ProbeMs);
        }

        private static string Classify(Spike s, List<(int i, long t)> top)
        {
            string best = null;
            float bestMs = 0;
            for (int i = 0; i < SCount; i++) Consider(SignalNames[i], s.SelfMs[i], ref best, ref bestMs);
            Consider(s.Gc0 + s.Gc1 + s.Gc2 > 0 ? "scripts+gc" : "scripts", Residual(s), ref best, ref bestMs);
            Consider(s.NewVariantsNear ? "render+newVariants" : "render", s.RenderMs, ref best, ref bestMs);
            Consider("physics", s.PhysicsMs, ref best, ref bestMs);
            Consider("loading", s.LoadingMs, ref best, ref bestMs);
            Consider(s.NewVariantsNear ? "present+newVariants" : "present", s.OutsideMs, ref best, ref bestMs);
            float otherMax = 0f;
            for (int i = 0; i < top.Count; i++)
            {
                if (_subKind[top[i].i] != 0) continue;
                float ms = (float)(top[i].t * MsPerTick);
                if (ms > otherMax) otherMax = ms;
            }
            Consider("other", otherMax, ref best, ref bestMs);
            return best ?? "other";
        }

        private static void Consider(string name, float v, ref string best, ref float bestMs)
        {
            if (v > bestMs)
            {
                bestMs = v;
                best = name;
            }
        }

        // ---- output ----

        /// <summary>Writes spikes.tsv + spike-summary.txt now (main thread, synchronous); for a test driver before it quits.</summary>
        public static void Flush() => Write(true);

        private sealed class WriteJob
        {
            public Spike[] Spikes;
            public SegStats[] Segs;
            public long[] SessionAcc;
            public long[] Calls;
            public long[] Self;
            public bool[] CounterSeen;
            public bool[] MarkerValid;
            public bool[] MarkerSeen;
            public string Probe;
            public object Scripts;
            public string FirstSeen;

            public void Run()
            {
                if (Interlocked.Exchange(ref _writing, 1) != 0) return;
                try
                {
                    Directory.CreateDirectory(Dir);
                    AtomicFile.WriteAllText(Path.Combine(Dir, "spikes.tsv"), Tsv(Spikes));
                    AtomicFile.WriteAllText(Path.Combine(Dir, "spike-summary.txt"),
                        Summary(Spikes, Segs, SessionAcc, Calls, Self, CounterSeen, MarkerValid, MarkerSeen, Probe, FirstSeen));
                    double residualSum = 0;
                    double scriptSum = 0;
                    for (int i = 0; i < Spikes.Length; i++)
                    {
                        residualSum += Residual(Spikes[i]);
                        scriptSum += Spikes[i].ScriptMs;
                    }
                    ScriptProbe.Write(Scripts, residualSum, scriptSum);
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
        }

        private static void RunWriteWorker(object state) => ((WriteJob)state).Run();

        private static void Write(bool sync)
        {
            if (!_active || _playStart == 0 || (!sync && Spikes.Count == _flushedSpikes)) return;
            _flushedSpikes = Spikes.Count;
            var segs = new SegStats[Segments.Count];
            int si = 0;
            foreach (SegStats seg in Segments.Values) segs[si++] = seg.Copy();
            var markerValid = new bool[_markers.Length];
            for (int i = 0; i < _markers.Length; i++) markerValid[i] = _markers[i].Valid;
            double meanUs = _probeFrames > 0 ? _probeTicks * MsPerTick * 1000.0 / _probeFrames : 0;
            var job = new WriteJob
            {
                Spikes = Spikes.ToArray(),
                Segs = segs,
                SessionAcc = (long[])_sessionAcc.Clone(),
                Calls = (long[])SessionCalls.Clone(),
                Self = (long[])SessionSelf.Clone(),
                CounterSeen = (bool[])CounterSeen.Clone(),
                MarkerValid = markerValid,
                MarkerSeen = (bool[])_markerSeen.Clone(),
                Probe = $"probe overhead: mean {meanUs.ToString("F1", CultureInfo.InvariantCulture)} us/frame, max {(_probeMaxTicks * MsPerTick).ToString("F2", CultureInfo.InvariantCulture)} ms over {_probeFrames.ToString(CultureInfo.InvariantCulture)} play frames (frame-end bookkeeping + first-seen scans)",
                Scripts = ScriptProbe.Snapshot(),
                FirstSeen = $"first seen this session: {SeenNetPrefabs.Count.ToString(CultureInfo.InvariantCulture)} net prefabs, {SeenNamed.Count.ToString(CultureInfo.InvariantCulture)} locations/rooms, {_sessionNewShaders.ToString(CultureInfo.InvariantCulture)} shaders, {_sessionNewVariants.ToString(CultureInfo.InvariantCulture)} shader/keyword combinations",
            };
            if (!sync)
            {
                ThreadPool.QueueUserWorkItem(RunWriteWorker, job);
                return;
            }
            while (Volatile.Read(ref _writing) != 0) Thread.Sleep(10);
            job.Run();
        }

        internal static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

        private static string Tsv(Spike[] spikes)
        {
            var sb = new StringBuilder();
            sb.Append("t\tutc\tms\tcause\tsegment\tteleporting\tinterior\tzoneChanged\tspeed\tmobs\tcombat\tpos\tzone\tgc0\tgc1\tgc2");
            foreach (string c in CounterNames) sb.Append('\t').Append(c.Replace(' ', '_'));
            foreach (string n in SignalNames) sb.Append('\t').Append(n).Append("_n\t").Append(n).Append("_ms\t").Append(n).Append("_max");
            sb.Append("\tnewPrefabs\tnewShaders\tnewVariants\tprevNewVariants\tnewShaderNames\tscriptMs\trenderMs\tphysicsMs\tloadingMs\toutsideMs\tprobeMs\ttopSubsystems\tscriptTop\n");
            for (int si = 0; si < spikes.Length; si++)
            {
                Spike s = spikes[si];
                sb.Append(s.T.ToString("F2", CultureInfo.InvariantCulture)).Append('\t').Append(s.Utc).Append('\t').Append(F(s.Ms))
                  .Append('\t').Append(s.Cause).Append('\t').Append(s.Segment).Append('\t').Append(B(s.Teleporting))
                  .Append('\t').Append(B(s.Interior)).Append('\t').Append(B(s.ZoneChanged)).Append('\t').Append(F(s.Speed))
                  .Append('\t').Append(s.Mobs).Append('\t').Append(B(s.Combat)).Append('\t').Append(s.Pos).Append('\t').Append(s.Zone)
                  .Append('\t').Append(s.Gc0).Append('\t').Append(s.Gc1).Append('\t').Append(s.Gc2);
                for (int i = 0; i < s.Counters.Length; i++)
                {
                    sb.Append('\t');
                    if (i < MemoryCounters) sb.Append(s.Counters[i] / 1024).Append("KB");
                    else sb.Append(s.Counters[i]);
                }
                for (int i = 0; i < SCount; i++) sb.Append('\t').Append(s.Calls[i]).Append('\t').Append(F(s.SelfMs[i])).Append('\t').Append(s.MaxName[i] ?? "");
                sb.Append('\t').Append(s.NewPrefabs).Append('\t').Append(s.NewShaders).Append('\t').Append(s.NewVariants)
                  .Append('\t').Append(s.PrevNewVariants).Append('\t').Append(s.NewShaderNames).Append('\t').Append(F(s.ScriptMs))
                  .Append('\t').Append(F(s.RenderMs)).Append('\t').Append(F(s.PhysicsMs)).Append('\t').Append(F(s.LoadingMs))
                  .Append('\t').Append(F(s.OutsideMs)).Append('\t').Append(s.ProbeMs.ToString("F2", CultureInfo.InvariantCulture))
                  .Append('\t').Append(s.TopSubs).Append('\t').Append(s.ScriptTop ?? "").Append('\n');
            }
            return sb.ToString();
        }

        private static int B(bool v) => v ? 1 : 0;

        private static double Pct(int[] hist, long n, double q)
        {
            long want = (long)Math.Ceiling(n * q), seen = 0;
            for (int i = 0; i < hist.Length; i++)
            {
                seen += hist[i];
                if (seen >= want && want > 0) return (i + 1) * 0.5;
            }
            return 0;
        }

        private sealed class CauseAgg
        {
            public string Cause;
            public int Count, Telep, NewVar;
            public double TotalMs, ExcessMs;
            public float MaxMs;
        }

        private static string Summary(Spike[] spikes, SegStats[] segs, long[] sessionAcc, long[] calls, long[] self, bool[] counterSeen,
            bool[] markerValid, bool[] markerSeen, string probe, string firstSeen)
        {
            var sb = new StringBuilder();
            sb.Append("Morgott.Preloader spike probe - ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" - threshold ")
                .Append(F(_thresholdMs)).Append(" ms - logical cores Environment=").Append(Environment.ProcessorCount).Append(" SystemInfo=")
                .Append(SystemInfo.processorCount).Append(" JobWorkerCount=").Append(JobsUtility.JobWorkerCount).Append('/').Append(JobsUtility.JobWorkerMaximumCount).Append('\n').Append(probe).Append('\n').Append(firstSeen).Append("\n\n");
            sb.Append("== frame time per segment (play frames, p = upper bound of a 0.5 ms bucket)\n");
            sb.Append(PadR("segment", 18)).Append(' ').Append(PadL("frames", 7)).Append(' ').Append(PadL("avg", 7)).Append(' ')
              .Append(PadL("p50", 7)).Append(' ').Append(PadL("p99", 7)).Append(' ').Append(PadL("p99.9", 7)).Append(' ')
              .Append(PadL("max", 8)).Append(' ').Append(PadL("spikes", 6)).Append(' ').Append(PadL("spikeMs", 9)).Append('\n');
            var all = new SegStats { Name = "ALL" };
            for (int si = 0; si < segs.Length; si++)
            {
                SegStats s = segs[si];
                all.Frames += s.Frames;
                all.TotalMs += s.TotalMs;
                all.MaxMs = Math.Max(all.MaxMs, s.MaxMs);
                all.Spikes += s.Spikes;
                all.SpikeMs += s.SpikeMs;
                for (int i = 0; i < HistBuckets; i++) all.Hist[i] += s.Hist[i];
                AppendSegRow(sb, s);
            }
            AppendSegRow(sb, all);

            double p50 = Pct(all.Hist, all.Frames, 0.5);
            sb.Append("\n== causes ranked by total spike ms (cause = largest of: hook self times, script residual, render, physics, loading, outside, other)\n");
            sb.Append(PadR("cause", 20)).Append(' ').Append(PadL("count", 6)).Append(' ').Append(PadL("totalMs", 9)).Append(' ')
              .Append(PadL("excessMs", 9)).Append(' ').Append(PadL("maxMs", 8)).Append(' ').Append(PadL("telep", 6)).Append(' ')
              .Append(PadL("newVar", 6)).Append("  owner\n");
            var causeMap = new Dictionary<string, CauseAgg>();
            var segCauseMap = new Dictionary<string, Dictionary<string, CauseAgg>>();
            var segOrder = new List<string>();
            var signalSpikeCalls = new long[SCount];
            var signalSpikeMs = new double[SCount];
            int withNewCount = 0;
            double withNewMs = 0;
            double totalSpikeMs = 0;
            for (int si = 0; si < spikes.Length; si++)
            {
                Spike s = spikes[si];
                totalSpikeMs += s.Ms;
                if (s.NewVariantsNear)
                {
                    withNewCount++;
                    withNewMs += s.Ms;
                }
                for (int i = 0; i < SCount; i++)
                {
                    signalSpikeCalls[i] += s.Calls[i];
                    signalSpikeMs[i] += s.SelfMs[i];
                }
                if (!causeMap.TryGetValue(s.Cause, out CauseAgg ca))
                    causeMap[s.Cause] = ca = new CauseAgg { Cause = s.Cause };
                ca.Count++;
                ca.TotalMs += s.Ms;
                ca.ExcessMs += Math.Max(0, s.Ms - p50);
                if (s.Ms > ca.MaxMs) ca.MaxMs = s.Ms;
                if (s.Teleporting) ca.Telep++;
                if (s.NewVariants + s.PrevNewVariants > 0) ca.NewVar++;

                if (!segCauseMap.TryGetValue(s.Segment, out Dictionary<string, CauseAgg> scMap))
                {
                    segCauseMap[s.Segment] = scMap = new Dictionary<string, CauseAgg>();
                    segOrder.Add(s.Segment);
                }
                if (!scMap.TryGetValue(s.Cause, out CauseAgg sca))
                    scMap[s.Cause] = sca = new CauseAgg { Cause = s.Cause };
                sca.Count++;
                sca.TotalMs += s.Ms;
            }
            var causeList = new List<CauseAgg>(causeMap.Values);
            causeList.Sort(CompareCauseMsDesc);
            for (int i = 0; i < causeList.Count; i++)
            {
                CauseAgg g = causeList[i];
                Owners.TryGetValue(g.Cause, out string owner);
                sb.Append(PadR(g.Cause, 20)).Append(' ').Append(PadN(g.Count, 6)).Append(' ').Append(PadF(g.TotalMs, 9, "F0")).Append(' ')
                  .Append(PadF(g.ExcessMs, 9, "F0")).Append(' ').Append(PadF(g.MaxMs, 8, "F1")).Append(' ')
                  .Append(PadN(g.Telep, 6)).Append(' ').Append(PadN(g.NewVar, 6)).Append("  ").Append(owner ?? "").Append('\n');
            }
            sb.Append("\n== causes per segment\n");
            for (int i = 0; i < segOrder.Count; i++)
            {
                string segName = segOrder[i];
                var list = new List<CauseAgg>(segCauseMap[segName].Values);
                list.Sort(CompareCauseMsDesc);
                sb.Append(segName).Append(": ");
                for (int j = 0; j < list.Count; j++)
                {
                    if (j > 0) sb.Append(", ");
                    sb.Append(list[j].Cause).Append(' ').Append(list[j].Count).Append("x/").Append(F(list[j].TotalMs)).Append("ms");
                }
                sb.Append('\n');
            }
            sb.Append("\n== hook signals over all spike frames (self ms) and over the whole session\n");
            for (int i = 0; i < SCount; i++)
            {
                sb.Append(PadR(SignalNames[i], 18)).Append(" spikes: ").Append(PadN(signalSpikeCalls[i], 6)).Append(" calls ")
                  .Append(PadF(signalSpikeMs[i], 9, "F1")).Append(" ms   session: ").Append(PadN(calls[i], 8)).Append(" calls ")
                  .Append(PadF(self[i] * MsPerTick, 9, "F1")).Append(" ms\n");
            }
            sb.Append("\n== first-seen shader correlation: ").Append(withNewCount).Append(" of ").Append(spikes.Length)
              .Append(" spike frames had first-seen shader variants in the frame or the one before (")
              .Append(withNewMs.ToString("F0", CultureInfo.InvariantCulture)).Append(" of ")
              .Append(totalSpikeMs.ToString("F0", CultureInfo.InvariantCulture)).Append(" ms)\n");
            sb.Append("\n== session time per player-loop subsystem (top 25, all frames incl. menu/loading)\n");
            var subOrder = new (int Idx, long Ticks)[sessionAcc.Length];
            for (int i = 0; i < sessionAcc.Length; i++) subOrder[i] = (i, sessionAcc[i]);
            Array.Sort(subOrder, CompareSubTicksDesc);
            int subTake = Math.Min(25, subOrder.Length);
            for (int i = 0; i < subTake; i++)
            {
                sb.Append(PadR(_subNames[subOrder[i].Idx], 60)).Append(' ').Append(PadF(subOrder[i].Ticks * MsPerTick, 10, "F0")).Append(" ms\n");
            }
            sb.Append("\n== top 40 spike frames\n");
            var topSpikes = (Spike[])spikes.Clone();
            Array.Sort(topSpikes, CompareSpikeMsDesc);
            int topSpikeTake = Math.Min(40, topSpikes.Length);
            for (int si = 0; si < topSpikeTake; si++)
            {
                Spike s = topSpikes[si];
                sb.Append(PadF(s.Ms, 8, "F1")).Append(" ms t=").Append(PadF(s.T, 7, "F1")).Append("s ").Append(s.Cause)
                  .Append(" [").Append(s.Segment).Append("] ")
                  .Append(s.Teleporting ? "TP " : "").Append(s.Interior ? "IN " : "").Append(s.ZoneChanged ? "ZONE " : "")
                  .Append("spd ").Append(F(s.Speed)).Append(" mobs ").Append(s.Mobs).Append(" gc ")
                  .Append(s.Gc0).Append('/').Append(s.Gc1).Append('/').Append(s.Gc2).Append(" | ");
                for (int i = 0; i < SCount; i++)
                {
                    if (s.Calls[i] == 0 || s.SelfMs[i] < 0.5f) continue;
                    sb.Append(SignalNames[i]).Append(' ').Append(s.Calls[i]).Append("x ").Append(F(s.SelfMs[i])).Append("ms");
                    if (s.MaxName[i] != null) sb.Append(" (max ").Append(s.MaxName[i]).Append(')');
                    sb.Append("; ");
                }
                sb.Append("new ").Append(s.NewPrefabs).Append(" prefabs/").Append(s.NewShaders).Append(" shaders/").Append(s.NewVariants).Append('+').Append(s.PrevNewVariants)
                    .Append(" var | script ").Append(F(s.ScriptMs)).Append(" render ").Append(F(s.RenderMs)).Append(" phys ").Append(F(s.PhysicsMs)).Append(" | ")
                    .Append(s.TopSubs);
                if (s.NewShaderNames.Length > 0) sb.Append(" | shaders ").Append(s.NewShaderNames);
                sb.Append(" | setpass ").Append(s.Counters[4]).Append(" draws ").Append(s.Counters[5]).Append(" vbKB ").Append(s.Counters[7] / 1024)
                    .Append(" gcUsedKB ").Append(s.Counters[0] / 1024).Append(" totalUsedKB ").Append(s.Counters[2] / 1024).Append('\n');
            }
            sb.Append("\n== ProfilerRecorder in this player (valid = handle resolved, nonzero = ever reported a value; profiler-available.txt lists every metric)\n");
            for (int i = 0; i < CounterNames.Length; i++)
                sb.Append("counter ").Append(CounterNames[i]).Append(": valid=").Append(i < _counters.Length && _counters[i].Valid).Append(" nonzero=").Append(counterSeen[i]).Append('\n');
            for (int i = 0; i < MarkerNames.Length && i < markerValid.Length; i++)
                sb.Append("marker ").Append(MarkerNames[i]).Append(": valid=").Append(markerValid[i]).Append(" nonzero=").Append(markerSeen[i]).Append('\n');
            sb.Append("metrics available=").Append(_availableMetrics).Append(" frameTimingStats=").Append(_frameTimingEnabled).Append('\n');
            return sb.ToString();
        }

        private static void AppendSegRow(StringBuilder sb, SegStats s)
        {
            sb.Append(PadR(s.Name, 18)).Append(' ').Append(PadN(s.Frames, 7)).Append(' ')
              .Append(PadF(s.Frames > 0 ? s.TotalMs / s.Frames : 0, 7, "F1")).Append(' ')
              .Append(PadF(Pct(s.Hist, s.Frames, 0.5), 7, "F1")).Append(' ')
              .Append(PadF(Pct(s.Hist, s.Frames, 0.99), 7, "F1")).Append(' ')
              .Append(PadF(Pct(s.Hist, s.Frames, 0.999), 7, "F1")).Append(' ')
              .Append(PadF(s.MaxMs, 8, "F1")).Append(' ').Append(PadN(s.Spikes, 6)).Append(' ')
              .Append(PadF(s.SpikeMs, 9, "F0")).Append('\n');
        }

        private static int CompareCauseMsDesc(CauseAgg a, CauseAgg b) => b.TotalMs.CompareTo(a.TotalMs);

        private static int CompareSpikeMsDesc(Spike a, Spike b) => b.Ms.CompareTo(a.Ms);

        private static string PadF(double v, int width, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture).PadLeft(width);

        private static string PadN(long v, int width) => v.ToString(CultureInfo.InvariantCulture).PadLeft(width);

        private static string PadL(string s, int width) => (s ?? "").PadLeft(width);

        private static string PadR(string s, int width) => (s ?? "").PadRight(width);
    }
}
