using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

/// <summary>
/// Resolving a child by name must use the listing its visibility walk already resolved:
/// that listing carries the child's header, so asking the layers by exact path — and the
/// source on top of that — repeats a fact the resolver is holding. These pin both halves:
/// no source lookup for a listed child, and the case-insensitive spelling NTFS expects.
/// </summary>
[TestClass]
public sealed class HeaderFromListingTests
{
    private static VirtualFileSystem Warm(FakeSourceLayer source, MemoryCacheLayer memory, out string initial)
    {
        var vfs = new VirtualFileSystem(new ILayer[] { memory, source });
        var listing = vfs.List("dir");
        initial = string.Join(",", listing.Select(e => e.Name));
        // The listing resolution itself is not what these tests are about.
        source.EntryLookups.Clear();
        source.ListedDirectories.Clear();
        return vfs;
    }

    [TestMethod]
    public void A_listed_child_resolves_without_a_source_lookup()
    {
        var source = new FakeSourceLayer();
        source.AddFile("dir/child.txt", "content");
        using var memory = new MemoryCacheLayer();
        var vfs = Warm(source, memory, out var initial);
        Assert.AreEqual("child.txt", initial);

        Assert.IsTrue(vfs.TryLookup("dir/child.txt", out var entry));
        Assert.IsFalse(entry.IsDir);
        Assert.AreEqual(0, source.EntryLookups.Count, "the parent listing already carried this header");
        Assert.AreEqual(0, source.ListedDirectories.Count, "the listing came from the cache");
    }

    [TestMethod]
    public void A_child_named_with_another_case_resolves_to_the_listed_spelling()
    {
        var source = new FakeSourceLayer();
        source.AddFile("dir/child.txt", "content");
        using var memory = new MemoryCacheLayer();
        var vfs = Warm(source, memory, out _);

        // NTFS is case-insensitive and the mount advertises that (CaseSensitiveSearch=false);
        // the listing member table is the same comparison, so the answer is already here.
        Assert.IsTrue(vfs.TryLookup("DIR/Child.TXT", out var entry));
        Assert.AreEqual("child.txt", entry.Name);
        Assert.AreEqual(0, source.EntryLookups.Count);
        Assert.AreEqual(0, source.ListedDirectories.Count);
    }

    [TestMethod]
    public void A_layer_above_the_listing_still_overrides_the_listed_header()
    {
        var source = new FakeSourceLayer();
        source.AddFile("dir/child.txt", "0123456789");
        using var memory = new MemoryCacheLayer();
        memory.StoreObject("dir/child.txt",
            VirtualObject.HeaderOnly(new VfsEntry { Name = "child.txt", IsDir = false, Size = 999, TimeUtc = DateTime.UtcNow }));
        var vfs = new VirtualFileSystem(new ILayer[] { memory, source });

        Assert.IsTrue(vfs.TryLookup("dir/child.txt", out var entry));
        Assert.AreEqual(999L, entry.Size, "the header record above the listing wins");
        Assert.IsFalse(source.EntryLookups.Contains("dir/child.txt"));
    }

    [TestMethod]
    public void A_child_deleted_in_an_upper_layer_is_not_resurrected_by_the_listing()
    {
        using var temp = new TempDir();
        var overlay = new OverlayLayer(temp.Sub("overlay"));
        var source = new FakeSourceLayer();
        source.AddFile("dir/child.txt", "content");
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => source.Generation, 1024 * 1024);
        var vfs = new VirtualFileSystem(new ILayer[] { overlay, cache, source });

        vfs.Delete("dir/child.txt");
        Assert.IsFalse(vfs.TryLookup("dir/child.txt", out _));
    }
}
