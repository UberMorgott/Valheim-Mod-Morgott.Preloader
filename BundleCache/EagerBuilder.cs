using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx;
using Morgott.Preloader.Core;
using Mono.Cecil;

namespace Morgott.Preloader.BundleCache
{
    /// <summary>
    /// <c>[BundleCache] EagerFirstBuild</c>: the first launch after a pack update used to decompress every mod's LZMA
    /// bundle on the main thread (31 loads, 18.8 s) and only build the LZ4 cache after the main menu. From the
    /// preloader on (before Unity starts), this scans BepInEx\plugins with Mono.Cecil (no assembly is loaded), finds
    /// manifest resources that are LZMA UnityFS bundles without a cached copy, and converts them on a few
    /// below-normal worker threads with <see cref="BundleConverter"/> (managed LZMA, byte-identical to Unity's own
    /// decode on every pack bundle) into the existing cache directory under the same key
    /// (<see cref="ResourceSource.FileName"/>: the chainloader loads the same DLL files, so MVID + resource name match).
    /// Largest first, so the longest stream starts at once. A main-thread load whose copy is still being built waits
    /// only when the remaining work is clearly shorter than Unity's own LZMA load (bounded by that estimate); else it
    /// loads the original, unchanged. Copies are stored uncompressed and upgraded to Unity's LZ4 after the main menu
    /// (<see cref="CacheStore.UpgradePath"/>), so the steady-state cache is the same as without this option.
    /// </summary>
    internal static class EagerBuilder
    {
        /// <summary>Unity's main-thread LZMA load rate, measured: 1114 MB of pack bundles in 18.8 s.</summary>
        private const double NativeBytesPerSecond = 59.0 * 1024 * 1024;

        private static readonly Dictionary<string, Job> Jobs = new Dictionary<string, Job>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<Job> Queue = new List<Job>();
        private static readonly Stopwatch Clock = new Stopwatch();
        private static string _dir;
        private static int _workers;
        private static int _waitHits;
        private static double _waitedMs;
        private static int _skippedWaits;
        private static volatile bool _scanDone;

        public static bool Started => _dir != null;

        /// <summary>Preloader: promotes finished LZ4 upgrades, then scans and starts the workers (all off this thread).</summary>
        public static void Start(string cacheDir)
        {
            _dir = cacheDir;
            Clock.Start();
            // Synchronous, before any load hook exists: no load can see a copy mid-replacement.
            Log.Guard("BundleCache eager promote", () =>
            {
                Directory.CreateDirectory(_dir);
                PromoteUpgrades(_dir);
            });
            var scan = new Thread(() => Log.Guard("BundleCache eager scan", Scan)) { IsBackground = true, Name = "Morgott.Preloader eager scan", Priority = ThreadPriority.BelowNormal };
            scan.Start();
        }

        /// <summary>Chainloader: the cache directory Unity reports must be the one the preloader guessed.</summary>
        public static bool Matches(string storeDir) =>
            _dir != null && string.Equals(Path.GetFullPath(_dir), Path.GetFullPath(storeDir), StringComparison.OrdinalIgnoreCase);

        /// <summary>The builder will produce (or produced) this copy; false after a failure.</summary>
        public static bool Owns(string fileName)
        {
            lock (Jobs)
            {
                return Jobs.TryGetValue(fileName, out Job job) && job.State != JobState.Failed;
            }
        }

        /// <summary>Copies built this session (uncompressed): queued for the LZ4 upgrade after the main menu.</summary>
        public static List<string> Built()
        {
            lock (Jobs)
            {
                return Jobs.Values.Where(j => j.State == JobState.Done).Select(j => j.FileName)
                    .Concat(Leftovers).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(name => !File.Exists(Path.Combine(_dir, name + CacheStore.UpgradeExtension))).ToList();
            }
        }

        /// <summary>A built copy Unity could not load was deleted: the key goes back to the normal miss path.</summary>
        public static void Release(string fileName)
        {
            lock (Jobs)
            {
                if (Jobs.TryGetValue(fileName, out Job job))
                {
                    job.State = JobState.Failed;
                }
                Leftovers.Remove(fileName);
            }
        }

        /// <summary>Raised on a worker thread when a job fails, so a miss the post-menu queue dropped is rebuilt.</summary>
        public static event Action<string> JobFailed;

        /// <summary>Uncompressed copies of earlier sessions (finished after that menu): still due for the LZ4 upgrade.</summary>
        private static readonly HashSet<string> Leftovers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static void FindLeftovers()
        {
            foreach (string path in Directory.GetFiles(_dir, "*.bundle"))
            {
                try
                {
                    using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        BundleConverter.Source s = BundleConverter.Parse(file, out _);
                        // Unity's own copies are LZ4; an all-stored copy is one of ours.
                        if (s != null && s.BlockFlags.All(f => (f & 0x3F) == 0))
                        {
                            lock (Jobs)
                            {
                                Leftovers.Add(Path.GetFileName(path));
                            }
                        }
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException || e is EndOfStreamException)
                {
                    // Unreadable now: looked at again next launch.
                }
            }
        }

        /// <summary>
        /// Main thread, on a cache miss: true when the copy is (now) in place. A running job is waited for only when
        /// its remaining time (from its own rate so far) is under 3/4 of Unity's estimated LZMA load of the same
        /// bundle, and never longer than that estimate.
        /// </summary>
        public static bool TryWait(string fileName)
        {
            Job job;
            lock (Jobs)
            {
                if (!_scanDone || !Jobs.TryGetValue(fileName, out job))
                {
                    return false;
                }
            }
            if (job.State == JobState.Done)
            {
                return true;
            }
            if (job.State != JobState.Running)
            {
                _skippedWaits++;
                return false;
            }
            double native = job.DataSize / NativeBytesPerSecond;
            double elapsed = job.Watch.Elapsed.TotalSeconds;
            long done = Interlocked.Read(ref job.Progress);
            if (done <= 0 || elapsed < 0.05)
            {
                _skippedWaits++;
                return false;
            }
            double remaining = (job.DataSize - done) / (done / elapsed);
            if (remaining > native * 0.75)
            {
                _skippedWaits++;
                return false;
            }
            var watch = Stopwatch.StartNew();
            bool finished = job.Finished.WaitOne(TimeSpan.FromSeconds(native));
            _waitedMs += watch.Elapsed.TotalMilliseconds;
            if (finished && job.State == JobState.Done)
            {
                _waitHits++;
                return true;
            }
            _skippedWaits++;
            return false;
        }

        public static string Summary()
        {
            lock (Jobs)
            {
                int done = Jobs.Values.Count(j => j.State == JobState.Done);
                int failed = Jobs.Values.Count(j => j.State == JobState.Failed);
                double mb = Jobs.Values.Where(j => j.State == JobState.Done).Sum(j => j.DataSize) / (1024.0 * 1024.0);
                return $"BundleCache eager: {Jobs.Count} LZMA bundles found, {done} built ({mb:F0} MB) on {_workers} threads, {failed} failed, " +
                       $"{Jobs.Count - done - failed} still running; {_waitHits} loads waited {_waitedMs:F0} ms for a copy, {_skippedWaits} loaded the original";
            }
        }

        private static void Scan()
        {
            FindLeftovers();
            var found = new List<Job>();
            string plugins = Paths.PluginPath;
            FieldInfo offsetField = typeof(EmbeddedResource).GetField("offset", BindingFlags.NonPublic | BindingFlags.Instance);
            if (offsetField == null)
            {
                Log.Warning("BundleCache eager: Mono.Cecil EmbeddedResource.offset not found (Cecil changed?), eager build off");
                _scanDone = true;
                return;
            }
            foreach (string dll in Directory.GetFiles(plugins, "*.dll", SearchOption.AllDirectories))
            {
                try
                {
                    ScanDll(dll, offsetField, found);
                }
                catch (Exception)
                {
                    // Not a managed DLL or unreadable: the chainloader decides about it, its bundles stay on the normal path.
                }
            }
            found.Sort((a, b) => b.DataSize.CompareTo(a.DataSize));
            lock (Jobs)
            {
                foreach (Job job in found)
                {
                    if (!Jobs.ContainsKey(job.FileName))
                    {
                        Jobs[job.FileName] = job;
                        Queue.Add(job);
                    }
                }
                _scanDone = true;
            }
            _workers = Math.Max(1, Math.Min(Environment.ProcessorCount - 2, 6));
            _workers = Math.Min(_workers, Math.Max(1, Queue.Count));
            Log.Info($"BundleCache eager: scan {Clock.ElapsedMilliseconds} ms, {Queue.Count} LZMA bundles without a copy " +
                     $"({found.Sum(j => j.DataSize) / (1024.0 * 1024.0):F0} MB), {(Queue.Count > 0 ? _workers : 0)} worker threads");
            if (Queue.Count == 0)
            {
                return;
            }
            for (int i = 0; i < _workers; i++)
            {
                new Thread(() => Log.Guard("BundleCache eager worker", Work)) { IsBackground = true, Name = "Morgott.Preloader eager " + i, Priority = ThreadPriority.BelowNormal }.Start();
            }
        }

        private static void ScanDll(string dll, FieldInfo offsetField, List<Job> found)
        {
            using (var file = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (ModuleDefinition module = ModuleDefinition.ReadModule(file, new ReaderParameters { ReadingMode = ReadingMode.Deferred }))
            {
                long resources = -1;
                foreach (Resource resource in module.Resources)
                {
                    if (!(resource is EmbeddedResource embedded) || !(offsetField.GetValue(embedded) is uint offset))
                    {
                        continue;
                    }
                    if (resources < 0)
                    {
                        resources = PeResources(file);
                    }
                    file.Position = resources + offset;
                    var len = new byte[4];
                    if (file.Read(len, 0, 4) != 4)
                    {
                        continue;
                    }
                    long length = BitConverter.ToUInt32(len, 0);
                    long start = resources + offset + 4;
                    if (length < 64 || start + length > file.Length)
                    {
                        continue;
                    }
                    string fileName = new ResourceSource(null, module.Mvid, embedded.Name, length).FileName;
                    if (File.Exists(Path.Combine(_dir, fileName)))
                    {
                        continue;
                    }
                    var slice = new SliceStream(file, start, length);
                    BundleConverter.Source source = BundleConverter.Parse(slice, out _);
                    if (source == null || !source.HasLzma)
                    {
                        continue;
                    }
                    found.Add(new Job
                    {
                        FileName = fileName,
                        Label = module.Assembly?.Name?.Name + ":" + embedded.Name,
                        Dll = dll,
                        Start = start,
                        Length = length,
                        Source = source,
                        DataSize = source.DataSize,
                    });
                }
            }
        }

        /// <summary>File offset of the CLI resources directory: PE optional header data directory 14 (CLI header),
        /// whose Resources entry sits at offset 24 (ECMA-335 II.25.3.3), RVAs mapped through the section table.</summary>
        private static long PeResources(FileStream file)
        {
            var r = new BinaryReader(file);
            file.Position = 0x3C;
            file.Position = r.ReadInt32();
            if (r.ReadUInt32() != 0x00004550)
            {
                throw new BadImageFormatException("no PE signature");
            }
            r.ReadUInt16();
            int sections = r.ReadUInt16();
            file.Position += 12;
            int optionalSize = r.ReadUInt16();
            r.ReadUInt16();
            long optional = file.Position;
            ushort magic = r.ReadUInt16();
            file.Position = optional + (magic == 0x20B ? 112 : 96) + 14 * 8;
            uint cliRva = r.ReadUInt32();
            long table = optional + optionalSize;
            long Map(uint rva)
            {
                for (int i = 0; i < sections; i++)
                {
                    file.Position = table + i * 40 + 8;
                    uint virtualSize = r.ReadUInt32();
                    uint virtualAddress = r.ReadUInt32();
                    uint rawSize = r.ReadUInt32();
                    uint rawPointer = r.ReadUInt32();
                    if (rva >= virtualAddress && rva < virtualAddress + Math.Max(virtualSize, rawSize))
                    {
                        return rawPointer + (rva - virtualAddress);
                    }
                }
                throw new BadImageFormatException("RVA outside every section");
            }
            file.Position = Map(cliRva) + 24;
            return Map(r.ReadUInt32());
        }

        private static void Work()
        {
            while (true)
            {
                Job job;
                lock (Jobs)
                {
                    job = Queue.FirstOrDefault();
                    if (job == null)
                    {
                        return;
                    }
                    Queue.RemoveAt(0);
                    job.State = JobState.Running;
                }
                job.Watch.Start();
                string temp = Path.Combine(_dir, job.FileName + "." + Process.GetCurrentProcess().Id + ".eager.tmp");
                string target = Path.Combine(_dir, job.FileName);
                string problem = null;
                try
                {
                    using (var file = new FileStream(job.Dll, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                    {
                        var slice = new SliceStream(file, job.Start, job.Length);
                        BundleConverter.Convert(slice, job.Source, temp, n => Interlocked.Exchange(ref job.Progress, n));
                    }
                    problem = Validate(temp, job.Source);
                    if (problem == null)
                    {
                        try
                        {
                            File.Move(temp, target);
                        }
                        catch (IOException) when (File.Exists(target))
                        {
                            TryDelete(temp); // another instance stored it first
                        }
                    }
                }
                catch (Exception e)
                {
                    problem = e.GetType().Name + ": " + e.Message;
                }
                job.Watch.Stop();
                if (problem != null)
                {
                    TryDelete(temp);
                    Log.Warning($"BundleCache eager: {job.Label} not built ({problem}), it takes the normal path");
                }
                lock (Jobs)
                {
                    job.State = problem == null ? JobState.Done : JobState.Failed;
                }
                job.Finished.Set();
                if (problem != null)
                {
                    JobFailed?.Invoke(job.FileName);
                }
                if (problem == null && Config.ProfilerEnabled?.Value == true)
                {
                    Log.Info($"BundleCache eager: {job.Label} {job.DataSize / (1024.0 * 1024.0):F1} MB in {job.Watch.ElapsedMilliseconds} ms (t={Clock.ElapsedMilliseconds} ms)");
                }
            }
        }

        /// <summary>The written copy re-read from disk: complete stored bundle, same hash, directory and data size.</summary>
        private static string Validate(string path, BundleConverter.Source source)
        {
            if (!BundleHeader.IsCompleteCopy(path))
            {
                return "written copy is incomplete";
            }
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                BundleConverter.Source copy = BundleConverter.Parse(file, out string problem);
                if (copy == null)
                {
                    return "written copy unreadable: " + problem;
                }
                if (copy.HasLzma || copy.DataSize != source.DataSize || !copy.Hash.SequenceEqual(source.Hash) || !copy.Directory.SequenceEqual(source.Directory))
                {
                    return "written copy differs from the source layout";
                }
            }
            return null;
        }

        /// <summary>LZ4 upgrades finished after the last main menu replace their uncompressed copies before any load.</summary>
        private static void PromoteUpgrades(string dir)
        {
            foreach (string upgrade in Directory.GetFiles(dir, "*" + CacheStore.UpgradeExtension))
            {
                string target = upgrade.Substring(0, upgrade.Length - CacheStore.UpgradeExtension.Length);
                try
                {
                    if (!BundleHeader.IsCompleteCopy(upgrade) || !File.Exists(target) || BundleHeader.Nodes(upgrade) != BundleHeader.Nodes(target))
                    {
                        File.Delete(upgrade);
                        continue;
                    }
                    File.Delete(target);
                    File.Move(upgrade, target);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // Copy in use by another running instance: promoted on a later launch.
                }
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // Removed by the next maintenance pass (dead temps).
            }
        }

        private enum JobState
        {
            Queued,
            Running,
            Done,
            Failed,
        }

        private sealed class Job
        {
            public readonly ManualResetEvent Finished = new ManualResetEvent(false);
            public readonly Stopwatch Watch = new Stopwatch();
            public string FileName;
            public string Label;
            public string Dll;
            public long Start;
            public long Length;
            public BundleConverter.Source Source;
            public long DataSize;
            public long Progress;
            public volatile JobState State;
        }

        /// <summary>Read-only window of a file (one manifest resource), position 0 = resource start.</summary>
        private sealed class SliceStream : Stream
        {
            private readonly Stream _inner;
            private readonly long _start;
            private long _position;

            public SliceStream(Stream inner, long start, long length)
            {
                _inner = inner;
                _start = start;
                Length = length;
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length { get; }

            public override long Position
            {
                get => _position;
                set => _position = value;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                long left = Length - _position;
                if (left <= 0)
                {
                    return 0;
                }
                _inner.Position = _start + _position;
                int n = _inner.Read(buffer, offset, (int)Math.Min(count, left));
                _position += n;
                return n;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                _position = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _position + offset : Length + offset;
                return _position;
            }

            public override void Flush()
            {
            }

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
