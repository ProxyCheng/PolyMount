using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class MemoryDirectoryCacheTests
{
    [TestMethod]
    public void Local_directory_preset_places_memory_cache_above_writes()
    {
        var preset = LayerPresetRegistry.All.Single(p => p.Id == "local-directory");
        CollectionAssert.AreEqual(new[] { "memory-cache", "overlay", "dir" }, preset.Layers.Select(l => l.Type).ToArray());
        Assert.AreEqual(preset.Layers.Count, preset.Layers.Select(l => l.Id).Distinct().Count());
        LayerRegistry.Validate(preset.Layers[0]);
    }

    private sealed class DelayedSource : ILayer
    {
        public readonly ManualResetEventSlim Started = new(), Resume = new();
        public string Name => "delayed";
        public string Generation => "v1";
        public bool Writable => false;
        public bool TryGetEntry(string path, out VfsEntry entry) { entry = new() { Name = path, IsDir = true }; return true; }
        public IReadOnlyList<VfsEntry> ListOwn(string path)
        {
            Started.Set();
            if (!Resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return Array.Empty<VfsEntry>();
        }
        public bool TryOpenRead(string path, out VfsEntry entry, out Stream stream) { entry = null!; stream = null!; return false; }
    }

    [TestMethod]
    public async Task Read_started_before_write_cannot_refill_an_old_directory_snapshot()
    {
        using var temp = new TempDir();
        using var cache = new MemoryCacheLayer();
        var source = new DelayedSource();
        var overlay = new OverlayLayer(temp.Sub("overlay"));
        var vfs = new VirtualFileSystem(new ILayer[] { cache, overlay, source });
        var read = Task.Run(() => vfs.List(""));
        try
        {
            Assert.IsTrue(source.Started.Wait(TimeSpan.FromSeconds(5)));
            // Publish a lower-layer write while the source's old snapshot is in flight.
            overlay.TryApply(new[] { new ObjectChange("", VirtualObject.Directory("", new[] { new VfsEntry { Name = "new", IsDir = true } })) });
            vfs.InvalidateCaches();
        }
        finally { source.Resume.Set(); }
        await read;
        Assert.AreEqual("new", vfs.List("").Single().Name);
        Assert.AreEqual("new", cache.ListOwn("").Single().Name);
    }

    [TestMethod]
    public void Directory_reads_are_cached_and_generation_change_refetches()
    {
        var source = new FakeSourceLayer(); source.AddFile("dir/first", "one");
        using var cache = new MemoryCacheLayer(generation: () => source.Generation);
        var vfs = new VirtualFileSystem(new ILayer[] { cache, source });
        Assert.AreEqual("first", vfs.List("dir").Single().Name);
        int reads = source.ListedDirectories.Count;
        Assert.AreEqual("first", vfs.List("DIR").Single().Name);
        Assert.AreEqual(reads, source.ListedDirectories.Count);
        source.AddFile("dir/second", "two"); source.BumpGeneration("g1");
        Assert.AreEqual(2, vfs.List("dir").Count);
        Assert.IsTrue(source.ListedDirectories.Count > reads);
        cache.Invalidate(); Assert.IsFalse(cache.TryReadObject("dir", out _));
    }

    [TestMethod]
    public void Writes_update_parent_snapshots_keep_unrelated_lists_and_forget_deleted_subtrees()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        source.AddFile("dir/old", "one"); source.AddFile("other/file", "two");
        using var cache = new MemoryCacheLayer();
        var overlay = new OverlayLayer(temp.Sub("overlay"));
        var vfs = new VirtualFileSystem(new ILayer[] { new NamedCacheLayer("ram", cache), overlay, source });
        vfs.List("dir"); vfs.List("other");
        vfs.CreateDirectory("dir/sub");
        using (var write = vfs.OpenWrite("dir/sub/new", true, out _))
        { write.Write(new byte[] { 1, 2, 3 }); write.Flush(); }
        Assert.IsTrue(cache.TryReadObject("dir/sub", out var sub));
        Assert.AreEqual(3L, sub.Children!.Single().Size);
        vfs.Rename("dir/sub/new", "dir/renamed", false);
        Assert.AreEqual(0, vfs.List("dir/sub").Count);
        Assert.IsTrue(cache.TryReadObject("dir", out var directory));
        CollectionAssert.AreEquivalent(new[] { "old", "sub", "renamed" }, directory.Children!.Select(c => c.Name).ToArray());
        vfs.Delete("dir/sub"); Assert.IsFalse(cache.TryReadObject("dir/sub", out _));
        vfs.Delete("dir/old");
        Assert.AreEqual("renamed", vfs.List("dir").Single().Name);
        int reads = source.ListedDirectories.Count;
        Assert.AreEqual("file", vfs.List("other").Single().Name);
        Assert.AreEqual(reads, source.ListedDirectories.Count);
        Assert.IsTrue(cache.TryReadObject("other", out _));
        vfs.List("dir"); vfs.List("dir");
        Assert.AreEqual(reads, source.ListedDirectories.Count);
        Assert.AreEqual(0L, cache.GetStorageUsage().DiskBytes);
    }

    [TestMethod]
    public void Directory_snapshots_and_blocks_share_bounded_lru_and_snapshot_children()
    {
        using var cache = new MemoryCacheLayer(8192, 4096);
        var children = new[] { new VfsEntry { Name = "original" } };
        cache.StoreObject("dir", VirtualObject.Directory("dir", children));
        children[0] = new VfsEntry { Name = "changed" };
        Assert.AreEqual("original", cache.ListOwn("dir").Single().Name);
        using var read = cache.WrapStream("file", "v1", new MemoryStream(new byte[8192]), false);
        read.ReadByte(); read.Position = 4096; read.ReadByte();
        Assert.IsTrue(cache.MemoryBytes <= cache.Capacity);
        Assert.IsFalse(cache.TryReadObject("dir", out _));
        cache.StoreObject("huge", VirtualObject.Directory("huge", Enumerable.Range(0, 100).Select(i => new VfsEntry { Name = i.ToString() })));
        Assert.IsFalse(cache.TryReadObject("huge", out _));
        cache.ReleaseObject("file"); Assert.AreEqual(0L, cache.MemoryBytes);
    }
}
