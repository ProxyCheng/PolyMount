using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

/// <summary>
/// Namespace records and file content need opposite eviction policies. Content is
/// re-fetchable and bounded by capacity; a directory is on the path of every object
/// beneath it, so dropping one turns into a source fetch for all of them.
/// </summary>
[TestClass]
public sealed class NamespaceResidencyTests
{
    private static VirtualObject Folder(string path) =>
        VirtualObject.Directory(path, new[] { new VfsEntry { Name = "child" } });

    private static VfsEntry FileEntry(string name, long size) => new() { Name = name, IsDir = false, Size = size };

    [TestMethod]
    public async Task Namespace_dropped_from_the_index_is_still_served()
    {
        using var temp = new TempDir();
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => "v1", capacityBytes: 1200);

        cache.StoreObject("keep", Folder("keep"));
        await cache.WaitForPendingFillsAsync();                  // "keep" is durable now

        // Pressure enough to push the oldest entries out of the resident index.
        for (int i = 0; i < 8; i++) cache.StoreObject("filler" + i, Folder("filler" + i));
        await cache.WaitForPendingFillsAsync();

        Assert.IsTrue(cache.Count < 4, "the resident index should have been trimmed");
        Assert.IsTrue(cache.TryReadObject("keep", out var value), "a namespace record must survive index eviction");
        Assert.AreEqual("child", value.Children!.Single().Name);
    }

    [TestMethod]
    public async Task Evicted_content_leaves_the_store()
    {
        using var temp = new TempDir();
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => "v1", capacityBytes: 1200);

        using (var content = new MemoryStream(new byte[600])) cache.Store("data", "v1", FileEntry("data", 600), content);
        await cache.WaitForPendingFillsAsync();
        Assert.IsTrue(cache.TryGetEntry("data", out _));

        for (int i = 0; i < 8; i++)
            using (var content = new MemoryStream(new byte[600]))
                cache.Store("more" + i, "v1", FileEntry("more" + i, 600), content);
        await cache.WaitForPendingFillsAsync();

        Assert.IsFalse(cache.TryGetEntry("data", out _), "reclaimed content must not linger in the store");
    }

    [TestMethod]
    public void Store_answers_from_disk_when_the_resident_index_is_bounded()
    {
        using var temp = new TempDir();
        var seed = new ObjectDatabase(temp.Sub("seed"), false, recoverable: true, metadataBudget: 1024);
        for (int i = 0; i < 6; i++) seed.Apply(new[] { new ObjectChange("k" + i, Folder("k" + i)) });
        // The budget admits nothing, so every row must come back from disk.
        var reopened = new ObjectDatabase(temp.Sub("seed"), false, recoverable: true, metadataBudget: 1024);
        Assert.AreEqual(0, reopened.Records.Count, "this test needs an index that holds nothing");
        for (int i = 0; i < 6; i++)
        {
            Assert.IsTrue(reopened.TryGet("k" + i, out var value), $"k{i} should be read back from disk");
            Assert.AreEqual("k" + i, value.Header.Name);
        }
    }

    [TestMethod]
    public void Clear_removes_rows_the_index_never_held()
    {
        using var temp = new TempDir();
        var seed = new ObjectDatabase(temp.Sub("db"), false, recoverable: true, metadataBudget: 1024);
        for (int i = 0; i < 5; i++) seed.Apply(new[] { new ObjectChange("k" + i, Folder("k" + i)) });

        var bounded = new ObjectDatabase(temp.Sub("db"), false, recoverable: true, metadataBudget: 1024);
        Assert.AreEqual(0, bounded.Records.Count);
        bounded.Clear();

        var reopened = new ObjectDatabase(temp.Sub("db"), false, recoverable: true, metadataBudget: 1024);
        for (int i = 0; i < 5; i++)
            Assert.IsFalse(reopened.TryGet("k" + i, out _), $"k{i} survived Clear and would return after a restart");
    }

    [TestMethod]
    public async Task Ancestor_chain_survives_cache_index_eviction_without_touching_the_source()
    {
        var source = new FakeSourceLayer();
        source.AddFile("a/b/c/leaf.txt", "hello");
        using var temp = new TempDir();
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => source.Generation, capacityBytes: 4096);
        var vfs = new VirtualFileSystem(new ILayer[] { cache, source });

        Assert.AreEqual("leaf.txt", vfs.List("a/b/c").Single().Name);
        await cache.DrainAsync();                                 // the chain is durable now
        int reads = source.ListedDirectories.Count;

        // Churn the cache hard enough that the ancestors cannot all stay resident.
        for (int i = 0; i < 40; i++) cache.StoreObject("noise/" + i, Folder("noise/" + i));
        await cache.DrainAsync();
        Assert.IsTrue(cache.Count < 8, "the resident index should have been trimmed");

        Assert.AreEqual("leaf.txt", vfs.List("a/b/c").Single().Name);
        Assert.AreEqual(reads, source.ListedDirectories.Count, "re-resolving the chain must not go back to the source");
    }

    [TestMethod]
    public void A_write_leaves_caches_below_it_intact()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        source.AddFile("dir/file", "source");
        var overlay = new OverlayLayer(temp.Sub("overlay"));
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => source.Generation, 1024 * 1024);
        var vfs = new VirtualFileSystem(new ILayer[] { overlay, cache, source });

        Assert.AreEqual("file", vfs.List("dir").Single().Name);        // caches "dir" from the source
        int reads = source.ListedDirectories.Count;

        // An unrelated write used to purge the whole read cache, namespace included.
        using (var write = vfs.OpenWrite("elsewhere.txt", true, out _))
        {
            write.Write(new byte[] { 1, 2, 3 });
            write.Flush();
        }

        Assert.IsTrue(cache.TryReadObject("dir", out _), "the namespace record should have survived the write");
        Assert.AreEqual("file", vfs.List("dir").Single().Name);
        Assert.AreEqual(reads, source.ListedDirectories.Count, "the listing should still come from the cache");
    }
}
