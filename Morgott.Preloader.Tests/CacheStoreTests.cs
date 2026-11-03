using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Morgott.Preloader.BundleCache;
using Xunit;

namespace Morgott.Preloader.Tests
{
    public sealed class CacheStoreTests : IDisposable
    {
        private static readonly Guid Loaded = Guid.NewGuid();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mp-cache-" + Guid.NewGuid().ToString("N"));
        private readonly CacheStore _store;

        public CacheStoreTests() => _store = new CacheStore(_root, "6000.0.75f1");

        public void Dispose() => Directory.Delete(_root, true);

        private string Copy(string name, int size, Guid? mvid = null)
        {
            string fileName = (mvid ?? Loaded).ToString("N") + "-" + name + ".bundle";
            File.WriteAllBytes(Path.Combine(_store.Dir, fileName), new byte[size]);
            return fileName;
        }

        private void WriteIndex(params (string File, long Ticks)[] entries) =>
            File.WriteAllLines(Path.Combine(_store.Dir, "index.tsv"), new[] { "# header" }.Concat(entries.Select(e => e.File + "\t" + e.Ticks + "\tmod:res")));

        private string[] Files() => Directory.GetFiles(_store.Dir, "*.bundle").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        [Fact]
        public void LayoutIsVersionedPerUnityVersion()
        {
            Assert.Equal(Path.Combine(Path.GetFullPath(_root), "v1-6000.0.75f1"), _store.Dir);
            Assert.True(Directory.Exists(_store.Dir));
            Assert.Equal(Path.Combine(Path.GetFullPath(_root), "v1-6000.0_x_y"), CacheStore.DirFor(_root, "6000.0 x/y"));
            Assert.Equal(Path.Combine(Path.GetFullPath(_root), "v1-unknown"), CacheStore.DirFor(_root, null));
            Assert.Equal("a.bundle." + Process.GetCurrentProcess().Id + ".src.tmp", CacheStore.TempOf("a.bundle", "src"));
        }

        [Fact]
        public void EvictsLeastRecentlyUsedBeyondTheCap()
        {
            string oldest = Copy("1", 100), middle = Copy("2", 100), newest = Copy("3", 100);
            WriteIndex((oldest, 10), (middle, 20), (newest, 30));
            long size = _store.Maintain(new[] { Loaded }, 250, out int deleted);
            Assert.Equal(200, size);
            Assert.Equal(1, deleted);
            Assert.Equal(new[] { middle, newest }.OrderBy(n => n, StringComparer.Ordinal), Files());
            string index = File.ReadAllText(Path.Combine(_store.Dir, "index.tsv"));
            Assert.DoesNotContain(oldest, index);
            Assert.Contains(middle + "\t20\tmod:res", index);
        }

        [Fact]
        public void CopiesUsedThisSessionAreNeverEvicted()
        {
            string oldest = Copy("1", 100), middle = Copy("2", 100), created = Copy("3", 100);
            WriteIndex((oldest, 10), (middle, 20), (created, 30));
            _store.MarkUsed(oldest);
            _store.Add(created, "mod:new");
            long size = _store.Maintain(new[] { Loaded }, 0, out int deleted);
            Assert.Equal(200, size);
            Assert.Equal(1, deleted);
            Assert.Equal(new[] { oldest, created }.OrderBy(n => n, StringComparer.Ordinal), Files());
        }

        [Fact]
        public void RemovesUnloadedAssembliesForeignFilesOtherVersionsAndDeadTemps()
        {
            string kept = Copy("1", 10);
            Copy("2", 10, Guid.NewGuid()); // assembly no longer loaded
            File.WriteAllBytes(Path.Combine(_store.Dir, "foreign.bundle"), new byte[10]);
            string liveTemp = CacheStore.TempOf(Path.Combine(_store.Dir, kept), "out");
            File.WriteAllBytes(liveTemp, new byte[1]);
            string deadTemp = Path.Combine(_store.Dir, kept + ".2147480000.out.tmp");
            File.WriteAllBytes(deadTemp, new byte[1]);
            string otherVersion = Path.Combine(_root, "v1-2022.3.9f1");
            Directory.CreateDirectory(otherVersion);

            long size = _store.Maintain(new List<Guid> { Loaded }, long.MaxValue, out int deleted);

            Assert.Equal(10, size);
            Assert.Equal(4, deleted); // unloaded + foreign + dead temp + other version
            Assert.Equal(new[] { kept }, Files());
            Assert.True(File.Exists(liveTemp));
            Assert.False(File.Exists(deadTemp));
            Assert.False(Directory.Exists(otherVersion));
            Assert.Contains(kept, File.ReadAllText(Path.Combine(_store.Dir, "index.tsv")));
        }
    }
}
