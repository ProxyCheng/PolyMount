using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class CacheStorageCheckTests
{
    [TestMethod]
    public async Task Reused_blob_handles_have_independent_positions_and_survive_eviction()
    {
        using var temp = new TempDir();
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => "1", 1048576);
        using (var content = new MemoryStream("abcdefgh"u8.ToArray()))
            cache.StoreObject("file", new(new VfsEntry { Name = "file", Size = 8 }), content);
        await cache.WaitForPendingFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(cache.TryOpenRead("file", out _, out var first));
        Assert.IsTrue(cache.TryOpenRead("file", out _, out var second));
        using (first)
        using (second)
        {
            first.Position = 5;
            Assert.AreEqual((int)'f', first.ReadByte());
            Assert.AreEqual((int)'a', second.ReadByte());
            cache.Purge();
            Assert.IsFalse(cache.TryGetEntry("file", out _));
            Assert.AreEqual((int)'g', first.ReadByte());
            Assert.AreEqual((int)'b', second.ReadByte());
        }
    }

    [TestMethod]
    public async Task Blocked_storage_validation_does_not_block_metadata_hits()
    {
        using var temp = new TempDir();
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => "1", 1048576);
        cache.StoreObject("folder", VirtualObject.Directory("folder", Array.Empty<VfsEntry>()));
        await cache.WaitForPendingFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        cache.BeforeStorageCheck = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        var check = Task.Run(cache.ValidateStorage);
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            await Task.Run(() =>
            {
                for (int i = 0; i < 1000; i++) Assert.IsTrue(cache.TryGetEntry("folder", out _));
            }).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsFalse(check.IsCompleted);
        }
        finally { release.Set(); await check.WaitAsync(TimeSpan.FromSeconds(5)); cache.BeforeStorageCheck = null; }
    }

    [TestMethod]
    public async Task Missing_blob_falls_back_on_open_before_background_validation()
    {
        using var temp = new TempDir();
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => "1", 1048576);
        using (var content = new MemoryStream("payload"u8.ToArray()))
            cache.StoreObject("file", new(new VfsEntry { Name = "file", Size = 7 }), content);
        await cache.WaitForPendingFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        string blob = Directory.GetFiles(temp.Sub("cache", "reads", "objects-v2"), "*.blob").Single();
        File.Delete(blob);
        Assert.IsFalse(cache.TryOpenRead("file", out _, out _));
        Assert.IsFalse(cache.TryGetEntry("file", out _));
    }
}
