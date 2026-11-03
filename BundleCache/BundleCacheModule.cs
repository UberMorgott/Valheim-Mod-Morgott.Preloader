using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx;
using Morgott.Preloader.Core;
using HarmonyLib;
using UnityEngine;

namespace Morgott.Preloader.BundleCache;

/// <summary>
/// Serves LZMA bundles that mods embed as manifest resources from an LZ4 copy on disk. The profile shows every
/// mod bundle arriving through <c>AssetBundle.LoadFromStream</c> on a resource stream (31 loads, 18.8 s, all
/// LZMA); the vanilla SoftRef file loads are already LZ4 and file/memory loads by mods do not occur, so only
/// the two internal stream statics every public overload funnels into are hooked
/// (UnityEngine.AssetBundleModule decompile lines 482/489).
/// Hit: <c>AssetBundle.LoadFromFile(Async)</c> of the copy (LZ4 is read chunk-wise on demand instead of
/// decompressing the whole LZMA stream). Miss: the original load runs untouched; the source is queued and,
/// after the main menu, copied off the main thread and recompressed with
/// <c>AssetBundle.RecompressAssetBundleAsync(.., BuildCompression.LZ4Runtime, .., ThreadPriority.Low)</c>
/// (AssetBundleModule line 925, CoreModule BuildCompression), one bundle at a time. Any failure = original load.
/// </summary>
internal static class BundleCacheModule
{
    private const string LogKey = "BundleCache.";

    private static readonly List<ResourceSource> Pending = new();
    private static readonly HashSet<string> Queued = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ResourceSource> Parked = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Stopwatch PopulateTime = new();
    private static Harmony _harmony;
    private static CacheStore _store;
    private static volatile bool _active;
    private static SynchronizationContext _main;
    private static HashSet<Guid> _loadedMvids;
    private static bool _menuReady;
    private static bool _running;
    private static bool _maintained;
    private static bool _eagerOff;
    private static List<string> _upgrades;

    private static int _hits;
    private static long _hitBytes;
    private static int _misses;
    private static int _alreadyFast;
    private static int _passedThrough;
    private static int _hitFailures;
    private static int _eagerHits;
    private static int _upgraded;
    private static int _created;
    private static long _createdBytes;

    /// <summary>BepInEx\Morgott.Preloader\cache\bundles (the per-Unity-version directory lives below it).</summary>
    public static string CacheRoot => Path.GetFullPath(Path.Combine(Paths.BepInExRootPath, "Morgott.Preloader", "cache", "bundles"));

    public static void Install(Harmony harmony)
    {
        _harmony = harmony;
        Lifecycle.ChainloaderInitialized += () => Log.Guard("BundleCache install", Activate);
        Lifecycle.MenuReady += () => Log.Guard("BundleCache menu ready", OnMenuReady);
    }

    private static void Activate()
    {
        if (!ResourceResolver.Available)
        {
            Log.Warning("BundleCache: Mono resource stream internals not found (runtime changed?), cache disabled");
            return;
        }
        _store = new CacheStore(CacheRoot, Application.unityVersion);
        if (EagerBuilder.Started && !EagerBuilder.Matches(_store.Dir))
        {
            Log.Warning($"BundleCache: the eager build guessed another cache directory than Unity's {_store.Dir}; its copies are not used");
            _eagerOff = true;
        }
        foreach ((string target, string prefix) in new[] { ("LoadFromStreamInternal", nameof(StreamPrefix)), ("LoadFromStreamAsyncInternal", nameof(StreamAsyncPrefix)) })
        {
            MethodInfo original = AccessTools.DeclaredMethod(typeof(AssetBundle), target);
            if (original == null)
            {
                Log.Warning($"BundleCache: AssetBundle.{target} not found (Unity changed?), not cached");
                continue;
            }
            _harmony.Patch(original, prefix: new HarmonyMethod(AccessTools.Method(typeof(BundleCacheModule), prefix)));
        }
        _active = true;
        Log.Info($"BundleCache: active, cache {_store.Dir}");
    }

    private static bool StreamPrefix(Stream stream, uint crc, ref AssetBundle __result, bool __runOriginal) =>
        Serve(__runOriginal, stream, crc, ref __result, "LoadFromFile", path => AssetBundle.LoadFromFile(path), null);

    // A request that completes without a bundle cannot be swapped for the original any more (the caller holds it);
    // the copy is dropped so the next launch loads the original and rebuilds it.
    private static bool StreamAsyncPrefix(Stream stream, uint crc, ref AssetBundleCreateRequest __result, bool __runOriginal) =>
        Serve(__runOriginal, stream, crc, ref __result, "LoadFromFileAsync", path => AssetBundle.LoadFromFileAsync(path), WatchAsync);

    private static void WatchAsync(Hit hit, AssetBundleCreateRequest request) => request.completed += _ => Log.Guard("BundleCache async hit", () =>
    {
        if (request.assetBundle == null)
        {
            Invalidate(hit, "LoadFromFileAsync completed without a bundle", fallback: false);
        }
    });

    /// <summary>Prefix body: false (skip the original) when the cached copy was handed out as the result.</summary>
    private static bool Serve<T>(bool runOriginal, Stream stream, uint crc, ref T result, string what, Func<string, T> load, Action<Hit, T> served) where T : class
    {
        if (!runOriginal)
        {
            return false; // an earlier prefix already produced the result
        }
        Hit hit = Lookup(stream, crc);
        if (hit == null)
        {
            return true;
        }
        T loaded = null;
        string error = what + " returned null";
        try
        {
            loaded = load(hit.Path);
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
        }
        if (loaded == null || (loaded is UnityEngine.Object unityObject && unityObject == null)) // Unity's null for a dead bundle
        {
            // Vanilla behaviour: the original stream load runs (this prefix returns true), the copy is dropped.
            Invalidate(hit, error, fallback: true);
            return true;
        }
        served?.Invoke(hit, loaded);
        result = loaded;
        return false;
    }

    /// <summary>A served copy that Unity could not load: never served again (local copy deleted) and queued for a rebuild from the original.</summary>
    private static void Invalidate(Hit hit, string error, bool fallback)
    {
        _hitFailures++;
        Log.WarningOnce(LogKey + "hitFailed." + hit.Source.FileName,
            $"BundleCache: cached copy of {hit.Source.Label} ({Path.GetFileName(hit.Path)}) did not load ({error}), " +
            (fallback ? "loading the original instead" : "the original is loaded from the next launch on") + "; copy invalidated");
        EagerBuilder.Release(hit.Source.FileName);
        CacheStore.TryDelete(hit.Path);
        lock (Pending)
        {
            Queued.Remove(hit.Source.FileName);
            Parked.Remove(hit.Source.FileName);
        }
        Enqueue(hit.Source);
    }

    private sealed class Hit(string path, ResourceSource source)
    {
        public readonly string Path = path;
        public readonly ResourceSource Source = source;
    }

    /// <summary>Cached copy on a hit; null = load the original (and queue it when it is LZMA).</summary>
    private static Hit Lookup(Stream stream, uint crc)
    {
        if (!_active || crc != 0)
        {
            return null; // a CRC check is the caller's integrity contract on the original bytes: leave it alone
        }
        try
        {
            ResourceSource source = ResourceResolver.Resolve(stream);
            if (source == null)
            {
                _passedThrough++;
                Log.InfoOnce(LogKey + "type." + stream?.GetType().FullName,
                    $"BundleCache: {stream?.GetType().FullName ?? "null"} stream is not an embedded resource, loaded uncached");
                return null;
            }
            string path = _store.PathOf(source);
            bool exists = File.Exists(path);
            // Checked on every hit (header + block table, ~KB): an async hit cannot fall back once the
            // original load is suppressed, so a truncated or foreign copy must be rejected here.
            bool eager = !exists && !_eagerOff && EagerBuilder.TryWait(source.FileName);
            if ((exists || eager) && BundleHeader.IsCompleteCopy(path))
            {
                _store.MarkUsed(source.FileName);
                _hits++;
                _eagerHits += eager ? 1 : 0;
                _hitBytes += source.Length;
                return new Hit(path, source);
            }
            if (exists)
            {
                Log.WarningOnce(LogKey + "invalid." + source.FileName, $"BundleCache: cached copy of {source.Label} is incomplete, rebuilding it");
                CacheStore.TryDelete(path);
            }
            switch (BundleHeader.Classify(stream))
            {
                case BundleCompression.Lzma:
                    _misses++;
                    Enqueue(source);
                    break;
                case BundleCompression.NotLzma:
                    _alreadyFast++;
                    break;
                default:
                    _passedThrough++;
                    Log.InfoOnce(LogKey + "unknown." + source.Label, $"BundleCache: {source.Label} is not a readable UnityFS bundle, loaded uncached");
                    break;
            }
        }
        catch (Exception e)
        {
            Log.WarningOnce(LogKey + "lookup", $"BundleCache: lookup failed, loading originals: {e}");
        }
        return null;
    }

    /// <summary>Worker thread: a failed eager job's dropped miss is rebuilt by the Unity queue after all.</summary>
    private static void OnEagerFailed(string fileName)
    {
        _main?.Post(_ => Log.Guard("BundleCache eager failed", () =>
        {
            lock (Pending)
            {
                if (!Parked.TryGetValue(fileName, out ResourceSource source))
                {
                    return;
                }
                Parked.Remove(fileName);
                Pending.Add(source);
            }
            if (_menuReady && !_running)
            {
                Next();
            }
        }), null);
    }

    private static void Enqueue(ResourceSource source)
    {
        lock (Pending)
        {
            if (!Queued.Add(source.FileName))
            {
                return;
            }
            Pending.Add(source);
        }
        if (_menuReady && !_running && SynchronizationContext.Current == _main)
        {
            Next();
        }
    }

    private static void OnMenuReady()
    {
        if (!_active)
        {
            return;
        }
        Log.Info($"BundleCache: {_hits} hits ({_hitBytes / (1024.0 * 1024.0):F1} MB served from LZ4 copies), {_misses} LZMA misses queued, " +
                 $"{_alreadyFast} already LZ4/uncompressed, {_passedThrough} not cacheable, {_hitFailures} copies failed to load (originals used)");
        if (EagerBuilder.Started && !_eagerOff)
        {
            Log.Info(EagerBuilder.Summary() + $"; {_eagerHits} of the local hits were copies finished while their load waited");
        }
        _main = SynchronizationContext.Current;
        EagerBuilder.JobFailed += OnEagerFailed;
        if (_main == null)
        {
            Log.Warning("BundleCache: no Unity synchronization context on the main thread, cache not populated");
            return;
        }
        _loadedMvids = new HashSet<Guid>();
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (!assembly.IsDynamic)
                {
                    _loadedMvids.UnionWith(assembly.GetModules().Select(m => m.ModuleVersionId));
                }
            }
            catch (Exception e) when (e is NotSupportedException || e is NotImplementedException)
            {
                // Such an assembly cannot own a cached resource either.
            }
        }
        _menuReady = true;
        Next();
    }

    /// <summary>
    /// Main thread: starts the next queued copy; once the miss queue is empty, recompresses one eagerly built
    /// (uncompressed) copy to Unity's own LZ4 next to it (<see cref="CacheStore.UpgradePath"/>; the preloader
    /// promotes it over the copy at the next launch, the copy itself is loaded now and cannot be replaced); with
    /// nothing left, runs maintenance once.
    /// </summary>
    private static void Next()
    {
        ResourceSource source;
        lock (Pending)
        {
            // Keys the eager builder produces (or produced) need no Unity recompress of the original; a failed
            // eager job releases its key, so that miss is rebuilt here as before.
            foreach (ResourceSource owned in Pending.Where(s => !_eagerOff && EagerBuilder.Owns(s.FileName)).ToList())
            {
                Pending.Remove(owned);
                Parked[owned.FileName] = owned; // back into the queue if the eager job fails (OnEagerFailed)
            }
            source = Pending.FirstOrDefault();
            if (source != null)
            {
                Pending.RemoveAt(0);
            }
        }
        if (source != null)
        {
            _running = true;
            PopulateTime.Start();
            string input = CacheStore.TempOf(_store.PathOf(source), "src");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool copied = CopyResource(source, input);
                _main.Post(__ => Log.Guard("BundleCache recompress", () =>
                {
                    string output = CacheStore.TempOf(_store.PathOf(source), "out");
                    if (!copied || !Recompress(input, output, source.Label, (success, result) => Store(source, input, output, success, result)))
                    {
                        CacheStore.TryDelete(input);
                        CacheStore.TryDelete(output);
                        Next();
                    }
                }), null);
            });
            return;
        }
        if (!_eagerOff && EagerBuilder.Started)
        {
            _upgrades ??= EagerBuilder.Built();
            while (_upgrades.Count > 0)
            {
                string fileName = _upgrades[0];
                _upgrades.RemoveAt(0);
                string input = Path.Combine(_store.Dir, fileName);
                string output = CacheStore.TempOf(input, "lz4");
                _running = true;
                PopulateTime.Start();
                if (Recompress(input, output, "LZ4 upgrade " + fileName, (success, _) =>
                    {
                        if (success && BundleHeader.IsCompleteCopy(output) && BundleHeader.Nodes(output) == BundleHeader.Nodes(input))
                        {
                            string target = _store.UpgradePath(fileName);
                            CacheStore.TryDelete(target);
                            File.Move(output, target);
                            _upgraded++;
                        }
                        else
                        {
                            CacheStore.TryDelete(output);
                        }
                    }))
                {
                    return;
                }
                CacheStore.TryDelete(output); // failed to start: try the next copy
            }
        }
        _running = false;
        if (PopulateTime.IsRunning)
        {
            PopulateTime.Stop();
            Log.Info($"BundleCache: cached {_created} bundles ({_createdBytes / (1024.0 * 1024.0):F1} MB LZ4), {_upgraded} eager copies recompressed to LZ4 " +
                     $"for the next launch, in {PopulateTime.Elapsed.TotalSeconds:F1} s");
        }
        if (!_maintained)
        {
            _maintained = true;
            ThreadPool.QueueUserWorkItem(_ => Log.Guard("BundleCache maintenance", Maintain));
        }
    }

    /// <summary>
    /// Main thread (Unity API): recompresses <paramref name="input"/> to LZ4 at <paramref name="output"/>; when it
    /// completes, <paramref name="store"/>(success, Unity's result text) runs on a worker thread, then <see cref="Next"/>
    /// on the main thread. False when the operation did not start.
    /// </summary>
    private static bool Recompress(string input, string output, string label, Action<bool, string> store)
    {
        AssetBundleRecompressOperation op = null;
        try
        {
            op = AssetBundle.RecompressAssetBundleAsync(input, output, BuildCompression.LZ4Runtime, 0u, UnityEngine.ThreadPriority.Low);
        }
        catch (Exception e)
        {
            Log.WarningOnce(LogKey + "recompress." + label, $"BundleCache: recompress of {label} failed to start: {e.Message}");
        }
        if (op == null)
        {
            return false;
        }
        op.completed += _ =>
        {
            bool success = op.success;
            string result = op.humanReadableResult;
            ThreadPool.QueueUserWorkItem(__ =>
            {
                Log.Guard("BundleCache store", () => store(success, result));
                _main.Post(___ => Log.Guard("BundleCache next", Next), null);
            });
        };
        return true;
    }

    /// <summary>Worker thread: RecompressAssetBundleAsync takes a file path, so the resource is copied out first.</summary>
    private static bool CopyResource(ResourceSource source, string input)
    {
        try
        {
            using Stream resource = source.Assembly.GetManifestResourceStream(source.ResourceName);
            using var file = new FileStream(input, FileMode.Create, FileAccess.Write, FileShare.None);
            resource.CopyTo(file);
            return true;
        }
        catch (Exception e)
        {
            Log.WarningOnce(LogKey + "copy." + source.Label, $"BundleCache: could not extract {source.Label}: {e.Message}");
            CacheStore.TryDelete(input);
            return false;
        }
    }

    /// <summary>Worker thread: validate the LZ4 output and rename it into place, then persist the index.</summary>
    private static void Store(ResourceSource source, string input, string output, bool success, string result)
    {
        CacheStore.TryDelete(input);
        if (!success || !BundleHeader.IsCompleteCopy(output) || !SameNodes(source, output))
        {
            Log.WarningOnce(LogKey + "store." + source.Label, $"BundleCache: recompress of {source.Label} failed ({result}), it stays uncached");
            CacheStore.TryDelete(output);
            return;
        }
        string target = _store.PathOf(source);
        long size = new FileInfo(output).Length;
        try
        {
            File.Move(output, target);
        }
        catch (IOException) when (File.Exists(target))
        {
            CacheStore.TryDelete(output); // another process stored the same copy first
        }
        _store.Add(source.FileName, source.Label);
        _created++;
        _createdBytes += size;
    }

    /// <summary>The copy holds the same files (node paths + sizes) as the source bundle; true when the source's
    /// directory is LZMA-compressed and cannot be read here (none seen so far).</summary>
    private static bool SameNodes(ResourceSource source, string copy)
    {
        using Stream resource = source.Assembly.GetManifestResourceStream(source.ResourceName);
        string nodes = resource == null ? null : BundleHeader.Nodes(resource);
        return nodes == null || nodes == BundleHeader.Nodes(copy);
    }

    private static void Maintain()
    {
        long size = _store.Maintain(_loadedMvids, Config.BundleCacheMaxSizeMB.Value * 1024L * 1024L, out int deleted);
        Log.Info($"BundleCache: {size / (1024.0 * 1024.0):F1} MB on disk (cap {Config.BundleCacheMaxSizeMB.Value} MB), {deleted} stale files removed");
    }
}
