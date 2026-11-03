using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Morgott.Preloader.Core;

namespace Morgott.Preloader.BundleCache;

/// <summary>
/// On-disk cache layout: <c>&lt;root&gt;\v1-&lt;Unity version&gt;\&lt;mvid&gt;-&lt;name hash&gt;.bundle</c>. The file
/// name IS the key, so a lookup is one <c>File.Exists</c> and a copy is valid as soon as it is renamed into
/// place; no index is needed to serve hits. <c>index.tsv</c> only keeps last-use times for LRU eviction and a
/// readable label; losing it (crash between rename and index write, another process racing) degrades LRU order
/// to file time and never deletes a valid copy. Writers use <c>&lt;file&gt;.&lt;pid&gt;.&lt;kind&gt;.tmp</c> + rename;
/// temps of dead processes are removed by <see cref="Maintain"/>.
/// </summary>
internal sealed class CacheStore
{
    private const string BundleExtension = ".bundle";
    private const string IndexName = "index.tsv";

    /// <summary>Suffix of Unity's LZ4 recompression of an eagerly built (uncompressed) copy, promoted over it at the next launch.</summary>
    public const string UpgradeExtension = ".lz4";

    private readonly string _root;
    private readonly HashSet<string> _usedThisSession = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, (long LastUsedUtc, string Label)> _index;

    public CacheStore(string root, string unityVersion)
    {
        _root = Path.GetFullPath(root);
        Dir = DirFor(root, unityVersion);
        Directory.CreateDirectory(Dir);
    }

    /// <summary>Directory of the copies valid for this format version and Unity version.</summary>
    public string Dir { get; }

    /// <summary>Copy directory for a Unity version (also computed by the preloader, before Unity reports it).</summary>
    public static string DirFor(string root, string unityVersion) =>
        Path.Combine(Path.GetFullPath(root), "v1-" + new string((unityVersion ?? "unknown").Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_').ToArray()));

    /// <summary>Writer temp next to <paramref name="target"/>: <c>&lt;target&gt;.&lt;pid&gt;.&lt;kind&gt;.tmp</c>.</summary>
    public static string TempOf(string target, string kind) =>
        target + "." + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + "." + kind + ".tmp";

    public string UpgradePath(string fileName) => Path.Combine(Dir, fileName + UpgradeExtension);

    public string PathOf(ResourceSource source) => Path.Combine(Dir, source.FileName);

    /// <summary>A copy served (or created) in this session: never evicted by this process.</summary>
    public void MarkUsed(string fileName)
    {
        lock (_usedThisSession)
        {
            _usedThisSession.Add(fileName);
        }
    }

    /// <summary>Records a new copy and persists the index right away (called after the rename into place).</summary>
    public void Add(string fileName, string label)
    {
        lock (_usedThisSession)
        {
            _usedThisSession.Add(fileName);
            LoadIndex();
            _index[fileName] = (DateTime.UtcNow.Ticks, label);
            SaveIndex();
        }
    }

    /// <summary>
    /// Background maintenance after the main menu: removes dead temps, copies for other Unity/format versions,
    /// copies whose assembly (MVID) is no longer loaded, then evicts least recently used copies beyond
    /// <paramref name="capBytes"/>. Copies used (loaded) in this session are never deleted. Returns the resulting cache size.
    /// </summary>
    public long Maintain(ICollection<Guid> loadedMvids, long capBytes, out int deleted)
    {
        deleted = 0;
        foreach (FileInfo temp in new DirectoryInfo(Dir).GetFiles("*" + BundleExtension + ".*.tmp"))
        {
            // <file>.bundle.<pid>.<kind>.tmp; a live writer's temp is kept unless it is a day old (pid reuse).
            string[] parts = temp.Name.Split('.');
            bool alive = parts.Length == 5 && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) &&
                         IsAlive(pid) && DateTime.UtcNow - temp.LastWriteTimeUtc < TimeSpan.FromDays(1);
            if (!alive && TryDelete(temp.FullName))
            {
                deleted++;
            }
        }
        // Compared by name only: a full-path comparison once mixed '/' and '\' and deleted the live directory.
        string current = Path.GetFileName(Dir);
        foreach (DirectoryInfo dir in new DirectoryInfo(_root).GetDirectories("v*-*").Where(d => !string.Equals(d.Name, current, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                dir.Delete(true);
                deleted++;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Log.WarningOnce("BundleCache.oldVersion", $"BundleCache: could not delete outdated cache {dir.FullName}: {e.Message}");
            }
        }
        lock (_usedThisSession)
        {
            LoadIndex();
            var files = new List<FileInfo>();
            foreach (FileInfo file in new DirectoryInfo(Dir).GetFiles("*" + BundleExtension))
            {
                bool used = _usedThisSession.Contains(file.Name);
                if (!used && (!TryParseMvid(file.Name, out Guid mvid) || !loadedMvids.Contains(mvid)))
                {
                    if (TryDelete(file.FullName))
                    {
                        deleted++;
                    }
                    continue;
                }
                if (used || !_index.ContainsKey(file.Name))
                {
                    _index[file.Name] = (used ? DateTime.UtcNow.Ticks : file.LastWriteTimeUtc.Ticks, _index.TryGetValue(file.Name, out var old) ? old.Label : "");
                }
                files.Add(file);
            }

            long total = files.Sum(f => f.Length);
            foreach (FileInfo file in files.OrderBy(f => _index[f.Name].LastUsedUtc))
            {
                if (total <= capBytes)
                {
                    break;
                }
                if (!_usedThisSession.Contains(file.Name) && TryDelete(file.FullName))
                {
                    total -= file.Length;
                    deleted++;
                    _index.Remove(file.Name);
                }
            }

            var present = new HashSet<string>(files.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            foreach (string name in _index.Keys.Where(k => !present.Contains(k) || !File.Exists(Path.Combine(Dir, k))).ToList())
            {
                _index.Remove(name);
            }
            SaveIndex();
            return total;
        }
    }

    /// <summary>False when the file stays (e.g. still mapped by a bundle another process has loaded); a leftover
    /// temp is removed by the next maintenance pass.</summary>
    public static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryParseMvid(string fileName, out Guid mvid)
    {
        mvid = Guid.Empty;
        return fileName.Length > 32 && fileName[32] == '-' && Guid.TryParseExact(fileName.Substring(0, 32), "N", out mvid);
    }

    private void LoadIndex()
    {
        if (_index != null)
        {
            return;
        }
        _index = new(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(Dir, IndexName);
        try
        {
            if (!File.Exists(path))
            {
                return;
            }
            foreach (string[] cols in File.ReadAllLines(path).Select(line => line.Split('\t')))
            {
                if (cols.Length >= 2 && cols[0].EndsWith(BundleExtension, StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(cols[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks))
                {
                    _index[cols[0]] = (ticks, cols.Length > 2 ? cols[2] : "");
                }
            }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            Log.WarningOnce("BundleCache.indexRead", $"BundleCache: index unreadable, LRU order falls back to file times: {e.Message}");
        }
    }

    private void SaveIndex()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Morgott.Preloader bundle cache: file<TAB>last used (UTC ticks)<TAB>source. Only used for LRU eviction.");
        foreach (var kv in _index.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append(kv.Key).Append('\t').Append(kv.Value.LastUsedUtc.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(kv.Value.Label).AppendLine();
        }
        try
        {
            AtomicFile.WriteAllText(Path.Combine(Dir, IndexName), sb.ToString());
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            Log.WarningOnce("BundleCache.indexWrite", $"BundleCache: index not saved (LRU order only): {e.Message}");
        }
    }
}
