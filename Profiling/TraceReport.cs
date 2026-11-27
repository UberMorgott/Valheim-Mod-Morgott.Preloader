using System;
using System.Collections.Generic;
using System.Globalization;
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
            (int Tid, string Name)[] lanes =
            {
                (StartupTrace.LaneScenes, "scene loads (async)"),
                (StartupTrace.LaneAsyncBundles, "bundle loads (async)"),
                (StartupTrace.LaneWorldLoad, "world load (wall clock)"),
                (StartupTrace.LaneWorldGen, "location generation (per location type, wall clock)"),
                (StartupTrace.MainThreadId, "main"),
            };
            for (int i = 0; i < lanes.Length; i++)
            {
                if (i > 0) sb.Append(",\n");
                sb.Append("{\"name\":\"thread_name\",\"ph\":\"M\",\"pid\":1,\"tid\":")
                  .Append(lanes[i].Tid.ToString(Inv)).Append(",\"args\":{\"name\":\"")
                  .Append(lanes[i].Name).Append("\"}}");
            }
            for (int i = 0; i < events.Count; i++)
            {
                TraceEvent e = events[i];
                sb.Append(",\n{\"name\":");
                Str(sb, e.Name);
                sb.Append(",\"cat\":");
                Str(sb, e.Cat);
                sb.Append(",\"ph\":\"").Append(e.Ph).Append("\",\"pid\":1,\"tid\":").Append(e.Tid);
                sb.Append(",\"ts\":").Append(Us(StartupTrace.ToMs(e.Start)));
                sb.Append(e.Ph == 'X' ? ",\"dur\":" + Us(Dur(e)) : ",\"s\":\"g\"");
                sb.Append(",\"args\":{");
                bool first = true;
                Arg(sb, ref first, "detail", e.Detail);
                Arg(sb, ref first, "detail2", e.Detail2);
                if (e.Bytes >= 0) Sep(sb, ref first).Append("\"bytes\":").Append(e.Bytes);
                if (e.Args != null) Sep(sb, ref first).Append(e.Args);
                sb.Append("}}");
            }
            sb.Append("\n]}\n");
            return sb.ToString();
        }

        private static double Dur(TraceEvent e) => StartupTrace.DurMs(e.Start, e.End);

        /// <summary>Per-event self time: duration minus direct children on the same real thread.</summary>
        private static Dictionary<TraceEvent, double> SelfTimes(List<TraceEvent> events, out double topLevelMainMs)
        {
            var self = new Dictionary<TraceEvent, double>();
            topLevelMainMs = 0;
            var byThread = new Dictionary<int, List<TraceEvent>>();
            for (int i = 0; i < events.Count; i++)
            {
                TraceEvent e = events[i];
                if (e.Ph != 'X' || e.Tid >= StartupTrace.LaneScenes) continue;
                if (!byThread.TryGetValue(e.Tid, out List<TraceEvent> list))
                    byThread[e.Tid] = list = new List<TraceEvent>();
                list.Add(e);
            }
            foreach (KeyValuePair<int, List<TraceEvent>> kv in byThread)
            {
                List<TraceEvent> list = kv.Value;
                list.Sort(CompareStartAscEndDesc);
                var stack = new Stack<TraceEvent>();
                for (int i = 0; i < list.Count; i++)
                {
                    TraceEvent e = list[i];
                    while (stack.Count > 0 && stack.Peek().End <= e.Start) stack.Pop();
                    self[e] = Dur(e);
                    if (stack.Count > 0) self[stack.Peek()] -= StartupTrace.DurMs(e.Start, Math.Min(e.End, stack.Peek().End));
                    else if (kv.Key == StartupTrace.MainThreadId) topLevelMainMs += self[e];
                    stack.Push(e);
                }
            }
            return self;
        }

        private static int CompareStartAscEndDesc(TraceEvent a, TraceEvent b)
        {
            int c = a.Start.CompareTo(b.Start);
            return c != 0 ? c : b.End.CompareTo(a.End);
        }

        private static int CompareStartAsc(TraceEvent a, TraceEvent b) => a.Start.CompareTo(b.Start);

        private static int CompareDurDesc(TraceEvent a, TraceEvent b) => (b.End - b.Start).CompareTo(a.End - a.Start);

        private static int CompareNameAsc(TraceEvent a, TraceEvent b) => StringComparer.Ordinal.Compare(a.Name, b.Name);

        /// <summary>Grouping key for the time-sink table: Harmony by owner, everything else by name.</summary>
        private static string Key(TraceEvent e) => e.Cat == "harmony" ? (e.Detail ?? "?") : e.Name;

        private static TraceEvent Game(List<TraceEvent> events, string name)
        {
            for (int i = 0; i < events.Count; i++)
            {
                TraceEvent e = events[i];
                if (e.Cat == "game" && e.Name == name) return e;
            }
            return null;
        }

        private sealed class CatAgg
        {
            public string Cat;
            public double Ms;
            public int N;
        }

        private sealed class SinkAgg
        {
            public string Cat;
            public string Key;
            public int N;
            public double Self;
            public double Incl;
        }

        private sealed class HarmonyOwnerAgg
        {
            public string Owner;
            public double Top;
            public int Builds;
            public int Skipped;
            public double BuildMs;
        }

        private sealed class NameCountMs
        {
            public string Name;
            public string Extra;
            public int N;
            public double Ms;
        }

        private sealed class GameAgg
        {
            public string Name;
            public int N;
            public double Incl;
            public double Body;
        }

        public static string Summary(List<TraceEvent> events, double endMs, string endLabel, string header)
        {
            Dictionary<TraceEvent, double> self = SelfTimes(events, out double accountedMs);
            var sb = new StringBuilder();
            sb.AppendLine(header);
            double pct = endMs > 0 ? accountedMs * 100 / endMs : 0;
            string unknownNote = StartupTrace.ProcessStartKnown ? "" : " (process start unknown: since Morgott.Preloader.Initialize)";
            sb.Append(endLabel).Append(' ').Append(F(endMs, "F0")).Append(" ms since process start").Append(unknownNote)
              .Append("; main-thread spans cover ").Append(F(accountedMs, "F0")).Append(" ms (").Append(F(pct, "F0"))
              .AppendLine("%), rest = Unity/native work, frames and waits between hooked calls");

            TraceEvent menu = Game(events, "FejdStartup.Start");
            TraceEvent request = Game(events, "FejdStartup.LoadMainScene");
            TraceEvent spawn = null;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].Cat == "mark" && events[i].Name == GameLifecycleProbe.WorldReadyMark)
                {
                    spawn = events[i];
                    break;
                }
            }
            if (request != null && spawn != null)
            {
                double menuMs = menu != null ? StartupTrace.ToMs(menu.End) : -1;
                double worldMs = StartupTrace.DurMs(request.Start, spawn.Start);
                double waitMs = menu != null ? StartupTrace.DurMs(menu.End, request.Start) : -1;
                sb.Append("menu ready ").Append(F(menuMs, "F0")).Append(" ms; world load ").Append(F(worldMs, "F0"))
                  .Append(" ms (FejdStartup.LoadMainScene start -> first spawn), time in the menu before it ")
                  .Append(F(waitMs, "F0")).AppendLine(" ms (user input, not startup cost)");
                sb.AppendLine("  Self-time tables below include the menu wait (idle frames are not spans, so it adds little).");
            }
            sb.AppendLine();
            WorldLoad(sb, events, self, request);

            sb.AppendLine("== Lifecycle marks (ms since process start; GC counts gen0/1/2; managed heap) ==");
            var marks = new List<TraceEvent>();
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].Ph == 'i') marks.Add(events[i]);
            }
            marks.Sort(CompareStartAsc);
            for (int i = 0; i < marks.Count; i++)
            {
                TraceEvent m = marks[i];
                sb.Append(PadF(StartupTrace.ToMs(m.Start), 9, "F0")).Append("  ").Append(m.Name);
                if (m.Detail != null) sb.Append(" (").Append(m.Detail).Append(')');
                sb.Append("   [").Append(m.Args?.Replace("\"", "")).AppendLine("]");
            }
            sb.AppendLine();

            sb.AppendLine("== Self time by category (ms; nested spans subtracted, no double counting) ==");
            var catMap = new Dictionary<string, CatAgg>();
            foreach (KeyValuePair<TraceEvent, double> kv in self)
            {
                if (!catMap.TryGetValue(kv.Key.Cat, out CatAgg agg))
                    catMap[kv.Key.Cat] = agg = new CatAgg { Cat = kv.Key.Cat };
                agg.Ms += kv.Value;
                agg.N++;
            }
            var catList = new List<CatAgg>(catMap.Values);
            catList.Sort((a, b) => b.Ms.CompareTo(a.Ms));
            for (int i = 0; i < catList.Count; i++)
            {
                CatAgg g = catList[i];
                sb.Append(PadF(g.Ms, 10, "F1")).Append("  ").Append(PadR(g.Cat, 10)).Append(' ').Append(PadI(g.N, 6)).AppendLine(" events");
            }
            sb.AppendLine("  game = mods' patches on hooked game methods (outer minus body); game.body = original body (plus any");
            sb.AppendLine("  other mod's Priority.Last prefix / Priority.First postfix registered after ours: upper bound);");
            sb.AppendLine("  plugin = plugin cctor+Awake not covered by a nested span; lifecycle = rest of that phase.");
            sb.AppendLine();

            sb.Append("== Top ").Append(TopN.ToString(Inv)).AppendLine(" time sinks by self time (category, key, calls, self ms, inclusive ms) ==");
            var sinkMap = new Dictionary<string, SinkAgg>();
            foreach (KeyValuePair<TraceEvent, double> kv in self)
            {
                string key = Key(kv.Key);
                string mapKey = kv.Key.Cat + "|" + key;
                if (!sinkMap.TryGetValue(mapKey, out SinkAgg agg))
                    sinkMap[mapKey] = agg = new SinkAgg { Cat = kv.Key.Cat, Key = key };
                agg.N++;
                agg.Self += kv.Value;
                agg.Incl += Dur(kv.Key);
            }
            var sinkList = new List<SinkAgg>(sinkMap.Values);
            sinkList.Sort((a, b) => b.Self.CompareTo(a.Self));
            int sinkTake = Math.Min(TopN, sinkList.Count);
            for (int i = 0; i < sinkTake; i++)
            {
                SinkAgg g = sinkList[i];
                sb.Append(PadF(g.Self, 10, "F1")).Append(' ').Append(PadF(g.Incl, 10, "F1")).Append("  ")
                  .Append(PadR(g.Cat, 10)).Append(' ').Append(PadI(g.N, 5)).Append("x  ").AppendLine(g.Key);
            }
            sb.AppendLine();

            var plugins = new List<TraceEvent>();
            double pluginSum = 0;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].Cat == "plugin")
                {
                    plugins.Add(events[i]);
                    pluginSum += Dur(events[i]);
                }
            }
            sb.Append("== Plugins: cctor + Awake inclusive (ms), ").Append(plugins.Count.ToString(Inv))
              .Append(" plugins, total ").Append(F(pluginSum, "F0")).AppendLine(" ms ==");
            plugins.Sort(CompareDurDesc);
            int pluginTake = Math.Min(TopN, plugins.Count);
            for (int i = 0; i < pluginTake; i++)
            {
                TraceEvent e = plugins[i];
                sb.Append(PadF(Dur(e), 10, "F1")).Append("  ").Append(e.Name).Append("  (").Append(e.Detail2).AppendLine(")");
            }
            sb.AppendLine();

            var harmonyOwnerMap = new Dictionary<string, HarmonyOwnerAgg>();
            var wrapperMap = new Dictionary<string, NameCountMs>();
            for (int i = 0; i < events.Count; i++)
            {
                TraceEvent e = events[i];
                if (e.Cat != "harmony") continue;
                string owner = e.Detail ?? "?";
                if (!harmonyOwnerMap.TryGetValue(owner, out HarmonyOwnerAgg h))
                    harmonyOwnerMap[owner] = h = new HarmonyOwnerAgg { Owner = owner };
                double dur = Dur(e);
                if (e.Depth == 1) h.Top += dur;
                if (e.Name == "UpdateWrapper")
                {
                    h.Builds++;
                    h.BuildMs += dur;
                    string target = e.Detail2 ?? "?";
                    if (!wrapperMap.TryGetValue(target, out NameCountMs w))
                        wrapperMap[target] = w = new NameCountMs { Name = target };
                    w.N++;
                    w.Ms += dur;
                }
                else if (e.Name == "UpdateWrapper (skipped)")
                {
                    h.Skipped++;
                    h.BuildMs += dur;
                }
                else if (e.Name.StartsWith("UpdateWrapper", StringComparison.Ordinal))
                {
                    h.BuildMs += dur;
                }
            }
            sb.AppendLine("== Harmony by owner: outermost API calls inclusive (ms), wrapper builds (count, ms) ==");
            var harmonyOwners = new List<HarmonyOwnerAgg>(harmonyOwnerMap.Values);
            harmonyOwners.Sort((a, b) => b.Top.CompareTo(a.Top));
            int hoTake = Math.Min(TopN, harmonyOwners.Count);
            for (int i = 0; i < hoTake; i++)
            {
                HarmonyOwnerAgg g = harmonyOwners[i];
                sb.Append(PadF(g.Top, 10, "F1")).Append("  ").Append(PadI(g.Builds, 5)).Append(" builds ")
                  .Append(PadF(g.BuildMs, 8, "F1")).Append(" ms  ").Append(PadI(g.Skipped, 4)).Append(" skipped  ").AppendLine(g.Owner);
            }
            sb.AppendLine("  Top originals by wrapper build time (ms, builds):");
            var wrappers = new List<NameCountMs>(wrapperMap.Values);
            wrappers.Sort((a, b) => b.Ms.CompareTo(a.Ms));
            int wTake = Math.Min(15, wrappers.Count);
            for (int i = 0; i < wTake; i++)
            {
                NameCountMs g = wrappers[i];
                sb.Append(PadF(g.Ms, 10, "F1")).Append(' ').Append(PadI(g.N, 4)).Append("x  ").AppendLine(g.Name);
            }
            sb.AppendLine();

            var bundles = new List<TraceEvent>();
            var asyncBundles = new List<TraceEvent>();
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].Cat == "bundle") bundles.Add(events[i]);
                else if (events[i].Cat == "bundle.async") asyncBundles.Add(events[i]);
            }
            // A cache hit loads its copy from inside the hooked stream load: count only outermost spans.
            int outerCount = 0;
            double outerSum = 0;
            for (int i = 0; i < bundles.Count; i++)
            {
                TraceEvent e = bundles[i];
                bool nested = false;
                for (int j = 0; j < bundles.Count; j++)
                {
                    TraceEvent p = bundles[j];
                    if (p != e && p.Tid == e.Tid && p.Start <= e.Start && p.End >= e.End && (p.Start < e.Start || p.End > e.End))
                    {
                        nested = true;
                        break;
                    }
                }
                if (!nested)
                {
                    outerCount++;
                    outerSum += Dur(e);
                }
            }
            sb.Append("== AssetBundle loads: ").Append(bundles.Count.ToString(Inv)).Append(" calls (")
              .Append(outerCount.ToString(Inv)).Append(" outermost), ").Append(F(outerSum, "F0")).AppendLine(" ms on calling threads ==");
            bundles.Sort(CompareDurDesc);
            int bTake = Math.Min(TopN, bundles.Count);
            for (int i = 0; i < bTake; i++)
            {
                TraceEvent e = bundles[i];
                sb.Append(PadF(Dur(e), 10, "F1")).Append("  ").Append(PadR(e.Detail2, 20)).Append(' ')
                  .Append(PadF(e.Bytes / (1024.0 * 1024.0), 8, "F1")).Append(" MB  ").AppendLine(e.Detail ?? e.Name);
            }
            if (asyncBundles.Count > 0)
            {
                double maxAsync = double.MinValue;
                for (int i = 0; i < asyncBundles.Count; i++)
                {
                    double d = Dur(asyncBundles[i]);
                    if (d > maxAsync) maxAsync = d;
                }
                sb.Append("  async request -> completed: ").Append(asyncBundles.Count.ToString(Inv))
                  .Append(" loads, longest ").Append(F(maxAsync, "F0")).AppendLine(" ms");
            }
            sb.AppendLine();

            sb.AppendLine("== Game methods: inclusive (ms) = body + mods' patches (body is an upper bound for vanilla) ==");
            var bodySumByName = new Dictionary<string, double>();
            var gameMap = new Dictionary<string, GameAgg>();
            var patchMap = new Dictionary<string, NameCountMs>();
            var jotunnMap = new Dictionary<string, NameCountMs>();
            var scenes = new List<TraceEvent>();
            var locations = new List<TraceEvent>();
            for (int i = 0; i < events.Count; i++)
            {
                TraceEvent e = events[i];
                switch (e.Cat)
                {
                    case "game.body":
                        bodySumByName.TryGetValue(e.Name, out double bs);
                        bodySumByName[e.Name] = bs + Dur(e);
                        break;
                    case "game":
                        if (!gameMap.TryGetValue(e.Name, out GameAgg ga))
                            gameMap[e.Name] = ga = new GameAgg { Name = e.Name };
                        ga.N++;
                        ga.Incl += Dur(e);
                        break;
                    case "game.patch":
                        string pk = e.Detail2 + "|" + e.Name;
                        if (!patchMap.TryGetValue(pk, out NameCountMs pa))
                            patchMap[pk] = pa = new NameCountMs { Name = e.Detail2, Extra = e.Name };
                        pa.N++;
                        pa.Ms += Dur(e);
                        break;
                    case "jotunn":
                        if (!jotunnMap.TryGetValue(e.Name, out NameCountMs ja))
                            jotunnMap[e.Name] = ja = new NameCountMs { Name = e.Name };
                        ja.N++;
                        ja.Ms += Dur(e);
                        break;
                    case "scene":
                        scenes.Add(e);
                        break;
                    case "worldgen":
                        locations.Add(e);
                        break;
                }
            }
            var gameList = new List<GameAgg>(gameMap.Values);
            for (int i = 0; i < gameList.Count; i++)
            {
                bodySumByName.TryGetValue(gameList[i].Name, out gameList[i].Body);
            }
            gameList.Sort((a, b) => b.Incl.CompareTo(a.Incl));
            for (int i = 0; i < gameList.Count; i++)
            {
                GameAgg g = gameList[i];
                sb.Append(PadF(g.Incl, 10, "F1")).Append("  body ").Append(PadF(g.Body, 8, "F1"))
                  .Append("  mods ").Append(PadF(g.Incl - g.Body, 8, "F1")).Append("  ")
                  .Append(PadI(g.N, 3)).Append("x  ").AppendLine(g.Name);
            }
            sb.AppendLine("  Mods' patches by owner (game.patch, inclusive ms, calls; transpilers count as body):");
            var patchList = new List<NameCountMs>(patchMap.Values);
            patchList.Sort((a, b) => b.Ms.CompareTo(a.Ms));
            int pTake = Math.Min(TopN, patchList.Count);
            for (int i = 0; i < pTake; i++)
            {
                NameCountMs g = patchList[i];
                sb.Append(PadF(g.Ms, 10, "F1")).Append(' ').Append(PadI(g.N, 4)).Append("x  ")
                  .Append(PadR(g.Name, 28)).Append(' ').AppendLine(g.Extra);
            }
            var jotunnList = new List<NameCountMs>(jotunnMap.Values);
            jotunnList.Sort((a, b) => b.Ms.CompareTo(a.Ms));
            int jTake = Math.Min(TopN, jotunnList.Count);
            for (int i = 0; i < jTake; i++)
            {
                NameCountMs g = jotunnList[i];
                sb.Append(PadF(g.Ms, 10, "F1")).Append(' ').Append(PadI(g.N, 4)).Append("x  jotunn ").AppendLine(g.Name);
            }
            for (int i = 0; i < scenes.Count; i++)
            {
                TraceEvent e = scenes[i];
                sb.Append(PadF(Dur(e), 10, "F1")).Append("  scene ").Append(e.Name).Append(" (").Append(e.Detail).AppendLine(")");
            }

            // WorldGen per-location spans (own lane, not in self time).
            if (locations.Count > 0)
            {
                long minStart = long.MaxValue;
                long maxEnd = long.MinValue;
                for (int i = 0; i < locations.Count; i++)
                {
                    if (locations[i].Start < minStart) minStart = locations[i].Start;
                    if (locations[i].End > maxEnd) maxEnd = locations[i].End;
                }
                sb.AppendLine();
                sb.Append("== Location generation: ").Append(locations.Count.ToString(Inv))
                  .Append(" location types, first start -> last end ").Append(F(StartupTrace.DurMs(minStart, maxEnd), "F0"))
                  .Append(" ms (wall ms, longest ").Append(TopN.ToString(Inv)).AppendLine(") ==");
                locations.Sort(CompareDurDesc);
                int lTake = Math.Min(TopN, locations.Count);
                for (int i = 0; i < lTake; i++)
                {
                    TraceEvent e = locations[i];
                    sb.Append(PadF(Dur(e), 10, "F1")).Append("  ").Append(e.Name).Append("  (").Append(e.Detail).AppendLine(")");
                }
            }
            return sb.ToString();
        }

        /// <summary>World-load wall clock (click -> loading screen -> ZoneSystem.Start) and the spans inside that
        /// window ranked by self time, mod patches and Jotunn handlers named by owner.</summary>
        private static void WorldLoad(StringBuilder sb, List<TraceEvent> events, Dictionary<TraceEvent, double> self, TraceEvent request)
        {
            var wall = new List<TraceEvent>();
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].Cat == "worldload") wall.Add(events[i]);
            }
            wall.Sort(CompareNameAsc);
            long from, to;
            if (wall.Count > 0)
            {
                from = long.MaxValue;
                to = long.MinValue;
                for (int i = 0; i < wall.Count; i++)
                {
                    if (wall[i].Start < from) from = wall[i].Start;
                    if (wall[i].End > to) to = wall[i].End;
                }
            }
            else
            {
                TraceEvent zone = null;
                if (request != null)
                {
                    for (int i = 0; i < events.Count; i++)
                    {
                        TraceEvent e = events[i];
                        if (e.Cat == "game" && e.Name == "ZoneSystem.Start" && e.Start > request.Start)
                        {
                            zone = e;
                            break;
                        }
                    }
                }
                if (request == null || zone == null) return;
                from = request.Start;
                to = zone.End;
            }
            sb.AppendLine("== World load wall clock (ms; click = FejdStartup.OnWorldStart start) ==");
            for (int i = 0; i < wall.Count; i++)
            {
                TraceEvent e = wall[i];
                sb.Append(PadF(Dur(e), 10, "F1")).Append("  ").Append(e.Name);
                if (e.Detail != null) sb.Append("  (").Append(e.Detail).Append(')');
                sb.AppendLine();
            }
            if (wall.Count == 0) sb.AppendLine("  (no rendered-frame mark: window = LoadMainScene start -> ZoneSystem.Start end)");

            double inWindowSum = 0;
            var culpritMap = new Dictionary<string, SinkAgg>();
            foreach (KeyValuePair<TraceEvent, double> kv in self)
            {
                TraceEvent e = kv.Key;
                if (e.Start < from || e.End > to || e.Tid != StartupTrace.MainThreadId) continue;
                inWindowSum += kv.Value;
                string what = Culprit(e);
                string mapKey = e.Cat + "|" + what;
                if (!culpritMap.TryGetValue(mapKey, out SinkAgg agg))
                    culpritMap[mapKey] = agg = new SinkAgg { Cat = e.Cat, Key = what };
                agg.N++;
                agg.Self += kv.Value;
                agg.Incl += Dur(e);
            }
            sb.Append("== World load culprits: top ").Append(TopN.ToString(Inv)).Append(" by self time in that window (")
              .Append(F(StartupTrace.DurMs(from, to), "F0")).Append(" ms, ").Append(F(inWindowSum, "F0"))
              .AppendLine(" ms in hooked main-thread spans) ==");
            sb.AppendLine("      self       incl  calls  category    what (owner [assembly] :: method @ hooked target)");
            var culprits = new List<SinkAgg>(culpritMap.Values);
            culprits.Sort((a, b) => b.Self.CompareTo(a.Self));
            int take = Math.Min(TopN, culprits.Count);
            for (int i = 0; i < take; i++)
            {
                SinkAgg g = culprits[i];
                sb.Append(PadF(g.Self, 10, "F1")).Append(' ').Append(PadF(g.Incl, 10, "F1")).Append(' ')
                  .Append(PadI(g.N, 6)).Append("x  ").Append(PadR(g.Cat, 10)).Append("  ").AppendLine(g.Key);
            }
            sb.AppendLine("  game.patch = one mod prefix/postfix/finalizer or Jotunn event handler (needs TimeModPatches); game = that");
            sb.AppendLine("  method's patches not timed one by one; game.body = vanilla body; jotunn = Jotunn's own registration step.");
            sb.AppendLine();
        }

        private static string Culprit(TraceEvent e) => e.Cat switch
        {
            "game.patch" => e.Name + " :: " + e.Detail + " @ " + e.Detail2,
            "game.body" => "vanilla " + e.Name,
            "game" => "untimed mod patches on " + e.Name,
            _ => Key(e),
        };

        /// <summary>One log line: end time (menu ready / first spawn) plus the three largest self-time sinks.</summary>
        public static string OneLine(List<TraceEvent> events, double endMs, string endLabel)
        {
            Dictionary<TraceEvent, double> self = SelfTimes(events, out double accountedMs);
            var map = new Dictionary<string, NameCountMs>();
            foreach (KeyValuePair<TraceEvent, double> kv in self)
            {
                string key = kv.Key.Cat + ":" + Key(kv.Key);
                if (!map.TryGetValue(key, out NameCountMs agg))
                    map[key] = agg = new NameCountMs { Name = key };
                agg.Ms += kv.Value;
            }
            var list = new List<NameCountMs>(map.Values);
            list.Sort((a, b) => b.Ms.CompareTo(a.Ms));
            var sb = new StringBuilder();
            sb.Append(endLabel).Append(' ').Append(F(endMs / 1000, "F1")).Append(" s, ")
              .Append(F(accountedMs / 1000, "F1")).Append(" s in hooked spans; top: ");
            int take = Math.Min(3, list.Count);
            for (int i = 0; i < take; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(list[i].Name).Append(' ').Append(F(list[i].Ms, "F0")).Append(" ms");
            }
            return sb.ToString();
        }

        private static string F(double v, string fmt) => v.ToString(fmt, Inv);

        private static string PadF(double v, int width, string fmt) => v.ToString(fmt, Inv).PadLeft(width);

        private static string PadI(int v, int width) => v.ToString(Inv).PadLeft(width);

        private static string PadR(string s, int width) => (s ?? "").PadRight(width);

        private static string Us(double ms) => (ms * 1000).ToString("F1", Inv);

        private static StringBuilder Sep(StringBuilder sb, ref bool first)
        {
            if (!first) sb.Append(',');
            first = false;
            return sb;
        }

        private static void Arg(StringBuilder sb, ref bool first, string key, string value)
        {
            if (value == null) return;
            Sep(sb, ref first).Append('"').Append(key).Append("\":");
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
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
