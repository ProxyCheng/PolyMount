using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class ConcurrentMetadataTests
{
    private sealed class BlockingSource : ILayer, IDisposable
    {
        public readonly ManualResetEventSlim Entered = new(), Release = new();
        public int SlowListings;
        public string Name => "blocking";
        public string Generation => "1";
        public bool Writable => false;
        public bool TryGetEntry(string path, out VfsEntry entry)
        {
            entry = new VfsEntry { Name = VfsPath.Name(path), IsDir = path != "fast/file", Size = 7 };
            return path is "" or "slow" or "fast" or "fast/file";
        }
        public IReadOnlyList<VfsEntry> ListOwn(string path)
        {
            if (path == "slow")
            {
                Interlocked.Increment(ref SlowListings);
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            }
            return path == "" ? new[] { new VfsEntry { Name = "slow", IsDir = true }, new VfsEntry { Name = "fast", IsDir = true } }
                : path == "fast" ? new[] { new VfsEntry { Name = "file", Size = 7 } } : Array.Empty<VfsEntry>();
        }
        public bool TryOpenRead(string path, out VfsEntry entry, out Stream stream)
        {
            stream = null!;
            if (!TryGetEntry(path, out entry) || entry.IsDir) return false;
            stream = new MemoryStream("payload"u8.ToArray());
            return true;
        }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }

    [TestMethod]
    public async Task Concurrent_same_directory_queries_share_one_inflight_listing()
    {
        using var source = new BlockingSource();
        var vfs = new VirtualFileSystem(new[] { source });
        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Factory.StartNew(() => vfs.List("slow"),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        try
        {
            Assert.IsTrue(source.Entered.Wait(TimeSpan.FromSeconds(5)));
            await Task.Delay(200);
            Assert.AreEqual(1, source.SlowListings);
        }
        finally { source.Release.Set(); await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)); }
        vfs.List("slow");
        Assert.AreEqual(2, source.SlowListings, "Finished results must not become an unversioned permanent cache");
    }

    [TestMethod]
    public async Task Slow_directory_does_not_block_other_lookup_listing_probe_or_file_read()
    {
        using var source = new BlockingSource();
        var vfs = new VirtualFileSystem(new[] { source });
        var slow = Task.Run(() => vfs.List("slow"));
        try
        {
            Assert.IsTrue(source.Entered.Wait(TimeSpan.FromSeconds(5)));
            await Task.Run(() =>
            {
                Assert.IsTrue(vfs.TryLookup("fast/file", out _));
                Assert.AreEqual("file", vfs.List("fast").Single().Name);
                Assert.IsTrue(vfs.Probe("fast/file").Single().Effective);
                using var reader = new StreamReader(vfs.OpenRead("fast/file", out _));
                Assert.AreEqual("payload", reader.ReadToEnd());
            }).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsFalse(slow.IsCompleted);
        }
        finally { source.Release.Set(); await slow.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
}
