using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public class ObjectNamespaceTests
{
    [TestMethod]
    public async Task Cached_directory_listing_does_not_query_source_child_headers()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer(); source.AddFile("folder/file", "payload");
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => "1", 1048576);
        var cold = new VirtualFileSystem(new ILayer[] { cache, source });
        cold.List(""); cold.List("folder");
        await cache.WaitForPendingFillsAsync();
        // No source is available in this chain. Headers in the cached directory
        // must suffice; resolving children must not fall through to lower layers.
        var unavailable = new HeaderFailureLayer();
        var warm = new VirtualFileSystem(new ILayer[] { cache, unavailable });
        Assert.AreEqual("file", warm.List("folder").Single().Name);
    }
    private sealed class HeaderFailureLayer : ILayer
    {
        public string Name => "unavailable";
        public string Generation => "1";
        public bool Writable => false;
        public bool TryGetEntry(string path, out VfsEntry entry) => throw new InvalidOperationException("Source header requested");
        public IReadOnlyList<VfsEntry> ListOwn(string path) => throw new InvalidOperationException("Source listing requested");
        public bool TryOpenRead(string path, out VfsEntry entry, out Stream stream) => throw new InvalidOperationException("Source content requested");
    }

    [TestMethod]
    public void Listing_and_directory_metadata_do_not_enumerate_child_directories()
    {
        var source = new FakeSourceLayer();
        source.AddFile("folder/sub/file", "payload");
        var vfs = new VirtualFileSystem(new[] { source });

        CollectionAssert.AreEqual(new[] { "folder" }, vfs.List("").Select(c => c.Name).ToArray());
        Assert.IsTrue(vfs.TryLookup("folder", out var header));
        Assert.IsTrue(header.IsDir);
        Assert.IsTrue(vfs.Probe("folder").Single().Effective);
        Assert.IsFalse(source.ListedDirectories.Contains("folder"));
        Assert.IsFalse(source.ListedDirectories.Contains("folder/sub"));

        CollectionAssert.AreEqual(new[] { "sub" }, vfs.List("folder").Select(c => c.Name).ToArray());
        Assert.IsTrue(source.ListedDirectories.Contains("folder"));
        Assert.IsFalse(source.ListedDirectories.Contains("folder/sub"));
    }

    [TestMethod]
    public void Listing_preserves_upper_child_header_overrides_without_loading_their_children()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        source.AddFile("folder/file", "source");
        var overlay = new OverlayLayer(temp.Sub("overlay"), temp.Sub("state"));
        using var content = new MemoryStream(new byte[123]);
        overlay.TryApply(new[] { new ObjectChange("folder", new VirtualObject(new VfsEntry { Name = "folder", Size = 123 }), content) });
        var vfs = new VirtualFileSystem(new ILayer[] { overlay, source });

        var child = vfs.List("").Single();
        Assert.IsFalse(child.IsDir);
        Assert.AreEqual(123L, child.Size);
        Assert.IsFalse(source.ListedDirectories.Contains("folder"));
    }

    [TestMethod]
    public void Opening_directory_caches_its_complete_listing_and_changes_its_origin()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer(); source.AddFile("folder/file", "payload");
        var overlay = new OverlayLayer(temp.Sub("overlay"), temp.Sub("state"));
        var cache = new LruCacheLayer(temp.Sub("cache"), () => source.Generation, 1048576);
        var vfs = new VirtualFileSystem(new ILayer[] { overlay, cache, source });
        Assert.AreEqual("fake", vfs.Probe("folder").Single(p => p.Effective).Name);
        Assert.IsFalse(cache.TryReadObject("folder", out _));
        CollectionAssert.AreEqual(new[] { "file" }, vfs.List("folder").Select(c => c.Name).ToArray());
        Assert.IsTrue(cache.TryReadObject("folder", out var directory));
        Assert.IsTrue(directory.Header.IsDir);
        Assert.AreEqual("file", directory.Children!.Single().Name);
        Assert.AreEqual("cache", vfs.Probe("folder").Single(p => p.Effective).Name);
        vfs.Delete("folder/file");
        Assert.AreEqual(0, vfs.List("folder").Count);
        Assert.AreEqual("overlay", vfs.Probe("folder").Single(p => p.Effective).Name);
    }
    private sealed class DecliningLayer(bool fail) : ILayer
    {
        public string Name => "declining";
        public string Generation => "0";
        public bool Writable => true;
        public bool TryGetEntry(string path, out VfsEntry entry) { entry = null!; return false; }
        public IReadOnlyList<VfsEntry> ListOwn(string path) => Array.Empty<VfsEntry>();
        public bool TryOpenRead(string path, out VfsEntry entry, out Stream stream) { entry = null!; stream = null!; return false; }
        public bool TryApply(IReadOnlyList<ObjectChange> changes)
        { if (fail) throw new IOException("Storage failure"); return false; }
    }

    [TestMethod]
    public void Unsupported_write_falls_through_but_storage_errors_do_not()
    {
        using var temp = new TempDir();
        var overlay = new OverlayLayer(temp.Sub("overlay"), temp.Sub("state"));
        var fallback = new VirtualFileSystem(new ILayer[] { new DecliningLayer(false), overlay });
        using (var write = fallback.OpenWrite("file", true, out _)) write.Write("data"u8);
        var failure = new VirtualFileSystem(new ILayer[] { new DecliningLayer(true), overlay });
        Assert.ThrowsException<IOException>(() => failure.OpenWrite("bad", true, out _));
        Assert.IsFalse(overlay.TryGetEntry("bad", out _));
    }
    [TestMethod]
    public void Delete_is_parent_snapshot_and_blocks_direct_and_descendant_access_after_reopen()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer(); source.AddFile("folder/file", "source"); source.AddFile("other", "keep");
        var overlay = new OverlayLayer(temp.Sub("overlay"), temp.Sub("state"));
        var vfs = new VirtualFileSystem(new ILayer[] { overlay, source });
        vfs.Delete("folder");
        var reopened = new VirtualFileSystem(new ILayer[] { new OverlayLayer(temp.Sub("overlay"), temp.Sub("state")), source });
        Assert.IsFalse(reopened.TryLookup("folder/file", out _));
        Assert.ThrowsException<VfsNotFoundException>(() => reopened.OpenRead("folder/file", out _));
        CollectionAssert.AreEqual(new[] { "other" }, reopened.List("").Select(c => c.Name).ToArray());
        Assert.IsTrue(overlay.TryReadObject("", out var root));
        Assert.AreEqual(1, root.Children!.Count);
    }

    [TestMethod]
    public void Complete_upper_directory_does_not_union_lower_members()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer(); source.AddFile("lower", "lower");
        var overlay = new OverlayLayer(temp.Sub("overlay"), temp.Sub("state"));
        overlay.TryApply(new[] { new ObjectChange("", VirtualObject.Directory("", Array.Empty<VfsEntry>())) });
        var vfs = new VirtualFileSystem(new ILayer[] { overlay, source });
        Assert.AreEqual(0, vfs.List("").Count);
        Assert.IsFalse(vfs.TryLookup("lower", out _));
    }

    [TestMethod]
    public void Directory_rename_copies_entire_visible_subtree_and_updates_both_parents()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer(); source.AddFile("old/sub/file", "payload"); source.AddDir("target");
        var vfs = new VirtualFileSystem(new ILayer[] { new OverlayLayer(temp.Sub("overlay"), temp.Sub("state")), source });
        vfs.Rename("old", "target/new", false);
        Assert.IsFalse(vfs.TryLookup("old/sub/file", out _));
        using var reader = new StreamReader(vfs.OpenRead("target/new/sub/file", out _));
        Assert.AreEqual("payload", reader.ReadToEnd());
    }

    [TestMethod]
    public void Cache_user_writes_are_not_evicted_and_survive_source_generation_change()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer(); source.AddFile("file", "original");
        var cache = new LruCacheLayer(temp.Sub("cache"), () => source.Generation, 200);
        var vfs = new VirtualFileSystem(new ILayer[] { cache, source });
        using (var write = vfs.OpenWrite("file", true, out _)) write.Write("edited"u8);
        cache.Purge(); source.BumpGeneration("next");
        using var reader = new StreamReader(vfs.OpenRead("file", out _));
        Assert.AreEqual("edited", reader.ReadToEnd());
    }

    [TestMethod]
    public void Case_only_rename_keeps_the_object()
    {
        using var temp = new TempDir();
        var vfs = new VirtualFileSystem(new[] { new OverlayLayer(temp.Sub("overlay"), temp.Sub("state")) });
        using (var write = vfs.OpenWrite("name", true, out _)) write.Write("data"u8);
        vfs.Rename("name", "NAME", false);
        using var reader = new StreamReader(vfs.OpenRead("NAME", out _));
        Assert.AreEqual("data", reader.ReadToEnd());
    }
}
