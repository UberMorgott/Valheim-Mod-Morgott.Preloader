using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
        }

        private sealed class Row
        {
            public string Owner, Name, Kind;
            public long SpikeSelf, SpikeHook, SpikeCalls, SpikeFrames, SpikeTop, PlaySelf, PlayHook, PlayCalls, PlayMax;
        }

        private const int SpawnFrames = 5;

        // Assemblies skipped the way StressProf skips them (native crashes while detouring in the lab; their time stays
        // in the wrapped game callers).
        private static readonly HashSet<string> SkipAsm = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Warfare", "Wizardry", "Armory", "Morgott.Preloader", "0Harmony", "BepInEx", "BepInEx.Preloader", "MonoMod.Utils", "MonoMod.RuntimeDetour" };

        private static readonly HashSet<string> PluginMsgs = new HashSet<string> { "Update", "FixedUpdate", "LateUpdate", "OnGUI" };
        private static readonly HashSet<string> VanillaMsgs = new HashSet<string>
            { "Update", "FixedUpdate", "LateUpdate", "CustomUpdate", "CustomFixedUpdate", "CustomLateUpdate", "UpdateAI" };

        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
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

        private static int _wrappedPatches, _wrappedPlugin, _wrappedVanilla, _failed, _skipped;
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

        private static string Dir => Path.Combine(Paths.BepInExRootPath, "Morgott.Preloader");

        internal static bool On => _on;

        /// <summary>A SpikeProbe hook call that just ended inside a wrapped call: its time is not the caller's self time.</summary>
        /// <param name="hookDepth">SpikeProbe's hook depth after that call ended: only a hook directly inside the innermost wrapped
        /// call counts (its nested hooks are already inside its total).</param>
        internal static void AddChild(long ticks, int hookDepth)
        {
            if (_on && _depth > 0 && StackHookDepth[_depth - 1] == hookDepth)
            {
                StackChild[_depth - 1] += ticks;
            }
        }

        /// <summary>Main menu, after SpikeProbe's player loop: every plugin has loaded and patched.</summary>
        internal static void Install()
        {
            var sw = Stopwatch.StartNew();
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            Directory.CreateDirectory(Dir);
            string pendingPath = Path.Combine(Dir, "script-probe.pending");
            string skipPath = Path.Combine(Dir, "script-probe.skip");
            var skip = new HashSet<string>(File.Exists(skipPath) ? File.ReadAllLines(skipPath) : new string[0]);
            if (File.Exists(pendingPath))
            {
                string crashed = File.ReadAllText(pendingPath).Trim();
                if (crashed.Length > 0 && skip.Add(crashed))
                {
                    File.AppendAllText(skipPath, crashed + Environment.NewLine);
                    Log.Warning($"Script probe: the last launch died while wrapping {crashed}; it is skipped from now on");
                }
                File.Delete(pendingPath);
            }
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
                if (!string.IsNullOrEmpty(pi.Location) && !byPath.ContainsKey(Path.GetFullPath(pi.Location)))
                {
                    byPath[Path.GetFullPath(pi.Location)] = pi.Metadata.Name;
                }
            }
            var owners = new Dictionary<Assembly, string>();
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string loc;
                try
                {
                    loc = a.IsDynamic ? null : a.Location;
                }
                catch (Exception)
                {
                    loc = null;
                }
                if (!string.IsNullOrEmpty(loc) && byPath.TryGetValue(Path.GetFullPath(loc), out string name))
                {
                    owners[a] = name;
                }
            }
            string OwnerOf(MethodBase m)
            {
                Assembly a = m.DeclaringType?.Assembly;
                return a != null && owners.TryGetValue(a, out string n) ? n : a?.GetName().Name ?? "?";
            }

            var targets = new List<(MethodInfo m, string owner, string name, string kind)>();
            var seen = new HashSet<MethodBase>();
            void Add(MethodInfo m, string owner, string name, string kind)
            {
                if (m == null || m.IsAbstract || m.ContainsGenericParameters || m.DeclaringType == null || m.DeclaringType.ContainsGenericParameters)
                {
                    return;
                }
                if (SkipAsm.Contains(m.DeclaringType.Assembly.GetName().Name) || !seen.Add(m))
                {
                    return;
                }
                MethodBody body;
                try
                {
                    body = m.GetMethodBody();
                }
                catch (Exception)
                {
                    return;
                }
                if (body != null)
                {
                    targets.Add((m, owner, name, kind));
                }
            }

            // A: foreign patch methods on anything (snapshot: wrapping them adds our own patches)
            foreach (MethodBase target in Harmony.GetAllPatchedMethods().ToList())
            {
                Patches info = Harmony.GetPatchInfo(target);
                if (info == null)
                {
                    continue;
                }
                foreach (Patch p in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers))
                {
                    if (p.PatchMethod == null || p.owner.StartsWith(PatchGuard.OwnPrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    Add(p.PatchMethod, OwnerOf(p.PatchMethod), p.PatchMethod.DeclaringType?.Name + "." + p.PatchMethod.Name + " -> " + target.DeclaringType?.Name + "." + target.Name, "patch");
                }
            }
            int patches = targets.Count;

            // B: plugin assemblies (Unity messages, coroutine bodies, module ticks)
            foreach (Assembly asm in owners.Keys)
            {
                foreach (Type t in SafeTypes(asm))
                {
                    bool mb = Is(typeof(MonoBehaviour), t), it = Is(typeof(IEnumerator), t);
                    foreach (MethodInfo m in Methods(t))
                    {
                        string kind = mb && PluginMsgs.Contains(m.Name) ? "msg" : it && m.Name == "MoveNext" ? "coroutine" : !m.IsStatic && m.Name == "Tick" && m.GetParameters().Length <= 1 ? "tick" : null;
                        if (kind != null)
                        {
                            Add(m, owners[asm], ShortName(t) + "." + m.Name, kind);
                        }
                    }
                }
            }
            int plugin = targets.Count - patches;

            // C: the game's per-frame messages
            foreach (Type t in SafeTypes(typeof(ZNetScene).Assembly))
            {
                if (!Is(typeof(MonoBehaviour), t))
                {
                    continue;
                }
                foreach (MethodInfo m in Methods(t))
                {
                    if (VanillaMsgs.Contains(m.Name))
                    {
                        Add(m, "vanilla", t.Name + "." + m.Name, "vanilla");
                    }
                }
            }
            int vanilla = targets.Count - patches - plugin;

            var harmony = new Harmony(HarmonyId);
            var pre = new HarmonyMethod(AccessTools.Method(typeof(ScriptProbe), nameof(Pre)), Priority.First);
            var fin = new HarmonyMethod(AccessTools.Method(typeof(ScriptProbe), nameof(Fin)), Priority.Last);
            foreach ((MethodInfo m, string owner, string name, string kind) in targets)
            {
                string id = PatchOwnerProbe.Identity(m);
                if (skip.Contains(id))
                {
                    _skipped++;
                    continue;
                }
                try
                {
                    File.WriteAllText(pendingPath, id);
                    PatchOwnerProbe.ClearResolveCache();
                    Idx[m] = Recs.Count;
                    Recs.Add(new Rec { Owner = owner, Name = name, Kind = kind });
                    harmony.Patch(m, prefix: pre, finalizer: fin);
                    if (kind == "patch")
                    {
                        _wrappedPatches++;
                    }
                    else if (kind == "vanilla")
                    {
                        _wrappedVanilla++;
                    }
                    else
                    {
                        _wrappedPlugin++;
                    }
                }
                catch (Exception e)
                {
                    Idx.Remove(m);
                    _failed++;
                    if (_failed <= 20)
                    {
                        Log.Warning($"Script probe: cannot wrap {owner} {name}: {e.GetType().Name} {e.Message}");
                    }
                }
                finally
                {
                    PatchOwnerProbe.ClearResolveCache();
                }
            }
            File.Delete(pendingPath);
            Calibrate(harmony, pre, fin);
            _installSec = sw.Elapsed.TotalSeconds;
            _on = true;
            Log.Info(string.Format(CultureInfo.InvariantCulture,
                "Script probe: {0} targets ({1} mod patch methods, {2} plugin messages/coroutines/ticks, {3} vanilla messages); wrapped {4}+{5}+{6}, failed {7}, skipped {8}; {9:F1} s; {10:F0} ns per wrapped call",
                targets.Count, patches, plugin, vanilla, _wrappedPatches, _wrappedPlugin, _wrappedVanilla, _failed, _skipped, _installSec, _costTicksPerCall * MsPerTick * 1e6));
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
                long t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < n; i++)
                {
                    x = Dummy(x);
                }
                long plain = Stopwatch.GetTimestamp() - t0;
                Idx[d] = Recs.Count;
                Recs.Add(new Rec { Owner = "(calibration)", Name = "Dummy", Kind = "calibration" });
                harmony.Patch(d, prefix: pre, finalizer: fin);
                _on = true;
                t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < n; i++)
                {
                    x = Dummy(x);
                }
                long wrapped = Stopwatch.GetTimestamp() - t0;
                _on = false;
                harmony.Unpatch(d, HarmonyPatchType.All, HarmonyId);
                Rec r = Recs[Idx[d]];
                r.FrameSelf = r.FrameHook = 0;
                r.FrameCalls = 0;
                r.Touched = false;
                Touched.Clear();
                _depth = 0;
                GC.KeepAlive(x);
                _costTicksPerCall = Math.Max(0, wrapped - plain) / (double)n;
            }
            catch (Exception e)
            {
                Log.Warning("Script probe: calibration failed: " + e.Message);
            }
        }

        private static void Pre(out long __state)
        {
            if (!_on || _depth >= StackStart.Length || Thread.CurrentThread.ManagedThreadId != _mainThread)
            {
                __state = 0;
                return;
            }
            __state = Stopwatch.GetTimestamp();
            StackStart[_depth] = __state;
            StackChild[_depth] = 0;
            StackHookDepth[_depth] = SpikeProbe.HookDepth;
            _depth++;
        }

        private static void Fin(long __state, MethodBase __originalMethod)
        {
            if (__state == 0 || _depth == 0)
            {
                return;
            }
            long total = Stopwatch.GetTimestamp() - __state;
            _depth--;
            long self = total - StackChild[_depth];
            // Only a call directly inside the parent counts as its child. Inside a SpikeProbe hook the hook's whole total
            // reaches the parent through AddChild when the hook ends, so passing this call's total up as well would
            // subtract it twice (ZoneSystem.Update went negative by every mod patch running inside SpawnZone /
            // Heightmap hooks).
            if (_depth > 0 && StackHookDepth[_depth - 1] == SpikeProbe.HookDepth)
            {
                StackChild[_depth - 1] += total;
            }
            if (!Idx.TryGetValue(__originalMethod, out int i))
            {
                return;
            }
            Rec r = Recs[i];
            if (self < 0)
            {
                self = SelfCheck(r, self);
            }
            r.FrameCalls++;
            _frameCalls++;
            if (SpikeProbe.InHook)
            {
                r.FrameHook += self;
            }
            else
            {
                r.FrameSelf += self;
            }
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
            if (-self > SelfEpsilonTicks)
            {
                _violations++;
                if (Offenders.Count < MaxOffenders)
                {
                    Offenders.Add(string.Format(CultureInfo.InvariantCulture, "{0}|{1} {2:F3} ms", r.Owner, r.Name, self * MsPerTick));
                }
            }
            return 0;
        }

        /// <summary>The self-check line for the log and script-breakdown.txt.</summary>
        private static string SelfCheckLine(long violations, string[] offenders) =>
            "[Profiler] ScriptProbe self-check: " + violations.ToString(CultureInfo.InvariantCulture) + " violation(s)" +
            (offenders.Length > 0 ? " (negative self time; first: " + string.Join("; ", offenders) + ")" : "");

        /// <summary>SpikeProbe frame end (main thread). Returns the frame's top methods for a spike row, else null.</summary>
        internal static string EndFrame(bool play, bool spike, double ms)
        {
            if (!_on)
            {
                return null;
            }
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
                long bestV = 0;
                foreach (int i in Touched)
                {
                    Rec r = Recs[i];
                    r.PlaySelf += r.FrameSelf;
                    r.PlayHook += r.FrameHook;
                    r.PlayCalls += r.FrameCalls;
                    r.PlayMax = Math.Max(r.PlayMax, r.FrameSelf + r.FrameHook);
                    if (spike)
                    {
                        r.SpikeSelf += r.FrameSelf;
                        r.SpikeHook += r.FrameHook;
                        r.SpikeCalls += r.FrameCalls;
                        r.SpikeFrames++;
                        if (r.FrameSelf > bestV)
                        {
                            bestV = r.FrameSelf;
                            best = i;
                        }
                    }
                }
                if (spike && best >= 0)
                {
                    Recs[best].SpikeTop++;
                }
                bool spawn = _playFrames <= SpawnFrames;
                if (spike || spawn)
                {
                    int take = spawn ? 15 : 4;
                    long covered = 0;
                    foreach (int i in Touched)
                    {
                        covered += Recs[i].FrameSelf;
                    }
                    top = string.Join("; ", Touched.Where(i => (Recs[i].FrameSelf + Recs[i].FrameHook) * MsPerTick >= (spawn ? 0.2 : 0.5))
                        .OrderByDescending(i => Recs[i].FrameSelf + Recs[i].FrameHook).Take(take)
                        .Select(i => Recs[i].Owner + "|" + Recs[i].Name + " " + F(Recs[i].FrameSelf * MsPerTick) +
                                     (Recs[i].FrameHook * MsPerTick >= 0.1 ? "+" + F(Recs[i].FrameHook * MsPerTick) + "h" : "") + (Recs[i].FrameCalls > 1 ? " x" + Recs[i].FrameCalls : ""))
                        .ToArray());
                    if (spawn)
                    {
                        SpawnLines.Add(string.Format(CultureInfo.InvariantCulture, "play frame {0}: {1:F1} ms, wrapped self {2:F1} ms, {3} wrapped calls | {4}",
                            _playFrames, ms, covered * MsPerTick, _frameCalls, top));
                    }
                }
            }
            foreach (int i in Touched)
            {
                Rec r = Recs[i];
                r.FrameSelf = r.FrameHook = 0;
                r.FrameCalls = 0;
                r.Touched = false;
            }
            Touched.Clear();
            _frameCalls = 0;
            _depth = 0; // a frame boundary is never inside a measured call (the player loop runs it first)
            return top;
        }

        /// <summary>Main-thread copy of the counters for a worker-thread write.</summary>
        internal static object Snapshot()
        {
            if (!_on)
            {
                return null;
            }
            return (Recs.Where(r => r.PlayCalls > 0).Select(r => new Row
            {
                Owner = r.Owner,
                Name = r.Name,
                Kind = r.Kind,
                SpikeSelf = r.SpikeSelf,
                SpikeHook = r.SpikeHook,
                SpikeCalls = r.SpikeCalls,
                SpikeFrames = r.SpikeFrames,
                SpikeTop = r.SpikeTop,
                PlaySelf = r.PlaySelf,
                PlayHook = r.PlayHook,
                PlayCalls = r.PlayCalls,
                PlayMax = r.PlayMax,
            }).ToArray(), SpawnLines.ToArray(), _playFrames, _playCalls, _spikeFrames, _spikeFrameCalls, _spikeFrameMs, _violations, Offenders.ToArray());
        }

        /// <summary>Worker thread: script-breakdown.txt from a <see cref="Snapshot"/>; residualMs = SpikeProbe's script residual over the spike frames.</summary>
        internal static void Write(object snapshot, double residualMs, double scriptMs)
        {
            if (snapshot == null)
            {
                return;
            }
            var (rows, spawn, playFrames, playCalls, spikeFrames, spikeCalls, spikeMs, violations, offenders) =
                ((Row[], string[], long, long, long, long, double, long, string[]))snapshot;
            string selfCheck = SelfCheckLine(violations, offenders);
            if (Interlocked.Exchange(ref _loggedViolations, violations) != violations)
            {
                if (violations == 0)
                {
                    Log.Info(selfCheck);
                }
                else
                {
                    Log.Warning(selfCheck);
                }
            }
            double nsPerCall = _costTicksPerCall * MsPerTick * 1e6;
            double coveredMs = rows.Sum(r => r.SpikeSelf) * MsPerTick;
            var sb = new StringBuilder();
            sb.Append("Morgott.Preloader script breakdown - ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(string.Format(CultureInfo.InvariantCulture,
                "wrapped {0} mod patch methods, {1} plugin messages/coroutines/ticks, {2} vanilla messages; failed {3}, skipped {4}; install {5:F1} s at the main menu\n",
                _wrappedPatches, _wrappedPlugin, _wrappedVanilla, _failed, _skipped, _installSec));
            sb.Append(string.Format(CultureInfo.InvariantCulture,
                "overhead: {0:F0} ns per wrapped call (calibrated); play frames {1}, {2:F0} wrapped calls/frame -> ~{3:F0} us/frame; spike frames {4}, {5:F0} calls/frame -> ~{6:F2} ms/spike frame ({7:F1} of {8:F0} spike ms)\n",
                nsPerCall, playFrames, playFrames > 0 ? playCalls / (double)playFrames : 0, playFrames > 0 ? playCalls * nsPerCall / 1000.0 / playFrames : 0,
                spikeFrames, spikeFrames > 0 ? spikeCalls / (double)spikeFrames : 0, spikeFrames > 0 ? spikeCalls * nsPerCall / 1e6 / spikeFrames : 0, spikeCalls * nsPerCall / 1e6, spikeMs));
            sb.Append(string.Format(CultureInfo.InvariantCulture,
                "spike frames: script subsystems {0:F0} ms, residual (minus SpikeProbe hooks and probe) {1:F0} ms; wrapped self time outside hooks {2:F0} ms ({3:F0}% of the residual); rest = unwrapped engine/script work and wrapper overhead\n",
                scriptMs, residualMs, coveredMs, residualMs > 0 ? 100 * coveredMs / residualMs : 0));
            sb.Append(selfCheck).Append("\n\n");

            void Table(string title, IEnumerable<Row> sel, int n)
            {
                sb.Append("== ").Append(title).Append('\n');
                sb.Append(string.Format(CultureInfo.InvariantCulture, "{0,9} {1,8} {2,9} {3,7} {4,6} {5,10} {6,9} {7,8}  {8,-28} {9,-9} {10}\n",
                    "spikeMs", "inHook", "calls", "frames", "top", "playMs", "playCalls", "maxMs", "owner", "kind", "method"));
                foreach (Row r in sel.Take(n))
                {
                    sb.Append(string.Format(CultureInfo.InvariantCulture, "{0,9:F1} {1,8:F1} {2,9} {3,7} {4,6} {5,10:F0} {6,9} {7,8:F1}  {8,-28} {9,-9} {10}\n",
                        r.SpikeSelf * MsPerTick, r.SpikeHook * MsPerTick, r.SpikeCalls, r.SpikeFrames, r.SpikeTop, (r.PlaySelf + r.PlayHook) * MsPerTick, r.PlayCalls,
                        r.PlayMax * MsPerTick, r.Owner, r.Kind, r.Name));
                }
                sb.Append('\n');
            }

            Table("top 40 methods by self ms in spike frames (outside SpikeProbe hooks; inHook = inside create/zone/... hooks)", rows.OrderByDescending(r => r.SpikeSelf), 40);
            sb.Append("== owners by self ms in spike frames / in all play frames\n");
            foreach (var g in rows.GroupBy(r => r.Owner).OrderByDescending(g => g.Sum(r => r.SpikeSelf)).Take(30))
            {
                sb.Append(string.Format(CultureInfo.InvariantCulture, "{0,-30} spike {1,8:F1} ms (+{2:F1} in hooks)   play {3,9:F0} ms   {4} methods\n",
                    g.Key, g.Sum(r => r.SpikeSelf) * MsPerTick, g.Sum(r => r.SpikeHook) * MsPerTick, g.Sum(r => r.PlaySelf + r.PlayHook) * MsPerTick, g.Count()));
            }
            sb.Append('\n');
            Table("top 40 methods by self ms over all play frames", rows.OrderByDescending(r => r.PlaySelf + r.PlayHook), 40);
            Table("top 25 methods by single-frame max", rows.OrderByDescending(r => r.PlayMax), 25);
            sb.Append("== first ").Append(SpawnFrames).Append(" play frames after the spawn (owner|method self ms [+in hooks] [x calls])\n");
            foreach (string l in spawn)
            {
                sb.Append(l).Append('\n');
            }
            try
            {
                AtomicFile.WriteAllText(Path.Combine(Dir, "script-breakdown.txt"), sb.ToString());
            }
            catch (Exception e)
            {
                Log.Warning("Script probe: write failed: " + e.Message);
            }
        }

        private static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

        private static string ShortName(Type t)
        {
            string n = t.Name;
            for (Type d = t.DeclaringType; d != null; d = d.DeclaringType)
            {
                n = d.Name + "." + n;
            }
            return n;
        }

        private static IEnumerable<Type> SafeTypes(Assembly asm)
        {
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t != null).ToArray();
            }
            catch (Exception)
            {
                yield break;
            }
            foreach (Type t in types)
            {
                bool open;
                try
                {
                    open = t.ContainsGenericParameters;
                }
                catch (Exception)
                {
                    continue;
                }
                if (!open)
                {
                    yield return t;
                }
            }
        }

        // Plugin types can name optional assemblies that are not installed: reflection on them throws, such members are skipped.
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
