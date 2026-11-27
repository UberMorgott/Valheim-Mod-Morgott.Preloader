using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Bootstrap;
using Morgott.Preloader.Core;
using HarmonyLib;
using UnityEngine;

namespace Morgott.Preloader.Profiling
{
    /// <summary>
    /// Opt-in with <c>[Profiler] ScriptBreakdown</c> (needs SpikeProbe): splits SpikeProbe's "script residual" (Update /
    /// FixedUpdate / LateUpdate time no SpikeProbe hook covers) per method and owner. Approach of Skidbladnir's lab
    /// StressProf (tools\Lab\StressProbe.cs): a First prefix + Last finalizer stopwatch pair around
    /// <list type="bullet">
    /// <item>every prefix / postfix / finalizer another Harmony ID put on any method (owner = the plugin whose assembly holds
    /// the patch method; transpiled code stays inside the patched game method's own time),</item>
    /// <item>plugin MonoBehaviour Update / FixedUpdate / LateUpdate / OnGUI, plugin coroutine bodies (MoveNext) and
    /// instance Tick methods (per-module ticks driven from one Update, Skidbladnir Registry),</item>
    /// <item>the game's own MonoBehaviour Update / FixedUpdate / LateUpdate and the MonoUpdaters-driven CustomUpdate /
    /// CustomFixedUpdate / CustomLateUpdate / UpdateAI (owner "vanilla &lt;Type&gt;").</item>
    /// </list>
    /// Self time = time minus wrapped children and minus SpikeProbe hook calls inside it (those are SpikeProbe's own
    /// signals); a child is subtracted only from the wrapped call it runs directly inside (a wrapped call inside a hook
    /// reaches the outer caller as part of that hook's total), so the "outside hooks" sum is comparable with the residual.
    /// Self time is checked never to go negative ([Profiler] ScriptProbe self-check line). Time a wrapped method spends inside a
    /// SpikeProbe hook (e.g. a mod's ZNetView.Awake postfix inside ZNetScene.CreateObject) is kept apart as "inHook".
    /// Installed once at the main menu (after SpikeProbe's player loop), main thread only. Safety as
    /// <see cref="PatchOwnerProbe"/>: MonoMod's reflection cache emptied around each wrap; the method being wrapped is
    /// written to script-probe.pending first and a launch that died there skips it from then on (script-probe.skip).
    /// Overhead: calibrated per wrapped call at install and reported per frame. Output BepInEx\Morgott.Preloader\script-breakdown.txt,
    /// written with SpikeProbe's files; spike rows get the top methods of the frame (spikes.tsv column scriptTop).
    /// The autotest harness (AutotestDriver.PerfAudit.cs) reflects On, Recs (Rec.Owner/Name/PlaySelf/PlayHook/PlayCalls/
    /// SpikeSelf/SpikeHook), _playFrames, _spikeFrames and _costTicksPerCall: keep those names.
    /// </summary>
    internal static class ScriptProbe
    {
        public const string HarmonyId = "morgott.valheim.preloader.scriptprobe";

        private sealed class Rec
        {
            public string Owner, Name, Kind;
            public long FrameSelf, FrameHook;
            public int FrameCalls;
            public bool Touched;
            public long SpikeSelf, SpikeHook, SpikeCalls, SpikeFrames, SpikeTop;
            public long PlaySelf, PlayHook, PlayCalls, PlayMax;

            public Rec Copy() => (Rec)MemberwiseClone();

            public void ResetFrame()
            {
                FrameSelf = FrameHook = 0;
                FrameCalls = 0;
                Touched = false;
            }
        }

        private sealed class OwnerAgg
        {
            public string Owner;
            public long SpikeSelf, SpikeHook, PlayTotal;
            public int Count;
        }

        private const int SpawnFrames = 5;

        // Assemblies skipped the way StressProf skips them (native crashes while detouring in the lab; their time stays
        // in the wrapped game callers). SkidbladnirLab = the lab harness plugin: timing the harness itself is meaningless.
        private static readonly HashSet<string> SkipAsm = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Warfare", "Wizardry", "Armory", "Morgott.Preloader", "0Harmony", "BepInEx", "BepInEx.Preloader", "MonoMod.Utils", "MonoMod.RuntimeDetour", "SkidbladnirLab" };

        private static readonly HashSet<string> PluginMsgs = new HashSet<string> { "Update", "FixedUpdate", "LateUpdate", "OnGUI" };
        private static readonly HashSet<string> VanillaMsgs = new HashSet<string>
            { "Update", "FixedUpdate", "LateUpdate", "CustomUpdate", "CustomFixedUpdate", "CustomLateUpdate", "UpdateAI" };

        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly Dictionary<MethodBase, int> Idx = new Dictionary<MethodBase, int>();
        private static readonly List<Rec> Recs = new List<Rec>();
        private static readonly List<int> Touched = new List<int>(1024);
        private static readonly long[] StackStart = new long[256];
        private static readonly long[] StackChild = new long[256];
        private static readonly int[] StackHookDepth = new int[256];
        private static int _depth;
        private static int _mainThread;
        private static bool _on;
        private static int _frameCalls;

        private static int _wrappedPatches, _wrappedPlugin, _wrappedVanilla, _failed, _skipped, _unpatchable;
        private static int _calibSink;
        private static double _installSec;
        private static double _costTicksPerCall;
        private static long _playFrames, _playCalls, _spikeFrameCalls, _spikeFrames;
        private static double _spikeFrameMs;
        private static readonly List<string> SpawnLines = new List<string>();

        private const int MaxOffenders = 5;
        private static readonly long SelfEpsilonTicks = Math.Max(1, Stopwatch.Frequency / 1000000);
        private static readonly List<string> Offenders = new List<string>();
        private static long _violations;
        private static long _loggedViolations = -1;

        internal static bool On => _on;

        /// <summary>A SpikeProbe hook call that just ended inside a wrapped call: its time is not the caller's self time.</summary>
        /// <param name="hookDepth">SpikeProbe's hook depth after that call ended: only a hook directly inside the innermost wrapped
        /// call counts (its nested hooks are already inside its total).</param>
        internal static void AddChild(long ticks, int hookDepth)
        {
            if (_on && _depth > 0 && StackHookDepth[_depth - 1] == hookDepth) StackChild[_depth - 1] += ticks;
        }

        private static string OwnerOf(MethodBase m, Dictionary<Assembly, string> owners)
        {
            Assembly a = m.DeclaringType?.Assembly;
            return a != null && owners.TryGetValue(a, out string n) ? n : a?.GetName().Name ?? "?";
        }

        private static void AddTarget(
            List<(MethodInfo m, string owner, string name, string kind)> targets,
            HashSet<MethodBase> seen,
            MethodInfo m,
            string owner,
            string name,
            string kind)
        {
            if (m == null || m.IsAbstract || m.ContainsGenericParameters || m.DeclaringType == null || m.DeclaringType.ContainsGenericParameters) return;
            if (SkipAsm.Contains(m.DeclaringType.Assembly.GetName().Name) || !seen.Add(m)) return;
            MethodBody body = SafeMethodBody(m);
            if (body == null) return;
            if (SafeEndsInHandler(body)) _unpatchable++;
            else targets.Add((m, owner, name, kind));
        }

        private static void AddPatchGroup(
            IReadOnlyList<Patch> patchList,
            MethodBase target,
            Dictionary<Assembly, string> owners,
            List<(MethodInfo m, string owner, string name, string kind)> targets,
            HashSet<MethodBase> seen)
        {
            for (int i = 0; i < patchList.Count; i++)
            {
                Patch p = patchList[i];
                if (p.PatchMethod == null || p.owner.StartsWith(PatchGuard.OwnPrefix, StringComparison.Ordinal)) continue;
                AddTarget(targets, seen, p.PatchMethod, OwnerOf(p.PatchMethod, owners),
                    p.PatchMethod.DeclaringType?.Name + "." + p.PatchMethod.Name + " -> " + target.DeclaringType?.Name + "." + target.Name, "patch");
            }
        }

        /// <summary>Main menu, after SpikeProbe's player loop: every plugin has loaded and patched.</summary>
        internal static void Install()
        {
            var sw = Stopwatch.StartNew();
            _mainThread = Environment.CurrentManagedThreadId;
            HashSet<string> skip = PatchOwnerProbe.CrashGuard("script-probe", "Script probe", out string pendingPath);
            if (PatchOwnerProbe.ResolveCache == null)
            {
                Log.Warning("Script probe: MonoMod.Utils has no ReflectionHelper.ResolveReflectionCache (other version), off");
                return;
            }

            // Plugin assembly -> plugin name by file location (PluginInfo.Instance is typed in the UnityEngine facade this
            // patcher does not reference).
            var byPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (PluginInfo pi in Chainloader.PluginInfos.Values)
            {
                if (!string.IsNullOrEmpty(pi.Location) && !byPath.ContainsKey(Path.GetFullPath(pi.Location))) byPath[Path.GetFullPath(pi.Location)] = pi.Metadata.Name;
            }
            var owners = new Dictionary<Assembly, string>();
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string loc = SafeLocation(a);
                if (!string.IsNullOrEmpty(loc) && byPath.TryGetValue(Path.GetFullPath(loc), out string name)) owners[a] = name;
            }

            var targets = new List<(MethodInfo m, string owner, string name, string kind)>();
            var seen = new HashSet<MethodBase>();

            // A: foreign patch methods on anything (snapshot: wrapping them adds our own patches)
            var patched = new List<MethodBase>(Harmony.GetAllPatchedMethods());
            for (int i = 0; i < patched.Count; i++)
            {
                MethodBase target = patched[i];
                Patches info = Harmony.GetPatchInfo(target);
                if (info == null) continue;
                AddPatchGroup(info.Prefixes, target, owners, targets, seen);
                AddPatchGroup(info.Postfixes, target, owners, targets, seen);
                AddPatchGroup(info.Finalizers, target, owners, targets, seen);
            }
            int patches = targets.Count;

            // B: plugin assemblies (Unity messages, coroutine bodies, module ticks)
            foreach (Assembly asm in owners.Keys)
            {
                List<Type> types = SafeTypes(asm);
                string owner = owners[asm];
                for (int ti = 0; ti < types.Count; ti++)
                {
                    Type t = types[ti];
                    bool mb = Is(typeof(MonoBehaviour), t), it = Is(typeof(IEnumerator), t);
                    MethodInfo[] methods = Methods(t);
                    for (int mi = 0; mi < methods.Length; mi++)
                    {
                        MethodInfo m = methods[mi];
                        string kind = mb && PluginMsgs.Contains(m.Name) ? "msg" : it && m.Name == "MoveNext" ? "coroutine" : !m.IsStatic && m.Name == "Tick" && m.GetParameters().Length <= 1 ? "tick" : null;
                        if (kind != null) AddTarget(targets, seen, m, owner, ShortName(t) + "." + m.Name, kind);
                    }
                }
            }
            int plugin = targets.Count - patches;

            // C: the game's per-frame messages
            List<Type> gameTypes = SafeTypes(typeof(ZNetScene).Assembly);
            for (int ti = 0; ti < gameTypes.Count; ti++)
            {
                Type t = gameTypes[ti];
                if (!Is(typeof(MonoBehaviour), t)) continue;
                MethodInfo[] methods = Methods(t);
                for (int mi = 0; mi < methods.Length; mi++)
                {
                    MethodInfo m = methods[mi];
                    if (VanillaMsgs.Contains(m.Name)) AddTarget(targets, seen, m, "vanilla", t.Name + "." + m.Name, "vanilla");
                }
            }
            int vanilla = targets.Count - patches - plugin;

            var harmony = new Harmony(HarmonyId);
            var pre = new HarmonyMethod(AccessTools.Method(typeof(ScriptProbe), nameof(Pre)), Priority.First);
            var fin = new HarmonyMethod(AccessTools.Method(typeof(ScriptProbe), nameof(Fin)), Priority.Last);
            for (int i = 0; i < targets.Count; i++)
            {
                (MethodInfo m, string owner, string name, string kind) = targets[i];
                string id = PatchOwnerProbe.Identity(m);
                if (skip.Contains(id))
                {
                    _skipped++;
                    continue;
                }
                try
                {
                    Idx[m] = Recs.Count;
                    Recs.Add(new Rec { Owner = owner, Name = name, Kind = kind });
                    PatchOwnerProbe.GuardedPatch(harmony, m, pendingPath, id, pre, fin);
                    if (kind == "patch") _wrappedPatches++;
                    else if (kind == "vanilla") _wrappedVanilla++;
                    else _wrappedPlugin++;
                }
                catch (Exception e)
                {
                    Idx.Remove(m);
                    if (++_failed <= 20) Log.Warning($"Script probe: cannot wrap {owner} {name}: {e.GetType().Name} {e.Message}");
                }
            }
            File.Delete(pendingPath);
            Calibrate(harmony, pre, fin);
            _installSec = sw.Elapsed.TotalSeconds;
            _on = true;
            Log.Info($"Script probe: {targets.Count.ToString(Inv)} targets ({patches.ToString(Inv)} mod patch methods, {plugin.ToString(Inv)} plugin messages/coroutines/ticks, {vanilla.ToString(Inv)} vanilla messages); " +
                     $"wrapped {_wrappedPatches.ToString(Inv)}+{_wrappedPlugin.ToString(Inv)}+{_wrappedVanilla.ToString(Inv)}, failed {_failed.ToString(Inv)}, skipped {_skipped.ToString(Inv)}, " +
                     $"{_unpatchable.ToString(Inv)} left out (IL ends in a handler, see DetourSafety); {_installSec.ToString("F1", Inv)} s; {(_costTicksPerCall * MsPerTick * 1e6).ToString("F0", Inv)} ns per wrapped call");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Dummy(int x) => x + 1;

        /// <summary>Wrapper cost per call: an empty method timed plain, then wrapped like every target.</summary>
        private static void Calibrate(Harmony harmony, HarmonyMethod pre, HarmonyMethod fin)
        {
            const int n = 200000;
            try
            {
                MethodInfo d = AccessTools.Method(typeof(ScriptProbe), nameof(Dummy));
                int x = 0;
                long plain = RunCalibrationLoop(n, ref x);
                Idx[d] = Recs.Count;
                Recs.Add(new Rec { Owner = "(calibration)", Name = "Dummy", Kind = "calibration" });
                harmony.Patch(d, prefix: pre, finalizer: fin);
                _on = true;
                long wrapped = RunCalibrationLoop(n, ref x);
                _on = false;
                harmony.Unpatch(d, HarmonyPatchType.All, HarmonyId);
                Recs[Idx[d]].ResetFrame();
                Touched.Clear();
                _depth = 0;
                _calibSink = x;
                _costTicksPerCall = Math.Max(0, wrapped - plain) / (double)n;
            }
            catch (Exception e)
            {
                Log.Warning("Script probe: calibration failed: " + e.Message);
            }
        }

        private static long RunCalibrationLoop(int n, ref int x)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < n; i++) x = Dummy(x);
            return Stopwatch.GetTimestamp() - t0;
        }

        private static void Pre(out long __state)
        {
            __state = 0;
            if (!_on || _depth >= StackStart.Length || Environment.CurrentManagedThreadId != _mainThread) return;
            __state = Stopwatch.GetTimestamp();
            StackStart[_depth] = __state;
            StackChild[_depth] = 0;
            StackHookDepth[_depth] = SpikeProbe.HookDepth;
            _depth++;
        }

        private static void Fin(long __state, MethodBase __originalMethod)
        {
            if (__state == 0 || _depth == 0) return;
            long total = Stopwatch.GetTimestamp() - __state;
            _depth--;
            long self = total - StackChild[_depth];
            // Only a call directly inside the parent counts as its child. Inside a SpikeProbe hook the hook's whole total
            // reaches the parent through AddChild when the hook ends, so passing this call's total up as well would
            // subtract it twice (ZoneSystem.Update went negative by every mod patch running inside SpawnZone /
            // Heightmap hooks).
            if (_depth > 0 && StackHookDepth[_depth - 1] == SpikeProbe.HookDepth) StackChild[_depth - 1] += total;
            if (!Idx.TryGetValue(__originalMethod, out int i)) return;
            Rec r = Recs[i];
            if (self < 0) self = SelfCheck(r, self);
            r.FrameCalls++;
            _frameCalls++;
            if (SpikeProbe.InHook) r.FrameHook += self;
            else r.FrameSelf += self;
            if (!r.Touched)
            {
                r.Touched = true;
                Touched.Add(i);
            }
        }

        /// <summary>
        /// Self-check: children are nested inside their parent, so self time can never be negative. A negative value is an
        /// accounting bug: it is counted (beyond 1 us) and reported, never emitted; 0 is the guarded fallback.
        /// </summary>
        private static long SelfCheck(Rec r, long self)
        {
            if (-self <= SelfEpsilonTicks) return 0;
            _violations++;
            if (Offenders.Count < MaxOffenders) Offenders.Add($"{r.Owner}|{r.Name} {(self * MsPerTick).ToString("F3", Inv)} ms");
            return 0;
        }

        /// <summary>SpikeProbe frame end (main thread). Returns the frame's top methods for a spike row, else null.</summary>
        internal static string EndFrame(bool play, bool spike, double ms)
        {
            if (!_on) return null;
            string top = null;
            if (play)
            {
                _playFrames++;
                _playCalls += _frameCalls;
                if (spike)
                {
                    _spikeFrames++;
                    _spikeFrameCalls += _frameCalls;
                    _spikeFrameMs += ms;
                }
                int best = -1;
                long bestV = 0, covered = 0;
                for (int ti = 0; ti < Touched.Count; ti++)
                {
                    int i = Touched[ti];
                    Rec r = Recs[i];
                    covered += r.FrameSelf;
                    r.PlaySelf += r.FrameSelf;
                    r.PlayHook += r.FrameHook;
                    r.PlayCalls += r.FrameCalls;
                    r.PlayMax = Math.Max(r.PlayMax, r.FrameSelf + r.FrameHook);
                    if (!spike) continue;
                    r.SpikeSelf += r.FrameSelf;
                    r.SpikeHook += r.FrameHook;
                    r.SpikeCalls += r.FrameCalls;
                    r.SpikeFrames++;
                    if (r.FrameSelf > bestV) (bestV, best) = (r.FrameSelf, i);
                }
                if (best >= 0) Recs[best].SpikeTop++;
                bool spawn = _playFrames <= SpawnFrames;
                if (spike || spawn)
                {
                    double minMs = spawn ? 0.2 : 0.5;
                    var hits = new List<Rec>();
                    for (int ti = 0; ti < Touched.Count; ti++)
                    {
                        Rec r = Recs[Touched[ti]];
                        if ((r.FrameSelf + r.FrameHook) * MsPerTick >= minMs) hits.Add(r);
                    }
                    hits.Sort(CompareFrameTotalDesc);
                    int take = Math.Min(spawn ? 15 : 4, hits.Count);
                    var topSb = new StringBuilder();
                    for (int hi = 0; hi < take; hi++)
                    {
                        if (hi > 0) topSb.Append("; ");
                        Rec r = hits[hi];
                        topSb.Append(r.Owner).Append('|').Append(r.Name).Append(' ').Append(SpikeProbe.F(r.FrameSelf * MsPerTick));
                        if (r.FrameHook * MsPerTick >= 0.1) topSb.Append('+').Append(SpikeProbe.F(r.FrameHook * MsPerTick)).Append('h');
                        if (r.FrameCalls > 1) topSb.Append(" x").Append(r.FrameCalls.ToString(Inv));
                    }
                    top = topSb.ToString();
                    if (spawn)
                    {
                        SpawnLines.Add($"play frame {_playFrames.ToString(Inv)}: {ms.ToString("F1", Inv)} ms, wrapped self {(covered * MsPerTick).ToString("F1", Inv)} ms, {_frameCalls.ToString(Inv)} wrapped calls | {top}");
                    }
                }
            }
            for (int ti = 0; ti < Touched.Count; ti++) Recs[Touched[ti]].ResetFrame();
            Touched.Clear();
            _frameCalls = 0;
            _depth = 0; // a frame boundary is never inside a measured call (the player loop runs it first)
            return top;
        }

        private static int CompareFrameTotalDesc(Rec a, Rec b) => (b.FrameSelf + b.FrameHook).CompareTo(a.FrameSelf + a.FrameHook);

        private static int CompareSpikeSelfDesc(Rec a, Rec b) => b.SpikeSelf.CompareTo(a.SpikeSelf);

        private static int ComparePlayTotalDesc(Rec a, Rec b) => (b.PlaySelf + b.PlayHook).CompareTo(a.PlaySelf + a.PlayHook);

        private static int ComparePlayMaxDesc(Rec a, Rec b) => b.PlayMax.CompareTo(a.PlayMax);

        private sealed class SnapshotData
        {
            public Rec[] Rows;
            public string[] Spawn;
            public long PlayFrames, PlayCalls, SpikeFrames, SpikeCalls, Violations;
            public double SpikeMs;
            public string[] Offenders;
        }

        /// <summary>Main-thread copy of the counters for a worker-thread write.</summary>
        internal static object Snapshot()
        {
            if (!_on) return null;
            var copied = new List<Rec>(Recs.Count);
            for (int i = 0; i < Recs.Count; i++)
            {
                if (Recs[i].PlayCalls > 0) copied.Add(Recs[i].Copy());
            }
            return new SnapshotData
            {
                Rows = copied.ToArray(),
                Spawn = SpawnLines.ToArray(),
                PlayFrames = _playFrames,
                PlayCalls = _playCalls,
                SpikeFrames = _spikeFrames,
                SpikeCalls = _spikeFrameCalls,
                SpikeMs = _spikeFrameMs,
                Violations = _violations,
                Offenders = Offenders.ToArray(),
            };
        }

        /// <summary>Worker thread: script-breakdown.txt from a <see cref="Snapshot"/>; residualMs = SpikeProbe's script residual over the spike frames.</summary>
        internal static void Write(object snapshot, double residualMs, double scriptMs)
        {
            if (!(snapshot is SnapshotData data)) return;
            Rec[] rows = data.Rows;
            string[] spawn = data.Spawn;
            long playFrames = data.PlayFrames;
            long playCalls = data.PlayCalls;
            long spikeFrames = data.SpikeFrames;
            long spikeCalls = data.SpikeCalls;
            double spikeMs = data.SpikeMs;
            long violations = data.Violations;
            string[] offenders = data.Offenders;
            string selfCheck = "[Profiler] ScriptProbe self-check: " + violations.ToString(Inv) + " violation(s)" +
                               (offenders.Length > 0 ? " (negative self time; first: " + string.Join("; ", offenders) + ")" : "");
            if (Interlocked.Exchange(ref _loggedViolations, violations) != violations)
            {
                if (violations == 0) Log.Info(selfCheck);
                else Log.Warning(selfCheck);
            }
            double nsPerCall = _costTicksPerCall * MsPerTick * 1e6;
            long coveredTicks = 0;
            for (int i = 0; i < rows.Length; i++) coveredTicks += rows[i].SpikeSelf;
            double coveredMs = coveredTicks * MsPerTick;
            var sb = new StringBuilder();
            sb.Append("Morgott.Preloader script breakdown - ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv)).Append('\n');
            sb.Append("wrapped ").Append(_wrappedPatches).Append(" mod patch methods, ").Append(_wrappedPlugin)
              .Append(" plugin messages/coroutines/ticks, ").Append(_wrappedVanilla).Append(" vanilla messages; failed ")
              .Append(_failed).Append(", skipped ").Append(_skipped).Append("; install ").Append(_installSec.ToString("F1", Inv))
              .Append(" s at the main menu\n");
            double playCallsPerFrame = playFrames > 0 ? playCalls / (double)playFrames : 0;
            double playUsPerFrame = playFrames > 0 ? playCalls * nsPerCall / 1000.0 / playFrames : 0;
            double spikeCallsPerFrame = spikeFrames > 0 ? spikeCalls / (double)spikeFrames : 0;
            double spikeMsPerFrame = spikeFrames > 0 ? spikeCalls * nsPerCall / 1e6 / spikeFrames : 0;
            sb.Append("overhead: ").Append(nsPerCall.ToString("F0", Inv)).Append(" ns per wrapped call (calibrated); play frames ")
              .Append(playFrames).Append(", ").Append(playCallsPerFrame.ToString("F0", Inv)).Append(" wrapped calls/frame -> ~")
              .Append(playUsPerFrame.ToString("F0", Inv)).Append(" us/frame; spike frames ").Append(spikeFrames).Append(", ")
              .Append(spikeCallsPerFrame.ToString("F0", Inv)).Append(" calls/frame -> ~").Append(spikeMsPerFrame.ToString("F2", Inv))
              .Append(" ms/spike frame (").Append((spikeCalls * nsPerCall / 1e6).ToString("F1", Inv)).Append(" of ")
              .Append(spikeMs.ToString("F0", Inv)).Append(" spike ms)\n");
            double pctResidual = residualMs > 0 ? 100 * coveredMs / residualMs : 0;
            sb.Append("spike frames: script subsystems ").Append(scriptMs.ToString("F0", Inv))
              .Append(" ms, residual (minus SpikeProbe hooks and probe) ").Append(residualMs.ToString("F0", Inv))
              .Append(" ms; wrapped self time outside hooks ").Append(coveredMs.ToString("F0", Inv)).Append(" ms (")
              .Append(pctResidual.ToString("F0", Inv)).Append("% of the residual); rest = unwrapped engine/script work and wrapper overhead\n");
            sb.Append(selfCheck).Append("\n\n");

            var sorted = (Rec[])rows.Clone();
            Array.Sort(sorted, CompareSpikeSelfDesc);
            AppendTable(sb, "top 40 methods by self ms in spike frames (outside SpikeProbe hooks; inHook = inside create/zone/... hooks)", sorted, 40);

            sb.Append("== owners by self ms in spike frames / in all play frames\n");
            var ownerMap = new Dictionary<string, OwnerAgg>();
            for (int i = 0; i < rows.Length; i++)
            {
                Rec r = rows[i];
                if (!ownerMap.TryGetValue(r.Owner, out OwnerAgg agg))
                    ownerMap[r.Owner] = agg = new OwnerAgg { Owner = r.Owner };
                agg.SpikeSelf += r.SpikeSelf;
                agg.SpikeHook += r.SpikeHook;
                agg.PlayTotal += r.PlaySelf + r.PlayHook;
                agg.Count++;
            }
            var ownerList = new List<OwnerAgg>(ownerMap.Values);
            ownerList.Sort((a, b) => b.SpikeSelf.CompareTo(a.SpikeSelf));
            int ownerTake = Math.Min(30, ownerList.Count);
            for (int i = 0; i < ownerTake; i++)
            {
                OwnerAgg g = ownerList[i];
                sb.Append(PadR(g.Owner, 30)).Append(" spike ").Append(PadF(g.SpikeSelf * MsPerTick, 8, "F1"))
                  .Append(" ms (+").Append((g.SpikeHook * MsPerTick).ToString("F1", Inv)).Append(" in hooks)   play ")
                  .Append(PadF(g.PlayTotal * MsPerTick, 9, "F0")).Append(" ms   ").Append(g.Count).Append(" methods\n");
            }
            sb.Append('\n');

            Array.Sort(sorted, ComparePlayTotalDesc);
            AppendTable(sb, "top 40 methods by self ms over all play frames", sorted, 40);

            // Every row, so a method below the top tables can be compared A/B (the tables rank by session totals).
            var tsv = new StringBuilder("spikeMs\tinHook\tcalls\tframes\ttop\tplayMs\tplayCalls\tmaxMs\towner\tkind\tmethod\n");
            for (int i = 0; i < sorted.Length; i++)
            {
                Rec r = sorted[i];
                tsv.Append((r.SpikeSelf * MsPerTick).ToString("F2", Inv)).Append('\t')
                   .Append((r.SpikeHook * MsPerTick).ToString("F2", Inv)).Append('\t')
                   .Append(r.SpikeCalls).Append('\t').Append(r.SpikeFrames).Append('\t').Append(r.SpikeTop).Append('\t')
                   .Append(((r.PlaySelf + r.PlayHook) * MsPerTick).ToString("F2", Inv)).Append('\t')
                   .Append(r.PlayCalls).Append('\t').Append((r.PlayMax * MsPerTick).ToString("F2", Inv)).Append('\t')
                   .Append(r.Owner).Append('\t').Append(r.Kind).Append('\t').Append(r.Name).Append('\n');
            }

            Array.Sort(sorted, ComparePlayMaxDesc);
            AppendTable(sb, "top 25 methods by single-frame max", sorted, 25);

            sb.Append("== first ").Append(SpawnFrames).Append(" play frames after the spawn (owner|method self ms [+in hooks] [x calls])\n");
            for (int i = 0; i < spawn.Length; i++) sb.Append(spawn[i]).Append('\n');

            try
            {
                AtomicFile.WriteAllText(Path.Combine(Profiler.OutputDir, "script-breakdown.txt"), sb.ToString());
                AtomicFile.WriteAllText(Path.Combine(Profiler.OutputDir, "script-methods.tsv"), tsv.ToString());
            }
            catch (Exception e)
            {
                Log.Warning("Script probe: write failed: " + e.Message);
            }
        }

        private static void AppendTable(StringBuilder sb, string title, Rec[] rows, int n)
        {
            sb.Append("== ").Append(title).Append('\n');
            sb.Append(PadL("spikeMs", 9)).Append(' ').Append(PadL("inHook", 8)).Append(' ').Append(PadL("calls", 9)).Append(' ')
              .Append(PadL("frames", 7)).Append(' ').Append(PadL("top", 6)).Append(' ').Append(PadL("playMs", 10)).Append(' ')
              .Append(PadL("playCalls", 9)).Append(' ').Append(PadL("maxMs", 8)).Append("  ").Append(PadR("owner", 28)).Append(' ')
              .Append(PadR("kind", 9)).Append(" method\n");
            int take = Math.Min(n, rows.Length);
            for (int i = 0; i < take; i++)
            {
                Rec r = rows[i];
                sb.Append(PadF(r.SpikeSelf * MsPerTick, 9, "F1")).Append(' ')
                  .Append(PadF(r.SpikeHook * MsPerTick, 8, "F1")).Append(' ')
                  .Append(PadN(r.SpikeCalls, 9)).Append(' ')
                  .Append(PadN(r.SpikeFrames, 7)).Append(' ')
                  .Append(PadN(r.SpikeTop, 6)).Append(' ')
                  .Append(PadF((r.PlaySelf + r.PlayHook) * MsPerTick, 10, "F0")).Append(' ')
                  .Append(PadN(r.PlayCalls, 9)).Append(' ')
                  .Append(PadF(r.PlayMax * MsPerTick, 8, "F1")).Append("  ")
                  .Append(PadR(r.Owner, 28)).Append(' ')
                  .Append(PadR(r.Kind, 9)).Append(' ').Append(r.Name).Append('\n');
            }
            sb.Append('\n');
        }

        private static string PadF(double v, int width, string fmt) => v.ToString(fmt, Inv).PadLeft(width);

        private static string PadN(long v, int width) => v.ToString(Inv).PadLeft(width);

        private static string PadL(string s, int width) => (s ?? "").PadLeft(width);

        private static string PadR(string s, int width) => (s ?? "").PadRight(width);

        private static string ShortName(Type t)
        {
            string n = t.Name;
            for (Type d = t.DeclaringType; d != null; d = d.DeclaringType) n = d.Name + "." + n;
            return n;
        }

        // Plugin types can name optional assemblies that are not installed: reflection on them throws, such members are skipped.
        private static string SafeLocation(Assembly a)
        {
            try
            {
                return a.IsDynamic ? null : a.Location;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static MethodBody SafeMethodBody(MethodInfo m)
        {
            try
            {
                return m.GetMethodBody();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool SafeEndsInHandler(MethodBody body)
        {
            try
            {
                return DetourSafety.EndsInHandler(body);
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static List<Type> SafeTypes(Assembly asm)
        {
            Type[] raw;
            try
            {
                raw = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                raw = e.Types;
            }
            catch (Exception)
            {
                raw = new Type[0];
            }
            var result = new List<Type>(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                Type t = raw[i];
                if (t == null) continue;
                bool hasGeneric;
                try
                {
                    hasGeneric = t.ContainsGenericParameters;
                }
                catch (Exception)
                {
                    hasGeneric = true;
                }
                if (!hasGeneric) result.Add(t);
            }
            return result;
        }

        private static bool Is(Type b, Type t)
        {
            try
            {
                return b.IsAssignableFrom(t);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static MethodInfo[] Methods(Type t)
        {
            try
            {
                return t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch (Exception)
            {
                return new MethodInfo[0];
            }
        }
    }
}
