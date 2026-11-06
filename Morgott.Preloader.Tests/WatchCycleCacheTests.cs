using System;
using System.IO;
using Morgott.Preloader.ConfigSave;
using Xunit;

namespace Morgott.Preloader.Tests
{
    public class WatchCycleCacheTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "wcc-" + Guid.NewGuid().ToString("N"));

        public WatchCycleCacheTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            WatchCycleCache.Exit();
            Directory.Delete(_dir, true);
        }

        [Fact]
        public void SharesReadsWithinPassAndRereadsNextPass()
        {
            string file = Path.Combine(_dir, "a.cfg");
            File.WriteAllText(file, "x");
            File.SetLastWriteTime(file, new DateTime(2020, 1, 1));

            WatchCycleCache.Enter();
            DateTime first = WatchCycleCache.GetLastWriteTime(file);
            Assert.Single(WatchCycleCache.GetFileSystemEntries(_dir, "*.cfg"));
            WatchCycleCache.Exit();

            File.SetLastWriteTime(file, new DateTime(2021, 1, 1));
            File.WriteAllText(Path.Combine(_dir, "b.cfg"), "y");
            (long real, long cached) = WatchCycleCache.Counts;
            WatchCycleCache.Enter(); // same pass: the next watcher shares the first one's reads
            Assert.Equal(first, WatchCycleCache.GetLastWriteTime(file));
            Assert.Single(WatchCycleCache.GetFileSystemEntries(_dir, "*.cfg"));
            Assert.Equal((real, cached + 2), WatchCycleCache.Counts);
            WatchCycleCache.Exit();

            WatchCycleCache.Sleep(0); // monitor loop wait = next pass: fresh reads
            WatchCycleCache.Enter();
            Assert.Equal(new DateTime(2021, 1, 1), WatchCycleCache.GetLastWriteTime(file));
            Assert.Equal(2, WatchCycleCache.GetFileSystemEntries(_dir, "*.cfg").Length);
        }

        [Fact]
        public void OutsidePassAndFailuresAreNotCached()
        {
            string missing = Path.Combine(_dir, "none", "x.cfg");
            Assert.False(WatchCycleCache.FileExists(missing)); // no Enter: direct call
            WatchCycleCache.Sleep(0);
            WatchCycleCache.Enter();
            Assert.Throws<DirectoryNotFoundException>(() => WatchCycleCache.GetFileSystemEntries(Path.Combine(_dir, "none"), "*"));
            Directory.CreateDirectory(Path.Combine(_dir, "none"));
            Assert.Empty(WatchCycleCache.GetFileSystemEntries(Path.Combine(_dir, "none"), "*"));
        }
    }
}
