using System;
using System.IO;
using System.Reflection;
using Morgott.Preloader.Core;
using HarmonyLib;
using UnityEngine;

namespace Morgott.Preloader.Profiling
{
    /// <summary>
    /// AssetBundle load timing. Every public <c>AssetBundle.LoadFrom*</c> overload funnels into one of six internal
    /// statics (UnityEngine.AssetBundleModule, decompiled lines 292/338/383/407/482/489); those are hooked with a
    /// First prefix and a Last postfix so the span also covers other patchers' prefixes (e.g. a bundle cache
    /// hashing the file). Sync loads = main-thread span; async loads = request-creation span on the calling thread
    /// plus a request-to-completed span on <see cref="StartupTrace.LaneAsyncBundles"/>.
    /// </summary>
    internal static class BundleProfiler
    {
        public static void Install(Harmony harmony)
        {
            Hook(harmony, "LoadFromFile_Internal", nameof(FilePostfix));
            Hook(harmony, "LoadFromFileAsync_Internal", nameof(FileAsyncPostfix));
            Hook(harmony, "LoadFromMemory_Internal", nameof(MemoryPostfix));
            Hook(harmony, "LoadFromMemoryAsync_Internal", nameof(MemoryAsyncPostfix));
            Hook(harmony, "LoadFromStreamInternal", nameof(StreamPostfix));
            Hook(harmony, "LoadFromStreamAsyncInternal", nameof(StreamAsyncPostfix));
        }

        private static void Hook(Harmony harmony, string target, string postfix)
        {
            MethodInfo original = AccessTools.DeclaredMethod(typeof(AssetBundle), target);
            if (original == null)
            {
                Log.Warning($"Bundle profiler: AssetBundle.{target} not found, skipped");
                return;
            }
            harmony.Patch(original,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(BundleProfiler), nameof(Prefix)), Priority.First),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(BundleProfiler), postfix), Priority.Last));
        }

        private static void Prefix(out long __state) => __state = StartupTrace.Recording ? StartupTrace.Now() : 0;

        private static void FilePostfix(long __state, string path) => Record(__state, "LoadFromFile", path, () => FileSize(path), null);

        private static void FileAsyncPostfix(long __state, string path, AssetBundleCreateRequest __result) =>
            Record(__state, "LoadFromFileAsync", path, () => FileSize(path), __result);

        private static void MemoryPostfix(long __state, byte[] binary) => Record(__state, "LoadFromMemory", null, () => binary?.LongLength ?? -1, null);

        private static void MemoryAsyncPostfix(long __state, byte[] binary, AssetBundleCreateRequest __result) =>
            Record(__state, "LoadFromMemoryAsync", null, () => binary?.LongLength ?? -1, __result);

        private static void StreamPostfix(long __state, Stream stream) => Record(__state, "LoadFromStream", (stream as FileStream)?.Name, () => StreamSize(stream), null);

        private static void StreamAsyncPostfix(long __state, Stream stream, AssetBundleCreateRequest __result) =>
            Record(__state, "LoadFromStreamAsync", (stream as FileStream)?.Name, () => StreamSize(stream), __result);

        /// <summary>Span start..now (0 = not recording); the size is read after the end time, only for a recorded load.</summary>
        private static void Record(long start, string kind, string path, Func<long> bytes, AssetBundleCreateRequest request)
        {
            if (start == 0) return;
            long end = StartupTrace.Now();
            try
            {
                string name = path != null ? Path.GetFileName(path) : "<" + kind + ">";
                long size = bytes();
                StartupTrace.Complete("bundle", name, start, end, path, kind, size);
                if (request != null)
                {
                    request.completed += _ => StartupTrace.Complete("bundle.async", name, start, StartupTrace.Now(),
                        path, kind + " (request -> completed)", size, StartupTrace.LaneAsyncBundles);
                }
            }
            catch (Exception e)
            {
                Log.Error($"Bundle profiler: {e}");
            }
        }
        private static long FileSize(string path)
        {
            try
            {
                return path != null && File.Exists(path) ? new FileInfo(path).Length : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static long StreamSize(Stream stream)
        {
            try
            {
                return stream != null && stream.CanSeek ? stream.Length : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }
}
