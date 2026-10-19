using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

/// <summary>
/// Resolving a path by name is the one read a listing cannot answer for: the parent's
/// listing holds the same header, but only for whoever enumerates it. These cover the
/// header-only records the resolver fills upward, so reopening one file is answered in
/// memory instead of walking the chain down to the provider every time — which is what a
/// disk cache read-back per open looked like.
/// </summary>
[TestClass]
public sealed class HeaderRecordTests
{
    private static VfsEntry File(string name, long size) => new() { Name = name, IsDir = false, Size = size };
    private static VfsEntry Folder(string name) => new() { Name = name, IsDir = true };

    [TestMethod]
    public void A_header_is_answered_by_TryGetEntry_and_refused_by_TryReadObject()
    {
        using var cache = new MemoryCacheLayer();
        cache.StoreObject("dir/file.bin", VirtualObject.HeaderOnly(File("file.bin", 42)));
        cache.StoreObject("dir/sub", VirtualObject.HeaderOnly(Folder("sub")));

        // The same key normalization the directory snapshots use.
        Assert.IsTrue(cache.TryGetEntry("DIR/FILE.BIN", out var file));
        Assert.IsFalse(file.IsDir);
        Assert.AreEqual(42L, file.Size);
        Assert.IsTrue(cache.TryGetEntry("dir/sub", out var folder));
        Assert.IsTrue(folder.IsDir);

        // A listing is read as the complete namespace value, so a header must not answer it.
        Assert.IsFalse(cache.TryReadObject("dir/file.bin", out _));
        Assert.IsFalse(cache.TryReadObject("dir/sub", out _));
        Assert.AreEqual(0, cache.ListOwn("dir/sub").Count);
    }

    [TestMethod]
    public void A_file_value_that_carries_content_is_still_refused()
    {
        using var cache = new MemoryCacheLayer();
        using var content = new MemoryStream(new byte[8]);
        cache.StoreObject("plain.bin", new VirtualObject(File("plain.bin", 8)), content);
        Assert.IsFalse(cache.TryGetEntry("plain.bin", out _), "file bytes belong to the block cache, not here");
        Assert.AreEqual(0L, cache.MemoryBytes);
    }

    [TestMethod]
    public void A_complete_listing_supersedes_the_header_for_the_same_path()
    {
        using var cache = new MemoryCacheLayer();
        cache.StoreObject("dir", VirtualObject.HeaderOnly(Folder("dir")));
        cache.StoreObject("dir", VirtualObject.Directory("dir", new[] { File("file", 1) }));

        Assert.IsTrue(cache.TryReadObject("dir", out var value), "the listing is what answers now");
        Assert.AreEqual("file", value.Children!.Single().Name);
        Assert.IsTrue(cache.TryGetEntry("dir", out var header));
        Assert.IsTrue(header.IsDir);
        Assert.IsTrue(cache.MemoryBytes <= cache.Capacity);
    }

    [TestMethod]
    public void Headers_share_the_capacity_and_evict_from_the_tail()
    {
        using var cache = new MemoryCacheLayer(4096, 4096);
        var keys = Enumerable.Range(0, 8).Select(i => "dir/child" + i).ToArray();
        foreach (var key in keys) cache.StoreObject(key, VirtualObject.HeaderOnly(File(VfsPath.Name(key), 1)));

        Assert.IsTrue(cache.MemoryBytes <= cache.Capacity, "headers must stay inside the existing budget");
        Assert.IsFalse(cache.TryGetEntry(keys[0], out _), "the oldest header went first");
        Assert.IsTrue(cache.TryGetEntry(keys[^1], out _));

        var usage = cache.GetStorageUsage();
        Assert.IsTrue(usage.MemoryBytes > 0, "the headers are memory this layer is holding");
        Assert.AreEqual(0, usage.FileObjects, "a header is not a cached file object");
        Assert.AreEqual(0, usage.DirectoryObjects);
        Assert.IsTrue(usage.CacheBytes <= cache.Capacity);
    }

    [TestMethod]
    public void A_generation_change_retires_header_records()
    {
        string generation = "v1";
        using var cache = new MemoryCacheLayer(generation: () => generation);
        cache.StoreObject("dir/file", VirtualObject.HeaderOnly(File("file", 3)));
        Assert.IsTrue(cache.TryGetEntry("dir/file", out _));

        generation = "v2";

        Assert.IsFalse(cache.TryGetEntry("dir/file", out _), "a header names a path in the tree it was resolved from");
        Assert.AreEqual(0L, cache.MemoryBytes);
    }

    [TestMethod]
    public void A_write_to_a_path_does_not_leave_its_own_header_behind()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer(); source.AddFile("dir/file", "payload");
        using var memory = new MemoryCacheLayer();
        var overlay = new OverlayLayer(temp.Sub("overlay"));
        var vfs = new VirtualFileSystem(new ILayer[] { memory, overlay, source });

        Assert.IsTrue(vfs.TryLookup("dir/file", out _));
        Assert.IsTrue(memory.TryGetEntry("dir/file", out var before));
        Assert.AreEqual(7L, before.Size);

        using (var write = vfs.OpenWrite("dir/file", true, out _)) write.Write("edited"u8);

        Assert.IsFalse(memory.TryGetEntry("dir/file", out _), "the layer's own change made the header stale");
        Assert.IsTrue(vfs.TryLookup("dir/file", out var after));
        Assert.AreEqual(6L, after.Size, "and the next lookup sees the write");
    }

    [TestMethod]
    public void A_header_only_directory_does_not_break_a_listing()
    {
        var source = new FakeSourceLayer(); source.AddFile("folder/file", "payload");
        using var memory = new MemoryCacheLayer();
        var vfs = new VirtualFileSystem(new ILayer[] { memory, source });

        Assert.IsTrue(vfs.TryLookup("folder", out var header));
        Assert.IsTrue(header.IsDir);
        Assert.IsTrue(memory.TryGetEntry("folder", out _), "the resolved path is recorded");
        Assert.IsFalse(memory.TryReadObject("folder", out _), "and is not mistaken for its listing");

        CollectionAssert.AreEqual(new[] { "file" }, vfs.List("folder").Select(c => c.Name).ToArray());
    }

    [TestMethod]
    public void Resolving_a_listed_path_never_asks_the_provider_by_name()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer(); source.AddFile("dir/video.bin", "payload");
        using var memory = new MemoryCacheLayer();
        // The production shape: memory above the durable read cache above the source.
        using var disk = new LruCacheLayer(temp.Sub("cache"), () => source.Generation, 4L * 1024 * 1024);
        var vfs = new VirtualFileSystem(new ILayer[] { memory, disk, source });
        vfs.Metrics = new MetricsCollector();
        // Nothing is counted until somebody asks for a window; this test asks, so it watches first.
        vfs.Metrics.Watch();

        // The visibility walk has to resolve the parent's listing; that listing carries the
        // child's header, so the provider is never asked for the path itself.
        Assert.IsTrue(vfs.TryLookup("dir/video.bin", out _, recordAccess: true));
        Assert.AreEqual(0, source.EntryLookups.Count(p => p == "dir/video.bin"));
        Assert.AreEqual("fake", vfs.Metrics.Snapshot("test", 60).Layers.Single(l => l.Hits > 0).Name);

        Assert.IsTrue(vfs.TryLookup("dir/video.bin", out _, recordAccess: true));

        var metrics = vfs.Metrics.Snapshot("test", 60);
        Assert.AreEqual(0, source.EntryLookups.Count(p => p == "dir/video.bin"), "no lookup ever reached the source by name");
        Assert.AreEqual(1L, metrics.Layers.Single(l => l.Name == "memory-cache").Hits,
            "the memory layer is what answered the second lookup");
        Assert.IsFalse(metrics.Layers.Any(l => l.Name == "cache" && l.Hits > 0), "and the disk cache was not walked to at all");
    }

    [TestMethod]
    public void A_header_does_not_enter_the_read_ahead_accounting()
    {
        using var temp = new TempDir();
        using var layer = new PredictionCacheLayer(65536, () => "v1", new ReadPredictionOptions(), temp.Sub("held.json"));
        layer.StoreObject("dir/file", VirtualObject.HeaderOnly(File("file", 4)));

        Assert.AreEqual(0, layer.HeldObjects, "a header is not a guess this layer chose to keep");
        Assert.AreEqual(0L, layer.MemoryBytes);
        Assert.AreEqual(0L, layer.Wasted, "and must not be counted as one that went unread");
        Assert.IsFalse(layer.TryReadObject("dir/file", out _));
    }

    [TestMethod]
    public async Task A_header_record_never_reaches_the_durable_store()
    {
        using var temp = new TempDir();
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => "v1", 1048576);
        cache.StoreObject("dir/file.bin", VirtualObject.HeaderOnly(File("file.bin", 5)));
        cache.StoreObject("dir/sub", VirtualObject.HeaderOnly(Folder("sub")));
        await cache.WaitForPendingFillsAsync();

        Assert.AreEqual(0, cache.Count, "a header-only record is not a durable record");
        Assert.AreEqual(0L, cache.GetStorageUsage().ObjectBytes, "and nothing about one is written");
        // TryReadObject hands a row back as a complete directory listing.
        Assert.IsFalse(cache.TryReadObject("dir/sub", out _));
        Assert.IsFalse(cache.TryGetEntry("dir/sub", out _));
    }
}
