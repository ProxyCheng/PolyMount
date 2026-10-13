using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public class VirtualFileSystemTests
{
    private sealed class Fixture : IDisposable
    {
        public TempDir Temp = new();
        public OverlayLayer Overlay = null!;
        public LruCacheLayer Cache = null!;
        public FakeSourceLayer Source = null!;
        public VirtualFileSystem Vfs = null!;

        public static Fixture Build()
        {
            var f = new Fixture();
            f.Overlay = new OverlayLayer(f.Temp.Sub("overlay"), f.Temp.Sub("state", "tombstones"));
            f.Source = new FakeSourceLayer();
            f.Cache = new LruCacheLayer(f.Temp.Sub("cache"), () => f.Source.Generation, 1024 * 1024);
            f.Vfs = new VirtualFileSystem(new ILayer[] { f.Overlay, f.Cache, f.Source });
            return f;
        }

        public void Dispose() { Cache.Dispose(); Temp.Dispose(); }
    }

    private static string ReadAll(Stream s)
    {
        s.Seek(0, SeekOrigin.Begin);
        using var r = new StreamReader(s, System.Text.Encoding.UTF8, leaveOpen: true);
        return r.ReadToEnd();
    }

    [TestMethod]
    public void Readonly_chain_reads_and_rejects_mutations()
    {
        var src = new FakeSourceLayer();
        src.AddFile("file", "read-only");
        var vfs = new VirtualFileSystem(new ILayer[] { src });
        using var read = vfs.OpenRead("file", out _);
        Assert.AreEqual("read-only", ReadAll(read));
        Assert.ThrowsException<UnauthorizedAccessException>(() => vfs.OpenWrite("file", false, out _));
        Assert.ThrowsException<UnauthorizedAccessException>(() => vfs.CreateDirectory("folder"));
        Assert.ThrowsException<UnauthorizedAccessException>(() => vfs.Delete("file"));
        Assert.ThrowsException<UnauthorizedAccessException>(() => vfs.Rename("file", "renamed", false));
    }

    [TestMethod]
    public void Read_falls_through_to_source()
    {
        using var f = Fixture.Build();
        f.Source.AddFile("readme.txt", "from-source");

        using var s = f.Vfs.OpenRead("readme.txt", out var e);
        Assert.AreEqual("from-source", ReadAll(s));
    }

    [TestMethod]
    public void Upper_layer_shadows_lower()
    {
        using var f = Fixture.Build();
        f.Source.AddFile("x.txt", "source-value");
        using (var w = f.Vfs.OpenWrite("x.txt", true, out _))
        {
            var b = System.Text.Encoding.UTF8.GetBytes("overlay-value");
            w.Write(b, 0, b.Length);
        }

        using var s = f.Vfs.OpenRead("x.txt", out _);
        Assert.AreEqual("overlay-value", ReadAll(s));
    }

    [TestMethod]
    public void Directory_snapshot_preserves_members_during_updates()
    {
        using var f = Fixture.Build();
        f.Source.AddFile("a.txt", "a");
        f.Source.AddFile("shared.txt", "src");
        f.Vfs.CreateDirectory("b");
        using (f.Vfs.OpenWrite("shared.txt", true, out _)) { }

        var names = f.Vfs.List("").Select(e => e.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "a.txt", "b", "shared.txt" }, names); // sorted, deduped
    }

    [TestMethod]
    public void Tombstone_hides_lower_entry_from_list_and_lookup()
    {
        using var f = Fixture.Build();
        f.Source.AddFile("del.txt", "x");

        // copy-up then delete so the overlay carries a tombstone shadowing the source.
        using (f.Vfs.OpenWrite("del.txt", truncate: false, out _)) { }
        f.Vfs.Delete("del.txt");

        Assert.IsFalse(f.Vfs.TryLookup("del.txt", out _));
        CollectionAssert.DoesNotContain(f.Vfs.List("").Select(e => e.Name).ToArray(), "del.txt");
    }

    [TestMethod]
    public void OpenWrite_copies_up_from_lower_layer()
    {
        using var f = Fixture.Build();
        f.Source.AddFile("doc.txt", "original");

        // Writing a file that only exists below must copy it into the overlay first.
        using (var w = f.Vfs.OpenWrite("doc.txt", truncate: false, out _))
        {
            w.Seek(0, SeekOrigin.End);
            var b = System.Text.Encoding.UTF8.GetBytes("+more");
            w.Write(b, 0, b.Length);
        }

        Assert.IsTrue(f.Overlay.TryGetEntry("doc.txt", out _), "file should be copied up into overlay");
        using var s = f.Vfs.OpenRead("doc.txt", out _);
        Assert.AreEqual("original+more", ReadAll(s));
    }

    [TestMethod]
    public void Read_backfills_cache_and_serves_from_cache_next_time()
    {
        using var f = Fixture.Build();
        f.Source.AddFile("cached.txt", "payload");

        using (var s = f.Vfs.OpenRead("cached.txt", out _))
            Assert.AreEqual("payload", ReadAll(s));

        Assert.AreEqual(1, f.Source.OpenReadCount);
        Assert.IsTrue(f.Cache.TryGetEntry("cached.txt", out _), "entry should have been backfilled");

        // Second read must come from the cache, not the source.
        using (var s2 = f.Vfs.OpenRead("cached.txt", out _))
            Assert.AreEqual("payload", ReadAll(s2));
        Assert.AreEqual(1, f.Source.OpenReadCount, "source must not be hit again");
    }

    [TestMethod]
    public void Generation_bump_invalidates_cached_content()
    {
        using var f = Fixture.Build();
        f.Source.AddFile("v.txt", "old");
        using (var s = f.Vfs.OpenRead("v.txt", out _)) Assert.AreEqual("old", ReadAll(s));

        // Upstream content changes AND its generation advances.
        f.Source.AddFile("v.txt", "new");
        f.Source.BumpGeneration("g1");

        using var s2 = f.Vfs.OpenRead("v.txt", out _);
        Assert.AreEqual("new", ReadAll(s2));
    }

    [TestMethod]
    public void Rename_in_overlay_from_source_file()
    {
        using var f = Fixture.Build();
        f.Source.AddFile("src.txt", "hello");

        f.Vfs.Rename("src.txt", "dst.txt", replace: false);

        Assert.IsTrue(f.Vfs.TryLookup("dst.txt", out _));
        using var s = f.Vfs.OpenRead("dst.txt", out _);
        Assert.AreEqual("hello", ReadAll(s));
    }

    [TestMethod]
    public void OpenRead_missing_throws()
    {
        using var f = Fixture.Build();
        Assert.ThrowsException<VfsNotFoundException>(() => f.Vfs.OpenRead("nope.txt", out _));
    }
}
