using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

/// <summary>
/// Content a source can name outlives the namespace key that admitted it. The version here is
/// deliberately opaque — the tests never say what produced it — because a cache must not know
/// which backend it is caching for.
/// </summary>
[TestClass]
public sealed class ContentVersionTests
{
    private static VfsEntry FileEntry(string name, long size, string version) =>
        new() { Name = name, IsDir = false, Size = size, ContentVersion = version };

    [TestMethod]
    public async Task Content_is_served_by_version_after_the_namespace_moves()
    {
        using var temp = new TempDir();
        string generation = "1";
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => generation, 1 << 20);

        using (var content = new MemoryStream("payload"u8.ToArray()))
            cache.StoreObject("f", new VirtualObject(FileEntry("f", 7, "42")), content);
        await cache.WaitForPendingFillsAsync();

        generation = "2";
        Assert.IsFalse(cache.TryReadObject("f", out _), "the generation-keyed namespace must have moved on");
        Assert.IsFalse(cache.TryOpenVersioned("f", FileEntry("f", 7, "41"), out _, out _),
            "a different version is not that content");
        Assert.IsFalse(cache.TryOpenVersioned("f", FileEntry("f", 8, "42"), out _, out _),
            "a header that disagrees about its size is not that content");
        Assert.IsTrue(cache.TryOpenVersioned("f", FileEntry("f", 7, "42"), out var entry, out var stream));
        using (stream) Assert.AreEqual("payload", new StreamReader(stream).ReadToEnd());
        Assert.AreEqual("42", entry.ContentVersion);
    }

    [TestMethod]
    public async Task Versioned_content_survives_a_restart()
    {
        using var temp = new TempDir();
        string generation = "1";
        using (var cache = new LruCacheLayer(temp.Sub("cache"), () => generation, 1 << 20))
        {
            using var content = new MemoryStream("payload"u8.ToArray());
            cache.StoreObject("f", new VirtualObject(FileEntry("f", 7, "42")), content);
            await cache.WaitForPendingFillsAsync();
        }

        generation = "2";
        using var reopened = new LruCacheLayer(temp.Sub("cache"), () => generation, 1 << 20);
        Assert.IsTrue(reopened.TryOpenVersioned("f", FileEntry("f", 7, "42"), out _, out var stream),
            "the record stores the version, so a restart can still match on it");
        using (stream) Assert.AreEqual("payload", new StreamReader(stream).ReadToEnd());
    }

    [TestMethod]
    public async Task A_namespace_move_fetches_only_what_the_source_says_changed()
    {
        var source = new FakeSourceLayer();
        source.AddFileBytes("dir/a.txt", "A"u8.ToArray(), contentVersion: "5");
        source.AddFileBytes("dir/b.txt", "B"u8.ToArray(), contentVersion: "5");
        using var temp = new TempDir();
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => source.Generation, 1 << 20);
        var vfs = new VirtualFileSystem(new ILayer[] { cache, source });

        using (var a = vfs.OpenRead("dir/a.txt", out _)) Assert.AreEqual("A", new StreamReader(a).ReadToEnd());
        using (var b = vfs.OpenRead("dir/b.txt", out _)) Assert.AreEqual("B", new StreamReader(b).ReadToEnd());
        await cache.DrainAsync();
        int fetched = source.OpenReadCount;
        Assert.AreEqual(2, fetched);

        // The mount moves to a new revision; one of the two files changed in it.
        source.BumpGeneration("g1");
        source.SetContentVersion("dir/b.txt", "6");

        using (var a = vfs.OpenRead("dir/a.txt", out var entry))
        {
            Assert.AreEqual("A", new StreamReader(a).ReadToEnd());
            Assert.AreEqual("5", entry.ContentVersion);
        }
        Assert.AreEqual(fetched, source.OpenReadCount, "a did not change and must come from the cache");

        using (var b = vfs.OpenRead("dir/b.txt", out _)) Assert.AreEqual("B", new StreamReader(b).ReadToEnd());
        Assert.AreEqual(fetched + 1, source.OpenReadCount, "b changed and must be fetched");
    }

    [TestMethod]
    public void A_source_without_versions_keeps_the_generation_behaviour()
    {
        using var temp = new TempDir();
        string generation = "1";
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => generation, 1 << 20);
        using (var content = new MemoryStream("payload"u8.ToArray()))
            cache.StoreObject("f", new VirtualObject(new VfsEntry { Name = "f", IsDir = false, Size = 7 }), content);
        cache.WaitForPendingFillsAsync().GetAwaiter().GetResult();

        generation = "2";
        Assert.IsFalse(cache.TryOpenVersioned("f", new VfsEntry { Name = "f", IsDir = false, Size = 7 }, out _, out _),
            "nothing can be matched on without a version");
    }
}
