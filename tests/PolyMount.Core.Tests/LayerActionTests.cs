using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount;
using PolyMount.Core;
using PolyMount.Core.Ipc;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public class LayerActionTests
{
    [TestMethod]
    public void Overlay_tombstone_roundtrip()
    {
        using var t = new TempDir();
        var o = new OverlayLayer(t.Sub("overlay"), t.Sub("state", "tombstones"));

        Assert.IsFalse(o.HasTombstone("f"));
        o.MarkTombstone("f");
        Assert.IsTrue(o.HasTombstone("f"));
        o.ClearTombstone("f");
        Assert.IsFalse(o.HasTombstone("f"));
    }

    [TestMethod]
    public void Cache_invalidate_entry_removes_single_path()
    {
        using var t = new TempDir();
        var cache = new LruCacheLayer(t.Sub("cache"), () => "v1", 1024 * 1024);
        using (var c = new MemoryStream(new byte[] { 1, 2, 3 }))
            cache.Store("a", "v1", new VfsEntry { Name = "a", Size = 3 }, c);
        using (var c = new MemoryStream(new byte[] { 4, 5 }))
            cache.Store("b", "v1", new VfsEntry { Name = "b", Size = 2 }, c);

        cache.InvalidateEntry("a");

        Assert.IsFalse(cache.TryGetEntry("a", out _));
        Assert.IsTrue(cache.TryGetEntry("b", out _));
    }

    [TestMethod]
    public void Probe_reports_direct_tombstone()
    {
        using var t = new TempDir();
        var overlay = new OverlayLayer(t.Sub("overlay"), t.Sub("state", "tombstones"));
        var source = new FakeSourceLayer();
        source.AddFile("x", "data");
        var vfs = new VirtualFileSystem(new ILayer[] { overlay, source });

        overlay.MarkTombstone("x");

        var probe = vfs.Probe("x");
        var ov = probe.First(p => p.Name == "overlay");
        Assert.IsTrue(ov.DirectTombstone);
        Assert.AreEqual("deleted", ov.State);
    }

    [TestMethod]
    public void MountInstance_hide_then_restore_against_dir_source()
    {
        using var t = new TempDir();
        var srcDir = t.Sub("src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "x.txt"), "from-dir");

        var profile = new MountProfile
        {
            Name = "t",
            MountPoint = t.Sub("mnt"),
            StoreRoot = t.Sub("store"),
            Source = new SourceConfig { Type = "dir", Path = srcDir }
        };

        using var mi = new MountInstance(profile);

        // Initially the dir source provides the file.
        var p0 = mi.Probe("x.txt");
        Assert.IsTrue(p0.Found);
        Assert.IsTrue(p0.Layers.First(l => l.Name == "dir").Effective);

        // Hide it via the overlay (tombstone).
        var p1 = mi.ApplyLayerAction("overlay", "x.txt", LayerActions.Hide);
        Assert.IsFalse(p1.Found);
        Assert.IsTrue(p1.Layers.First(l => l.Name == "overlay").Tombstoned);
        Assert.IsTrue(p1.Layers.First(l => l.Name == "dir").Shadowed);

        // Restore (remove tombstone) -> the dir source is visible again.
        var p2 = mi.ApplyLayerAction("overlay", "x.txt", LayerActions.Restore);
        Assert.IsTrue(p2.Found);
        Assert.IsTrue(p2.Layers.First(l => l.Name == "dir").Effective);
    }

    [TestMethod]
    public void Dir_source_mount_has_no_cache_layer()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.Sub("src"));
        var profile = new MountProfile
        {
            Name = "t",
            MountPoint = t.Sub("mnt"),
            StoreRoot = t.Sub("store"),
            Source = new SourceConfig { Type = "dir", Path = t.Sub("src") }
        };

        using var mi = new MountInstance(profile);

        Assert.IsNull(mi.Cache, "local dir source should skip the cache layer");
        CollectionAssert.AreEqual(
            new[] { "overlay", "dir" },
            mi.Vfs.Layers.Select(l => l.Name).ToArray());
    }
}
