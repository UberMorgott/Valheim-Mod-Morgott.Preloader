using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using Morgott.Preloader.Core;
using HarmonyLib;

namespace Morgott.Preloader.Profiling
{
    /// <summary>
    /// BepInEx chainloader lifecycle + per-plugin load cost. <c>Chainloader.Start</c> logs
    /// <c>Loading [{PluginInfo}]</c> (pack 5.4.2351+: <c>Loading [{PluginInfo}] ({GUID})</c>) right before <c>Assembly.LoadFile</c> + <c>ManagerObject.AddComponent(type)</c>
    /// (BepInEx.dll decompile, Chainloader.Start lines 6243-6249); AddComponent runs the plugin's static constructor
    /// and <c>Awake</c> synchronously. A log listener (BepInEx dispatches log events synchronously,
    /// <c>SendLogEvent</c> line 2778) turns those lines into spans: one plugin = its Loading line to the next one
    /// (or chainloader end), keyed by GUID. No Unity method is patched, so no inlining or early-cctor risk.
    /// </summary>
    internal sealed class PluginLifecycleProfiler : ILogListener
    {
        private const string LoadingPrefix = "Loading [";
        private const string CompleteMessage = "Chainloader startup complete"; // Chainloader.Start line 6280

        private static readonly PluginLifecycleProfiler Instance = new PluginLifecycleProfiler();
        private static readonly List<TraceEvent> Pending = new List<TraceEvent>();
        private static Harmony _harmony;
        private static long _currentStart;
        private static string _currentName;
        private static string _currentGuid;
        private const string GuidSeparator = "] (";

        /// <summary>Raised at the end of Chainloader.Initialize: Unity is up, game assemblies are resolvable.</summary>
        public static event Action ChainloaderInitialized;

        /// <summary>Raised after Chainloader.Start (all plugins loaded and initialized).</summary>
        public static event Action ChainloaderStarted;

        /// <summary>Patcher Finish: only Chainloader.Initialize (BepInEx type, no Unity type initializers run).</summary>
        public static void Install(Harmony harmony)
        {
            _harmony = harmony;
            harmony.Patch(AccessTools.Method(typeof(Chainloader), nameof(Chainloader.Initialize)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(PluginLifecycleProfiler), nameof(InitPrefix)), Priority.First),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(PluginLifecycleProfiler), nameof(InitPostfix)), Priority.Last));
        }

        private static void InitPrefix(out long __state)
        {
            __state = StartupTrace.Now();
            StartupTrace.Mark("Chainloader.Initialize begin");
        }

        private static void InitPostfix(long __state)
        {
            StartupTrace.Complete("lifecycle", "Chainloader.Initialize", __state, StartupTrace.Now());
            StartupTrace.Mark("Chainloader.Initialize end");
            Log.Guard("Chainloader.Start hook", () => _harmony.Patch(AccessTools.Method(typeof(Chainloader), nameof(Chainloader.Start)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(PluginLifecycleProfiler), nameof(StartPrefix)), Priority.First),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(PluginLifecycleProfiler), nameof(StartFinalizer)), Priority.Last)));
            Log.Guard("ChainloaderInitialized handlers", () => ChainloaderInitialized?.Invoke());
        }

        private static void StartPrefix(out long __state)
        {
            StartupTrace.Mark("Chainloader.Start begin");
            Log.Guard("Plugin profiler attach", () => Logger.Listeners.Add(Instance));
            __state = StartupTrace.Now();
        }

        // Finalizer: runs after every postfix (incl. StartupAccelerator's deferred Harmony flush) and even on throw.
        private static void StartFinalizer(long __state)
        {
            long end = StartupTrace.Now();
            Log.Guard("Plugin profiler detach", () =>
            {
                Logger.Listeners.Remove(Instance);
                ClosePlugin(end);
                var guids = new Dictionary<string, string>();
                foreach (KeyValuePair<string, PluginInfo> kv in Chainloader.PluginInfos)
                {
                    guids[kv.Value.ToString()] = kv.Key;
                }
                foreach (TraceEvent e in Pending)
                {
                    if (guids.TryGetValue(e.Detail, out string guid))
                    {
                        e.Name = guid;
                    }
                    StartupTrace.Complete(e.Cat, e.Name, e.Start, e.End, e.Detail, e.Detail2);
                }
                Pending.Clear();
                StartupTrace.Complete("lifecycle", "Chainloader.Start", __state, end, $"{Chainloader.PluginInfos.Count} plugins");
            });
            StartupTrace.Mark("Chainloader.Start end");
            Log.Guard("ChainloaderStarted handlers", () => ChainloaderStarted?.Invoke());
        }

        private static void ClosePlugin(long end)
        {
            if (_currentName != null)
            {
                Pending.Add(new TraceEvent
                {
                    Cat = "plugin",
                    Name = _currentGuid ?? _currentName,
                    Start = _currentStart,
                    End = end,
                    Detail = _currentName,
                    Detail2 = "assembly load + static ctor + Awake",
                });
                _currentName = null;
                _currentGuid = null;
            }
        }

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            if (eventArgs.Source?.SourceName != "BepInEx" || !(eventArgs.Data is string text))
            {
                return;
            }
            if (IsCompleteMessage(text))
            {
                // Logged before Chainloader.Start postfixes run: keeps a deferred Harmony flush out of the last plugin.
                ClosePlugin(StartupTrace.Now());
                return;
            }
            if (!TryParseLoading(text, out string name, out string guid))
            {
                return;
            }
            long now = StartupTrace.Now();
            ClosePlugin(now);
            _currentName = name;
            _currentGuid = guid;
            _currentStart = now;
        }

        // Pack 5.4.2350: "Chainloader startup complete"; 5.4.2351: "... complete (N loaded, N skipped, N failed)".
        internal static bool IsCompleteMessage(string text)
        {
            return text == CompleteMessage
                || (text.StartsWith(CompleteMessage + " (", StringComparison.Ordinal) && text.EndsWith(")", StringComparison.Ordinal));
        }

        // Pack 5.4.2350: "Loading [{PluginInfo}]"; 5.4.2351: "Loading [{PluginInfo}] ({GUID})" (Chainloader.Start).
        // GUID is null for the old format and resolved from Chainloader.PluginInfos at the end of Start.
        internal static bool TryParseLoading(string text, out string name, out string guid)
        {
            name = null;
            guid = null;
            if (!text.StartsWith(LoadingPrefix, StringComparison.Ordinal))
            {
                return false;
            }
            if (text.EndsWith(")", StringComparison.Ordinal))
            {
                int sep = text.LastIndexOf(GuidSeparator, StringComparison.Ordinal);
                if (sep < LoadingPrefix.Length)
                {
                    return false;
                }
                int guidStart = sep + GuidSeparator.Length;
                guid = text.Substring(guidStart, text.Length - guidStart - 1);
                name = text.Substring(LoadingPrefix.Length, sep - LoadingPrefix.Length);
                if (guid.Length == 0)
                {
                    guid = null;
                }
                return true;
            }
            if (text.EndsWith("]", StringComparison.Ordinal))
            {
                name = text.Substring(LoadingPrefix.Length, text.Length - LoadingPrefix.Length - 1);
                return true;
            }
            return false;
        }

        public void Dispose()
        {
        }
    }
}
