using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Morgott.Preloader.Profiling
{
    /// <summary>Chrome Trace JSON + plain-text top-N summary of a finished trace.</summary>
    internal static class TraceReport
    {
        private const int TopN = 30;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string ChromeTrace(List<TraceEvent> events)
        {
            var sb = new StringBuilder(events.Count * 160);
            sb.Append("{\"displayTimeUnit\":\"ms\",\"traceEvents\":[\n");
            sb.Append("{\"name\":\"thread_name\",\"ph\":\"M\",\"pid\":1,\"tid\":").Append(StartupTrace.LaneScenes)
                .Append(",\"args\":{\"name\":\"scene loads (async)\"}},\n");
            sb.Append("{\"name\":\"thread_name\",\"ph\":\"M\",\"pid\":1,\"tid\":").Append(StartupTrace.LaneAsyncBundles)
                .Append(",\"args\":{\"name\":\"bundle loads (async)\"}},\n");
            sb.Append("{\"name\":\"thread_name\",\"ph\":\"M\",\"pid\":1,\"tid\":").Append(StartupTrace.LaneWorldLoad)
                .Append(",\"args\":{\"name\":\"world load (wall clock)\"}},\n");
            sb.Append("{\"name\":\"thread_name\",\"ph\":\"M\",\"pid\":1,\"tid\":").Append(StartupTrace.LaneWorldGen)
                .Append(",\"args\":{\"name\":\"location generation (per location type, wall clock)\"}},\n");
            sb.Append("{\"name\":\"thread_name\",\"ph\":\"M\",\"pid\":1,\"tid\":").Append(StartupTrace.MainThreadId)
                .Append(",\"args\":{\"name\":\"main\"}}");
            foreach (TraceEvent e in events)
            {
                sb.Append(",\n{\"name\":");
                Str(sb, e.Name);
                sb.Append(",\"cat\":");
                Str(sb, e.Cat);
                sb.Append(",\"ph\":\"").Append(e.Ph).Append("\",\"pid\":1,\"tid\":").Append(e.Tid);
                sb.Append(",\"ts\":").Append(Us(StartupTrace.ToMs(e.Start)));
                if (e.Ph == 'X')
                {
                    sb.Append(",\"dur\":").Append(Us(StartupTrace.DurMs(e.Start, e.End)));
                }
                else
                {
                    sb.Append(",\"s\":\"g\"");
                }
                sb.Append(",\"args\":{");
                bool first = true;
                Arg(sb, ref first, "detail", e.Detail);
                Arg(sb, ref first, "detail2", e.Detail2);
                if (e.Bytes >= 0)
                {
                    Sep(sb, ref first);
                    sb.Append("\"bytes\":").Append(e.Bytes);
                }
                if (e.Args != null)
                {
                    Sep(sb, ref first);
                    sb.Append(e.Args);
                }
                sb.Append("}}");
            }
            sb.Append("\n]}\n");
            return sb.ToString();
        }

        /// <summary>Per-event self time: duration minus direct children on the same real thread.</summary>
        private static Dictionary<TraceEvent, double> SelfTimes(List<TraceEvent> events, out double topLevelMainMs)
        {
            var self = new Dictionary<TraceEvent, double>();
            topLevelMainMs = 0;
            foreach (IGrouping<int, TraceEvent> thread in events.Where(e => e.Ph == 'X' && e.Tid < StartupTrace.LaneScenes).GroupBy(e => e.Tid))
            {
                var stack = new Stack<TraceEvent>();
                foreach (TraceEvent e in thread.OrderBy(e => e.Start).ThenByDescending(e => e.End))
                {
                    while (stack.Count > 0 && stack.Peek().End <= e.Start)
                    {
                        stack.Pop();
                    }
                    double dur = StartupTrace.DurMs(e.Start, e.End);
                    self[e] = dur;
                    if (stack.Count > 0)
                    {
                        TraceEvent parent = stack.Peek();
                        long childEnd = Math.Min(e.End, parent.End);
                        self[parent] -= StartupTrace.DurMs(e.Start, childEnd);
                    }
                    else if (thread.Key == StartupTrace.MainThreadId)
                    {
                        topLevelMainMs += dur;
                    }
                    stack.Push(e);
                }
            }
            return self;
        }

        /// <summary>Grouping key for the time-sink table: Harmony by owner, everything else by name.</summary>
        private static string Key(TraceEvent e) => e.Cat == "harmony" ? (e.Detail ?? "?") : e.Name;

        public static string Summary(List<TraceEvent> events, double endMs, string endLabel, string header)
        {
            Dictionary<TraceEvent, double> self = SelfTimes(events, out double accountedMs);
            var sb = new StringBuilder();
            sb.AppendLine(header);
            sb.AppendLine(F("{0} {1:F0} ms since process start{2}; main-thread spans cover {3:F0} ms ({4:F0}%), " +
                            "rest = Unity/native work, frames and waits between hooked calls",
                endLabel, endMs, StartupTrace.ProcessStartKnown ? "" : " (process start unknown: since Morgott.Preloader.Initialize)",
                accountedMs, endMs > 0 ? accountedMs * 100 / endMs : 0));
            TraceEvent menu = events.FirstOrDefault(e => e.Cat == "game" && e.Name == "FejdStartup.Start");
            TraceEvent request = events.FirstOrDefault(e => e.Cat == "game" && e.Name == "FejdStartup.LoadMainScene");
            TraceEvent spawn = events.FirstOrDefault(e => e.Cat == "mark" && e.Name == GameLifecycleProbe.WorldReadyMark);
            if (request != null && spawn != null)
            {
                sb.AppendLine(F("menu ready {0:F0} ms; world load {1:F0} ms (FejdStartup.LoadMainScene start -> first spawn), " +
                                "time in the menu before it {2:F0} ms (user input, not startup cost)",
                    menu != null ? StartupTrace.ToMs(menu.End) : -1, StartupTrace.DurMs(request.Start, spawn.Start),
                    menu != null ? StartupTrace.DurMs(menu.End, request.Start) : -1));
                sb.AppendLine("  Self-time tables below include the menu wait (idle frames are not spans, so it adds little).");
            }
            sb.AppendLine();
            WorldLoad(sb, events, self);

            sb.AppendLine("== Lifecycle marks (ms since process start; GC counts gen0/1/2; managed heap) ==");
            foreach (TraceEvent m in events.Where(e => e.Ph == 'i').OrderBy(e => e.Start))
            {
                sb.AppendLine(F("{0,9:F0}  {1}{2}   [{3}]", StartupTrace.ToMs(m.Start), m.Name,
                    m.Detail != null ? " (" + m.Detail + ")" : "", m.Args?.Replace("\"", "")));
            }
            sb.AppendLine();

            sb.AppendLine("== Self time by category (ms; nested spans subtracted, no double counting) ==");
            foreach (var g in self.GroupBy(kv => kv.Key.Cat).Select(g => new { Cat = g.Key, Ms = g.Sum(kv => kv.Value), N = g.Count() })
                         .OrderByDescending(g => g.Ms))
            {
                sb.AppendLine(F("{0,10:F1}  {1,-10} {2,6} events", g.Ms, g.Cat, g.N));
            }
            sb.AppendLine("  game = mods' patches on hooked game methods (outer minus body); game.body = original body (plus any");
            sb.AppendLine("  other mod's Priority.Last prefix / Priority.First postfix registered after ours: upper bound);");
            sb.AppendLine("  plugin = plugin cctor+Awake not covered by a nested span; lifecycle = rest of that phase.");
            sb.AppendLine();

            sb.AppendLine(F("== Top {0} time sinks by self time (category, key, calls, self ms, inclusive ms) ==", TopN));
            foreach (var g in self.GroupBy(kv => kv.Key.Cat + "|" + Key(kv.Key))
                         .Select(g => new
                         {
                             Cat = g.First().Key.Cat,
                             Key = Key(g.First().Key),
                             N = g.Count(),
                             Self = g.Sum(kv => kv.Value),
                             Incl = g.Sum(kv => StartupTrace.DurMs(kv.Key.Start, kv.Key.End)),
                         })
                         .OrderByDescending(g => g.Self).Take(TopN))
            {
                sb.AppendLine(F("{0,10:F1} {1,10:F1}  {2,-10} {3,5}x  {4}", g.Self, g.Incl, g.Cat, g.N, g.Key));
            }
            sb.AppendLine();

            List<TraceEvent> plugins = events.Where(e => e.Cat == "plugin").ToList();
            sb.AppendLine(F("== Plugins: cctor + Awake inclusive (ms), {0} plugins, total {1:F0} ms ==", plugins.Count,
                plugins.Sum(e => StartupTrace.DurMs(e.Start, e.End))));
            foreach (TraceEvent e in plugins.OrderByDescending(e => e.End - e.Start).Take(TopN))
            {
                sb.AppendLine(F("{0,10:F1}  {1}  ({2})", StartupTrace.DurMs(e.Start, e.End), e.Name, e.Detail2));
            }
            sb.AppendLine();

            List<TraceEvent> harmony = events.Where(e => e.Cat == "harmony").ToList();
            sb.AppendLine("== Harmony by owner: outermost API calls inclusive (ms), wrapper builds (count, ms) ==");
            foreach (var g in harmony.GroupBy(e => e.Detail ?? "?")
                         .Select(g => new
                         {
                             Owner = g.Key,
                             Top = g.Where(e => e.Depth == 1).Sum(e => StartupTrace.DurMs(e.Start, e.End)),
                             Builds = g.Count(e => e.Name == "UpdateWrapper"),
                             Skipped = g.Count(e => e.Name == "UpdateWrapper (skipped)"),
                             BuildMs = g.Where(e => e.Name.StartsWith("UpdateWrapper", StringComparison.Ordinal)).Sum(e => StartupTrace.DurMs(e.Start, e.End)),
                         })
                         .OrderByDescending(g => g.Top).Take(TopN))
            {
                sb.AppendLine(F("{0,10:F1}  {1,5} builds {2,8:F1} ms  {3,4} skipped  {4}", g.Top, g.Builds, g.BuildMs, g.Skipped, g.Owner));
            }
            sb.AppendLine("  Top originals by wrapper build time (ms, builds):");
            foreach (var g in harmony.Where(e => e.Name == "UpdateWrapper").GroupBy(e => e.Detail2 ?? "?")
                         .Select(g => new { Target = g.Key, N = g.Count(), Ms = g.Sum(e => StartupTrace.DurMs(e.Start, e.End)) })
                         .OrderByDescending(g => g.Ms).Take(15))
            {
                sb.AppendLine(F("{0,10:F1} {1,4}x  {2}", g.Ms, g.N, g.Target));
            }
            sb.AppendLine();

            List<TraceEvent> bundles = events.Where(e => e.Cat == "bundle").ToList();
            // A cache hit loads its copy from inside the hooked stream load: count only outermost spans.
            List<TraceEvent> outer = bundles.Where(e => !bundles.Any(p => p != e && p.Tid == e.Tid && p.Start <= e.Start &&
                                                                         p.End >= e.End && (p.Start < e.Start || p.End > e.End))).ToList();
            sb.AppendLine(F("== AssetBundle loads: {0} calls ({1} outermost), {2:F0} ms on calling threads ==", bundles.Count,
                outer.Count, outer.Sum(e => StartupTrace.DurMs(e.Start, e.End))));
            foreach (TraceEvent e in bundles.OrderByDescending(e => e.End - e.Start).Take(TopN))
            {
                sb.AppendLine(F("{0,10:F1}  {1,-20} {2,8:F1} MB  {3}", StartupTrace.DurMs(e.Start, e.End), e.Detail2,
                    e.Bytes / (1024.0 * 1024.0), e.Detail ?? e.Name));
            }
            List<TraceEvent> async = events.Where(e => e.Cat == "bundle.async").ToList();
            if (async.Count > 0)
            {
                sb.AppendLine(F("  async request -> completed: {0} loads, longest {1:F0} ms", async.Count,
                    async.Max(e => StartupTrace.DurMs(e.Start, e.End))));
            }
            sb.AppendLine();

            sb.AppendLine("== Game methods: inclusive (ms) = body + mods' patches (body is an upper bound for vanilla) ==");
            foreach (var g in events.Where(e => e.Cat == "game").GroupBy(e => e.Name).Select(g => new
            {
                Name = g.Key,
                N = g.Count(),
                Incl = g.Sum(e => StartupTrace.DurMs(e.Start, e.End)),
                Body = events.Where(b => b.Cat == "game.body" && b.Name == g.Key).Sum(b => StartupTrace.DurMs(b.Start, b.End)),
            }).OrderByDescending(g => g.Incl))
            {
                sb.AppendLine(F("{0,10:F1}  body {1,8:F1}  mods {2,8:F1}  {3,3}x  {4}", g.Incl, g.Body, g.Incl - g.Body, g.N, g.Name));
            }
            sb.AppendLine("  Mods' patches by owner (game.patch, inclusive ms, calls; transpilers count as body):");
            foreach (var g in events.Where(e => e.Cat == "game.patch").GroupBy(e => e.Detail2 + "|" + e.Name)
                         .Select(g => new { Target = g.First().Detail2, Owner = g.First().Name, N = g.Count(), Ms = g.Sum(e => StartupTrace.DurMs(e.Start, e.End)) })
                         .OrderByDescending(g => g.Ms).Take(TopN))
            {
                sb.AppendLine(F("{0,10:F1} {1,4}x  {2,-28} {3}", g.Ms, g.N, g.Target, g.Owner));
            }
            foreach (var g in events.Where(e => e.Cat == "jotunn").GroupBy(e => e.Name)
                         .Select(g => new { Name = g.Key, N = g.Count(), Ms = g.Sum(e => StartupTrace.DurMs(e.Start, e.End)) })
                         .OrderByDescending(g => g.Ms).Take(TopN))
            {
                sb.AppendLine(F("{0,10:F1} {1,4}x  jotunn {2}", g.Ms, g.N, g.Name));
            }
            foreach (TraceEvent e in events.Where(e => e.Cat == "scene"))
            {
                sb.AppendLine(F("{0,10:F1}  scene {1} ({2})", StartupTrace.DurMs(e.Start, e.End), e.Name, e.Detail));
            }
            AggregatedSections(sb, events);
            return sb.ToString();
        }

        /// <summary>WorldGen per-location spans (own lane, not in self time).</summary>
        private static void AggregatedSections(StringBuilder sb, List<TraceEvent> events)
        {
            List<TraceEvent> locations = events.Where(e => e.Cat == "worldgen").ToList();
            if (locations.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(F("== Location generation: {0} location types, first start -> last end {1:F0} ms (wall ms, longest {2}) ==",
                    locations.Count, StartupTrace.DurMs(locations.Min(e => e.Start), locations.Max(e => e.End)), TopN));
                foreach (TraceEvent e in locations.OrderByDescending(e => e.End - e.Start).Take(TopN))
                {
                    sb.AppendLine(F("{0,10:F1}  {1}  ({2})", StartupTrace.DurMs(e.Start, e.End), e.Name, e.Detail));
                }
            }
        }

        /// <summary>World-load wall clock (click -> loading screen -> ZoneSystem.Start) and the spans inside that
        /// window ranked by self time, mod patches and Jotunn handlers named by owner.</summary>
        private static void WorldLoad(StringBuilder sb, List<TraceEvent> events, Dictionary<TraceEvent, double> self)
        {
            List<TraceEvent> wall = events.Where(e => e.Cat == "worldload").OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
            long from, to;
            if (wall.Count > 0)
            {
                from = wall.Min(e => e.Start);
                to = wall.Max(e => e.End);
            }
            else
            {
                TraceEvent request = events.FirstOrDefault(e => e.Cat == "game" && e.Name == "FejdStartup.LoadMainScene");
                TraceEvent zone = events.FirstOrDefault(e => e.Cat == "game" && e.Name == "ZoneSystem.Start" && request != null && e.Start > request.Start);
                if (request == null || zone == null)
                {
                    return;
                }
                from = request.Start;
                to = zone.End;
            }
            sb.AppendLine("== World load wall clock (ms; click = FejdStartup.OnWorldStart start) ==");
            foreach (TraceEvent e in wall)
            {
                sb.AppendLine(F("{0,10:F1}  {1}{2}", StartupTrace.DurMs(e.Start, e.End), e.Name, e.Detail != null ? "  (" + e.Detail + ")" : ""));
            }
            if (wall.Count == 0)
            {
                sb.AppendLine("  (no rendered-frame mark: window = LoadMainScene start -> ZoneSystem.Start end)");
            }
            var inWindow = self.Where(kv => kv.Key.Start >= from && kv.Key.End <= to && kv.Key.Tid == StartupTrace.MainThreadId).ToList();
            sb.AppendLine(F("== World load culprits: top {0} by self time in that window ({1:F0} ms, {2:F0} ms in hooked main-thread spans) ==",
                TopN, StartupTrace.DurMs(from, to), inWindow.Sum(kv => kv.Value)));
            sb.AppendLine("      self       incl  calls  category    what (owner [assembly] :: method @ hooked target)");
            foreach (var g in inWindow.GroupBy(kv => kv.Key.Cat + "|" + Culprit(kv.Key))
                         .Select(g => new
                         {
                             Cat = g.First().Key.Cat,
                             What = Culprit(g.First().Key),
                             N = g.Count(),
                             Self = g.Sum(kv => kv.Value),
                             Incl = g.Sum(kv => StartupTrace.DurMs(kv.Key.Start, kv.Key.End)),
                         })
                         .OrderByDescending(g => g.Self).Take(TopN))
            {
                sb.AppendLine(F("{0,10:F1} {1,10:F1} {2,6}x  {3,-10}  {4}", g.Self, g.Incl, g.N, g.Cat, g.What));
            }
            sb.AppendLine("  game.patch = one mod prefix/postfix/finalizer or Jotunn event handler (needs TimeModPatches); game = that");
            sb.AppendLine("  method's patches not timed one by one; game.body = vanilla body; jotunn = Jotunn's own registration step.");
            sb.AppendLine();
        }

        private static string Culprit(TraceEvent e)
        {
            switch (e.Cat)
            {
                case "game.patch": return e.Name + " :: " + e.Detail + " @ " + e.Detail2;
                case "game.body": return "vanilla " + e.Name;
                case "game": return "untimed mod patches on " + e.Name;
                default: return Key(e);
            }
        }

        /// <summary>One log line: end time (menu ready / first spawn) plus the three largest self-time sinks.</summary>
        public static string OneLine(List<TraceEvent> events, double endMs, string endLabel)
        {
            Dictionary<TraceEvent, double> self = SelfTimes(events, out double accountedMs);
            IEnumerable<string> top = self.GroupBy(kv => kv.Key.Cat + ":" + Key(kv.Key))
                .Select(g => new { g.Key, Ms = g.Sum(kv => kv.Value) })
                .OrderByDescending(g => g.Ms).Take(3).Select(g => F("{0} {1:F0} ms", g.Key, g.Ms));
            return F("{0} {1:F1} s, {2:F1} s in hooked spans; top: {3}", endLabel, endMs / 1000, accountedMs / 1000,
                string.Join(", ", top.ToArray()));
        }

        private static string F(string format, params object[] args) => string.Format(Inv, format, args);

        private static string Us(double ms) => (ms * 1000).ToString("F1", Inv);

        private static void Sep(StringBuilder sb, ref bool first)
        {
            if (!first)
            {
                sb.Append(',');
            }
            first = false;
        }

        private static void Arg(StringBuilder sb, ref bool first, string key, string value)
        {
            if (value == null)
            {
                return;
            }
            Sep(sb, ref first);
            sb.Append('"').Append(key).Append("\":");
            Str(sb, value);
        }

        private static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
