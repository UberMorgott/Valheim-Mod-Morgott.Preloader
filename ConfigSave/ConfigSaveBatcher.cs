using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using Morgott.Preloader.Core;
using HarmonyLib;

namespace Morgott.Preloader.ConfigSave
{
    /// <summary>
    /// BepInEx 5.4.23 <c>ConfigFile.Bind</c> and every value change call <c>Save()</c> (whole-file rewrite) while
    /// <c>SaveOnConfigSet</c> is true: ~1130 writes during startup here. From <c>Chainloader.Start</c> until the main menu
    /// BepInEx's own implicit saves (Bind / value change / saveOnInit) only record the file; a mod's explicit
    /// <c>Save()</c> writes at once. Recorded files are written at the end of <c>Chainloader.Start</c> (also when it
    /// throws), at the main menu (deferring stops) and on process exit. <c>SaveOnConfigSet</c> is never touched.
    /// A <c>Reload()</c> of a pending file writes it first, unless the file changed on disk since last seen (an outside
    /// edit): then the reload reads that edit and the file stays pending.
    /// </summary>
    internal static class ConfigSaveBatcher
    {
        private static readonly object Lock = new object();
        private static readonly List<ConfigFile> Pending = new List<ConfigFile>();

        /// <summary>Pending file -> its disk stamp (write time, length) when last seen (first deferral or last reload).</summary>
        private static readonly Dictionary<ConfigFile, (long Ticks, long Length)> Stamps = new Dictionary<ConfigFile, (long, long)>();

        private static bool _active;
        private static int _deferred;
        private static int _explicit;

        /// <summary>Set while this module itself writes a file, so its own Save call is not deferred.</summary>
        [ThreadStatic] private static bool _bypass;

        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Chainloader), nameof(Chainloader.Start)),
                prefix: Hook(nameof(StartPrefix), Priority.First), finalizer: Hook(nameof(StartFinalizer), Priority.Last));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(ConfigFile), nameof(ConfigFile.Save)),
                prefix: Hook(nameof(SavePrefix), Priority.Last), postfix: Hook(nameof(SavePostfix), Priority.Last));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(ConfigFile), nameof(ConfigFile.Reload)),
                prefix: Hook(nameof(ReloadPrefix), Priority.First), postfix: Hook(nameof(ReloadPostfix), Priority.Last));
            Lifecycle.MenuReady += () => Flush("main menu", stop: true);
            AppDomain.CurrentDomain.ProcessExit += (_, __) => Flush("process exit", stop: true);
            AppDomain.CurrentDomain.DomainUnload += (_, __) => Flush("domain unload", stop: true);
        }

        private static HarmonyMethod Hook(string name, int priority) => new HarmonyMethod(AccessTools.Method(typeof(ConfigSaveBatcher), name), priority);

        private static void StartPrefix()
        {
            lock (Lock) _active = true;
        }

        private static void StartFinalizer() => Flush("chainloader end", stop: false);

        private static bool SavePrefix(ConfigFile __instance, bool __runOriginal, out bool __state)
        {
            __state = false;
            if (!__runOriginal || _bypass) return __runOriginal;
            lock (Lock)
            {
                if (!_active) return true;
            }
            // BepInEx's implicit saves are guarded by SaveOnConfigSet, so with it off every call is a mod's own.
            if (!__instance.SaveOnConfigSet || !IsImplicitSave())
            {
                // Explicit Save(): a caller may read the file right after, so it runs now; it also settles a pending
                // deferred write, but only once it succeeded (postfix).
                lock (Lock) _explicit++;
                __state = true;
                return true;
            }
            lock (Lock)
            {
                if (!_active) return true;
                _deferred++;
                if (!Stamps.ContainsKey(__instance))
                {
                    Stamps[__instance] = Stamp(__instance.ConfigFilePath);
                    Pending.Add(__instance);
                }
                return false;
            }
        }

        private static void SavePostfix(ConfigFile __instance, bool __state, bool __runOriginal)
        {
            if (!__state || !__runOriginal) return;
            lock (Lock)
            {
                if (Stamps.Remove(__instance)) Pending.Remove(__instance);
            }
        }

        /// <summary>
        /// True when the running <c>ConfigFile.Save</c> was called by BepInEx itself (<c>Bind&lt;T&gt;</c>,
        /// <c>OnSettingChanged</c> or the constructor's saveOnInit), decided by the direct caller frame, so an explicit
        /// <c>Save()</c> from a <c>SettingChanged</c> handler stays explicit. Harmony / reflection frames are skipped.
        /// Unknown shape = explicit (immediate write).
        /// </summary>
        private static bool IsImplicitSave()
        {
            try
            {
                return ImplicitCaller();
            }
            catch (Exception e)
            {
                Log.WarningOnce("ConfigSaveBatcher.stack", $"ConfigSaveBatcher: caller check failed, saves written at once: {e.Message}");
                return false;
            }
        }

        private static bool ImplicitCaller()
        {
            foreach (StackFrame frame in new StackTrace(1, false).GetFrames() ?? new StackFrame[0])
            {
                MethodBase method = frame.GetMethod();
                // Harmony's patched copy of Save ("DMD<BepInEx.Configuration.ConfigFile::Save>") or a native frame.
                if (method == null || method.Name.IndexOf("ConfigFile::Save", StringComparison.Ordinal) >= 0 ||
                    (method.DeclaringType?.FullName?.IndexOf("ConfigFile::Save", StringComparison.Ordinal) ?? -1) >= 0)
                    continue;
                Type type = method.DeclaringType;
                if (type == null) return false; // unknown frame (e.g. a dynamic handler): explicit
                string ns = type.Namespace ?? "";
                if (type == typeof(ConfigSaveBatcher) || ns.StartsWith("HarmonyLib", StringComparison.Ordinal) ||
                    ns.StartsWith("MonoMod", StringComparison.Ordinal) || ns.StartsWith("System.Reflection", StringComparison.Ordinal) ||
                    (type == typeof(ConfigFile) && method.Name == nameof(ConfigFile.Save)))
                    continue;
                return type == typeof(ConfigFile) &&
                       (method.Name == nameof(ConfigFile.Bind) || method.Name == "OnSettingChanged" || method.Name == ".ctor");
            }
            return false;
        }

        private static void ReloadPrefix(ConfigFile __instance, out (long, long)? __state)
        {
            __state = null;
            lock (Lock)
            {
                if (!_active || !Stamps.TryGetValue(__instance, out (long, long) seen)) return;
                (long, long) now = Stamp(__instance.ConfigFilePath);
                if (!now.Equals(seen))
                {
                    // Edited outside: Reload reads the edit, the merged state is written at the next flush. The stamp
                    // moves only once Reload read it (postfix): if Reload throws, a retry still sees the edit as unread.
                    __state = now;
                    Log.Info($"ConfigSaveBatcher: {Path.GetFileName(__instance.ConfigFilePath)} changed on disk, reloaded before its deferred save");
                    return;
                }
                Stamps.Remove(__instance);
                Pending.Remove(__instance);
            }
            SaveNow(__instance);
        }

        /// <summary>Runs only when Reload ran and returned normally, i.e. the external edit was read.</summary>
        private static void ReloadPostfix(ConfigFile __instance, (long, long)? __state, bool __runOriginal)
        {
            if (__state == null || !__runOriginal) return;
            lock (Lock)
            {
                if (Stamps.ContainsKey(__instance)) Stamps[__instance] = __state.Value;
            }
        }

        /// <summary>Writes every pending file once (in first-save order); with <paramref name="stop"/> deferring ends.</summary>
        private static void Flush(string reason, bool stop)
        {
            ConfigFile[] files;
            int deferred, explicitSaves;
            lock (Lock)
            {
                if (!_active) return;
                _active = !stop;
                files = Pending.ToArray();
                deferred = _deferred;
                explicitSaves = _explicit;
                _deferred = _explicit = 0;
                Pending.Clear();
                Stamps.Clear();
            }
            Stopwatch watch = Stopwatch.StartNew();
            int failed = files.Count(file => !SaveNow(file));
            Log.Info($"ConfigSaveBatcher: {deferred} saves deferred -> {files.Length} file writes at {reason} " +
                     $"in {watch.ElapsedMilliseconds} ms, {explicitSaves} explicit saves written at once" + (failed > 0 ? $", {failed} failed" : ""));
        }

        /// <summary>Runs the real Save now. A failure is logged here: the plugin whose Bind triggered it has moved on.</summary>
        private static bool SaveNow(ConfigFile file)
        {
            try
            {
                _bypass = true;
                file.Save();
                return true;
            }
            catch (Exception e)
            {
                Log.Error($"ConfigSaveBatcher: writing {Path.GetFileName(file.ConfigFilePath)} failed: {e}");
                return false;
            }
            finally
            {
                _bypass = false;
            }
        }

        /// <summary>Last write time + length of a file (both 0 when it does not exist).</summary>
        private static (long Ticks, long Length) Stamp(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : (0L, 0L);
        }
    }
}
