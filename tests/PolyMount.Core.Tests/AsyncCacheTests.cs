using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class AsyncCacheTests
{
    [TestMethod]
    public async Task Full_queue_skips_optional_fill_instead_of_waiting_for_writer()
    {
        using var temp = new TempDir();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => "1", 1048576);
        cache.BeforePersist = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        try
        {
            cache.StoreObject("first", VirtualObject.Directory("first", Array.Empty<VfsEntry>()));
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            await Task.Run(() =>
            {
                for (int i = 0; i < 200; i++) cache.StoreObject("dir" + i, VirtualObject.Directory("dir" + i, Array.Empty<VfsEntry>()));
                Assert.IsTrue(cache.Count <= 129);
                Assert.IsFalse(cache.TryGetEntry("dir199", out _));
                Assert.IsTrue(cache.TryGetEntry("first", out _));
            }).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { release.Set(); }
        await cache.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task Blocked_disk_writer_does_not_block_reads_or_fills_and_retains_closed_source()
    {
        using var temp = new TempDir();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => "1", 1048576);
        cache.BeforePersist = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        string source = temp.Sub("source");
        File.WriteAllText(source, "payload");
        try
        {
            using (var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                4096, FileOptions.DeleteOnClose))
                cache.StoreObject("file", new(new VfsEntry { Name = "file", Size = 7 }), file);
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            await Task.Run(() =>
            {
                Assert.IsTrue(cache.TryOpenRead("file", out _, out var stream));
                using (var reader = new StreamReader(stream)) Assert.AreEqual("payload", reader.ReadToEnd());
                cache.StoreObject("folder", VirtualObject.Directory("folder", Array.Empty<VfsEntry>()));
                Assert.IsTrue(cache.TryGetEntry("folder", out _));
            }).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { release.Set(); }
        await cache.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var reopened = new LruCacheLayer(temp.Sub("cache"), () => "1", 1048576);
        Assert.IsTrue(reopened.TryOpenRead("file", out _, out var persisted));
        using var result = new StreamReader(persisted);
        Assert.AreEqual("payload", result.ReadToEnd());
    }

    [TestMethod]
    public async Task Purge_does_not_allow_queued_fills_to_reappear()
    {
        using var temp = new TempDir();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => "1", 1048576);
        cache.BeforePersist = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        Task? purge = null;
        try
        {
            cache.StoreObject("first", VirtualObject.Directory("first", Array.Empty<VfsEntry>()));
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            cache.StoreObject("second", VirtualObject.Directory("second", Array.Empty<VfsEntry>()));
            purge = Task.Run(cache.Purge);
        }
        finally { release.Set(); }
        await purge!.WaitAsync(TimeSpan.FromSeconds(5));
        await cache.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, cache.Count);
        using var reopened = new LruCacheLayer(temp.Sub("cache"), () => "1", 1048576);
        Assert.AreEqual(0, reopened.Count);
    }
}
