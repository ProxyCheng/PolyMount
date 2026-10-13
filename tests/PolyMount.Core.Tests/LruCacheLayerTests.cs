using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public class LruCacheLayerTests
{
    private static VfsEntry FileEntry(string name, long size) =>
        new() { Name = name, IsDir = false, Size = size };

    [TestMethod]
    public void Store_then_read_hits_cache()
    {
        using var t = new TempDir();
        var cache = new LruCacheLayer(t.Sub("cache"), () => "v1", 1024 * 1024);

        using var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("payload"));
        cache.Store("a/b.txt", "v1", FileEntry("b.txt", 7), content);

        Assert.IsTrue(cache.TryGetEntry("a/b.txt", out var e));
        Assert.AreEqual(7, e.Size);
        Assert.IsTrue(cache.TryOpenRead("a/b.txt", out _, out var s));
        using var r = new StreamReader(s);
        Assert.AreEqual("payload", r.ReadToEnd());
    }

    [TestMethod]
    public void Generation_change_isolates_entries()
    {
        using var t = new TempDir();
        string gen = "v1";
        var cache = new LruCacheLayer(t.Sub("cache"), () => gen, 1024 * 1024);

        using (var c = new MemoryStream(new byte[] { 1, 2, 3 }))
            cache.Store("f", "v1", FileEntry("f", 3), c);
        Assert.IsTrue(cache.TryGetEntry("f", out _));

        gen = "v2"; // upstream content moved on; old cached blob must no longer be visible
        Assert.IsFalse(cache.TryGetEntry("f", out _));
    }

    [TestMethod]
    public void Eviction_respects_capacity()
    {
        using var t = new TempDir();
        var cache = new LruCacheLayer(t.Sub("cache"), () => "v1", capacityBytes: 250);

        using (var c = new MemoryStream(new byte[100])) cache.Store("a", "v1", FileEntry("a", 100), c);
        using (var c = new MemoryStream(new byte[100])) cache.Store("b", "v1", FileEntry("b", 100), c);

        // 12 bytes > 10 capacity, so the oldest ("a") must have been evicted.
        Assert.IsTrue(cache.TotalBytes <= 250);
        Assert.IsFalse(cache.TryGetEntry("a", out _));
        Assert.IsTrue(cache.TryGetEntry("b", out _));
    }

    [TestMethod]
    public void Purge_clears_everything()
    {
        using var t = new TempDir();
        var cache = new LruCacheLayer(t.Sub("cache"), () => "v1", 1024);
        using (var c = new MemoryStream(new byte[3])) cache.Store("a", "v1", FileEntry("a", 3), c);
        Assert.AreEqual(1, cache.Count);

        cache.Purge();
        Assert.AreEqual(0, cache.Count);
        Assert.AreEqual(0, cache.TotalBytes);
        Assert.IsFalse(cache.TryGetEntry("a", out _));
    }

    [TestMethod]
    public void ListOwn_is_always_empty()
    {
        using var t = new TempDir();
        var cache = new LruCacheLayer(t.Sub("cache"), () => "v1", 1024);
        Assert.AreEqual(0, cache.ListOwn("").Count);
    }
}
