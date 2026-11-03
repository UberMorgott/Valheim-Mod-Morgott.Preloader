using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Morgott.Preloader.Core;

namespace Morgott.Preloader.BundleCache
{
    /// <summary>
    /// Prebuilt LZ4 copies shipped in the modpack: <c>&lt;root&gt;\v1-&lt;Unity version&gt;\&lt;key&gt;.bundle</c> +
    /// <c>manifest.tsv</c> (written by <c>tools\build-pack-cache.ps1</c>). Read-only: Morgott.Preloader never writes or
    /// deletes here, so the launcher (which restores every pack file) and the cache never fight.
    /// A copy is served only when, for its key (module MVID + resource-name hash): the manifest row names the same
    /// resource; the copy is a complete LZ4 UnityFS file whose directory (node paths + sizes) equals the source
    /// bundle's; the SHA-256 and length of the source resource and of the copy equal the manifest. The two hashes
    /// are computed once per file version and stamped in the local cache (<c>pack-verified.tsv</c>: copy length +
    /// last write time, DLL length + last write time, both hashes); later launches compare lengths and times only
    /// (an in-place edit keeping both is not rehashed; the launcher replaces pack files by SHA-256 anyway).
    /// Copies are hashed on the thread pool from <see cref="Open"/> on (before plugins load bundles); the source
    /// resource at its first load. Anything else = the copy is ignored for the session and the bundle takes the
    /// local-cache path (hit, or original load + local rebuild).
    /// </summary>
    internal sealed class PackCache
    {
        public const string ManifestName = "manifest.tsv";
        private const string StampName = "pack-verified.tsv";

        private readonly Dictionary<string, Entry> _entries;
        private readonly Dictionary<string, Task<string>> _copyHashes = new Dictionary<string, Task<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _checked = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _stamps;
        private readonly string _stampPath;

        private PackCache(string dir, Dictionary<string, Entry> entries, string stampPath)
        {
            Dir = dir;
            _entries = entries;
            _stampPath = stampPath;
            _stamps = ReadStamps();
        }

        public string Dir { get; }

        public int Count => _entries.Count;

        /// <summary>Null when no pack cache is installed for this Unity version (or its manifest is unreadable).</summary>
        public static PackCache Open(string dir, string localDir)
        {
            string manifest = Path.Combine(dir, ManifestName);
            if (!File.Exists(manifest))
            {
                return null;
            }
            var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string line in File.ReadAllLines(manifest))
                {
                    if (line.Length == 0 || line[0] == '#')
                    {
                        continue;
                    }
                    Entry entry = Entry.Parse(line);
                    entries[entry.File] = entry;
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException || e is OverflowException)
            {
                Log.Warning($"BundleCache: pack manifest {manifest} unreadable, pack cache ignored: {e.Message}");
                return null;
            }
            var pack = new PackCache(dir, entries, Path.Combine(localDir, StampName));
            pack.StartCopyHashes();
            return pack;
        }

        /// <summary>Main thread, at load time: path of the verified pack copy, or null (reason logged once).</summary>
        public string Find(ResourceSource source, Stream stream)
        {
            if (!_entries.TryGetValue(source.FileName, out Entry entry))
            {
                return null;
            }
            string path = Path.Combine(Dir, entry.File);
            lock (_checked) // loads are normally main-thread only; the lock also serializes the stamp file writes
            {
                if (!_checked.TryGetValue(entry.File, out bool ok))
                {
                    string problem = Problem(entry, source, stream, path);
                    ok = problem == null;
                    _checked[entry.File] = ok;
                    if (!ok)
                    {
                        Log.Warning($"BundleCache: pack copy of {source.Label} rejected ({problem}), using the local cache");
                    }
                }
                return ok ? path : null;
            }
        }

        /// <summary>A copy that Unity failed to load: never served again in this session (the pack dir is read-only).</summary>
        public void Reject(string fileName)
        {
            lock (_checked)
            {
                _checked[fileName] = false;
            }
        }

        private string Problem(Entry entry, ResourceSource source, Stream stream, string path)
        {
            if (!string.Equals(entry.ResourceName, source.ResourceName, StringComparison.Ordinal))
            {
                return "manifest names resource " + entry.ResourceName;
            }
            if (entry.SourceLength != source.Length)
            {
                return F("source is {0} bytes, manifest {1}", source.Length, entry.SourceLength);
            }
            var copy = new FileInfo(path);
            if (!copy.Exists || copy.Length != entry.CopyLength)
            {
                return copy.Exists ? F("copy is {0} bytes, manifest {1}", copy.Length, entry.CopyLength) : "copy missing";
            }
            if (!BundleHeader.IsCompleteCopy(path))
            {
                return "not a complete LZ4 bundle";
            }
            string nodes = BundleHeader.Nodes(path);
            if (nodes == null || nodes != BundleHeader.Nodes(stream))
            {
                return "its file list differs from the source bundle";
            }

            string stamp = StampOf(entry, copy, source);
            if (stamp != null && _stamps.TryGetValue(entry.File, out string known) && known == stamp)
            {
                return null;
            }
            // First launch with this copy/DLL: the copy hash normally finished long ago (started before plugin Awake);
            // waiting is still far cheaper than the LZMA load it replaces.
            if (!_copyHashes.TryGetValue(entry.File, out Task<string> copyHash) || copyHash.Result != entry.CopySha256)
            {
                return "copy SHA-256 differs from the manifest";
            }
            using (Stream resource = source.Assembly.GetManifestResourceStream(source.ResourceName))
            {
                if (resource == null || Sha256Hex(resource) != entry.SourceSha256)
                {
                    return "source SHA-256 differs from the manifest (stale copy)";
                }
            }
            if (stamp != null)
            {
                _stamps[entry.File] = stamp;
                WriteStamps();
            }
            return null;
        }

        /// <summary>Copy length + time, DLL length + time and the manifest hashes; null when the DLL has no file.</summary>
        private static string StampOf(Entry entry, FileInfo copy, ResourceSource source)
        {
            string location = source.Assembly.Location;
            if (string.IsNullOrEmpty(location) || !File.Exists(location))
            {
                return null;
            }
            var dll = new FileInfo(location);
            return string.Join("\t", new[]
            {
                copy.Length.ToString(CultureInfo.InvariantCulture), copy.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                dll.Length.ToString(CultureInfo.InvariantCulture), dll.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                entry.SourceSha256, entry.CopySha256,
            });
        }

        private void StartCopyHashes()
        {
            int started = 0;
            foreach (Entry entry in _entries.Values)
            {
                string path = Path.Combine(Dir, entry.File);
                // A copy whose stamp still matches (length, time, manifest hash) is not hashed again.
                var copy = new FileInfo(path);
                if (copy.Exists && _stamps.TryGetValue(entry.File, out string stamp) &&
                    stamp.StartsWith(copy.Length.ToString(CultureInfo.InvariantCulture) + "\t" +
                                     copy.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "\t", StringComparison.Ordinal) &&
                    stamp.EndsWith("\t" + entry.CopySha256, StringComparison.Ordinal))
                {
                    _copyHashes[entry.File] = Task.FromResult(entry.CopySha256);
                    continue;
                }
                started++;
                _copyHashes[entry.File] = Task.Run(() =>
                {
                    try
                    {
                        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 20))
                        {
                            return Sha256Hex(file);
                        }
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        return "unreadable: " + e.Message;
                    }
                });
            }
            Log.Info($"BundleCache: pack cache {Dir}: {_entries.Count} copies, {started} hashed in the background (new or changed)");
        }

        private Dictionary<string, string> ReadStamps()
        {
            var stamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(_stampPath))
                {
                    foreach (string line in File.ReadAllLines(_stampPath))
                    {
                        int tab = line.IndexOf('\t');
                        if (tab > 0)
                        {
                            stamps[line.Substring(0, tab)] = line.Substring(tab + 1);
                        }
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Log.WarningOnce("BundleCache.stampRead", $"BundleCache: {_stampPath} unreadable, pack copies are hashed again: {e.Message}");
            }
            return stamps;
        }

        private void WriteStamps()
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in _stamps.Where(kv => _entries.ContainsKey(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                sb.Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
            }
            try
            {
                AtomicFile.WriteAllText(_stampPath, sb.ToString());
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Log.WarningOnce("BundleCache.stampWrite", $"BundleCache: {_stampPath} not saved, pack copies are hashed again next launch: {e.Message}");
            }
        }

        private static string Sha256Hex(Stream stream)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        private static string F(string format, params object[] args) => string.Format(CultureInfo.InvariantCulture, format, args);

        /// <summary>manifest.tsv row: file, resource name, source length, source SHA-256, copy length, copy SHA-256, owner.</summary>
        private sealed class Entry
        {
            public string File;
            public string ResourceName;
            public long SourceLength;
            public string SourceSha256;
            public long CopyLength;
            public string CopySha256;

            public static Entry Parse(string line)
            {
                string[] c = line.Split('\t');
                if (c.Length < 6 || !c[0].EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) || c[0].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    throw new FormatException("bad manifest row: " + line);
                }
                return new Entry
                {
                    File = c[0],
                    ResourceName = c[1],
                    SourceLength = long.Parse(c[2], NumberStyles.None, CultureInfo.InvariantCulture),
                    SourceSha256 = c[3].ToLowerInvariant(),
                    CopyLength = long.Parse(c[4], NumberStyles.None, CultureInfo.InvariantCulture),
                    CopySha256 = c[5].ToLowerInvariant(),
                };
            }
        }
    }
}
