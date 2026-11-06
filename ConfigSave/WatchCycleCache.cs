using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Morgott.Preloader.ConfigSave
{
    /// <summary>
    /// File-system reads of one Mono <c>DefaultWatcher</c> poll pass, shared by every watcher in it. Many mods watch the
    /// same BepInEx\config folder; vanilla re-reads it (entries + two stats per file) once per watcher every 750 ms.
    /// Active only on the thread between <see cref="Enter"/> and <see cref="Exit"/>; <see cref="Sleep"/> (the monitor
    /// loop's wait) starts a new pass, so a change is seen at most one pass later. Calls that throw are not cached.
    /// </summary>
    internal sealed class WatchCycleCache
    {
        [ThreadStatic] private static WatchCycleCache _thread;
        [ThreadStatic] private static WatchCycleCache _active;

        private static int _generation;
        private static long _real;
        private static long _cached;

        private int _cycle = -1;
        private readonly Dictionary<string, bool> _dirExists = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _fileExists = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> _dirs = new Dictionary<string, string[]>(StringComparer.Ordinal);
        private readonly Dictionary<(string, string), string[]> _entries = new Dictionary<(string, string), string[]>();
        private readonly Dictionary<string, DateTime> _created = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _written = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        /// <summary>Real file-system calls made / calls answered from the pass cache, process-wide.</summary>
        public static (long Real, long Cached) Counts => (Interlocked.Read(ref _real), Interlocked.Read(ref _cached));

        /// <summary>The monitor loop's <c>Thread.Sleep</c>: after it every read is fresh again.</summary>
        public static void Sleep(int milliseconds)
        {
            Thread.Sleep(milliseconds);
            Interlocked.Increment(ref _generation);
        }

        /// <summary>Serves this thread's reads from the current pass's cache until <see cref="Exit"/>.</summary>
        public static void Enter()
        {
            WatchCycleCache cache = _thread ??= new WatchCycleCache();
            int cycle = Volatile.Read(ref _generation);
            if (cache._cycle != cycle)
            {
                cache.Clear();
                cache._cycle = cycle;
            }
            _active = cache;
        }

        public static void Exit() => _active = null;

        public static bool DirectoryExists(string path) => _active == null ? Directory.Exists(path) : Get(_active._dirExists, path, Directory.Exists);

        public static bool FileExists(string path) => _active == null ? File.Exists(path) : Get(_active._fileExists, path, File.Exists);

        public static string[] GetDirectories(string path) => _active == null ? Directory.GetDirectories(path) : Get(_active._dirs, path, Directory.GetDirectories);

        public static string[] GetFileSystemEntries(string path, string pattern)
        {
            if (_active == null) return Directory.GetFileSystemEntries(path, pattern);
            if (_active._entries.TryGetValue((path, pattern), out string[] hit))
            {
                Interlocked.Increment(ref _cached);
                return hit;
            }
            string[] value = Directory.GetFileSystemEntries(path, pattern);
            Interlocked.Increment(ref _real);
            _active._entries[(path, pattern)] = value;
            return value;
        }

        public static DateTime GetCreationTime(string path) => _active == null ? File.GetCreationTime(path) : Get(_active._created, path, File.GetCreationTime);

        public static DateTime GetLastWriteTime(string path) => _active == null ? File.GetLastWriteTime(path) : Get(_active._written, path, File.GetLastWriteTime);

        private static T Get<T>(Dictionary<string, T> cache, string path, Func<string, T> real)
        {
            // A null path throws in the real call, as vanilla; it is never a key.
            if (path != null && cache.TryGetValue(path, out T hit))
            {
                Interlocked.Increment(ref _cached);
                return hit;
            }
            T value = real(path);
            Interlocked.Increment(ref _real);
            cache[path] = value;
            return value;
        }

        private void Clear()
        {
            _dirExists.Clear();
            _fileExists.Clear();
            _dirs.Clear();
            _entries.Clear();
            _created.Clear();
            _written.Clear();
        }
    }
}
