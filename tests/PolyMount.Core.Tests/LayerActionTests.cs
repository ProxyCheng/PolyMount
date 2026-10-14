using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Core.Ipc;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public class LayerActionTests
{
    [TestMethod]
    public void Reset_overlay_restores_parent_listing_and_source_data()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Sub("source"));
        File.WriteAllText(temp.Sub("source", "original"), "source");
        var profile = new MountProfile { StoreRoot = temp.Sub("store"), Layers = new()
        {
            new() { Id = "overlay", Type = "overlay" },
            new() { Id = "dir", Type = "dir", Source = new() { Path = temp.Sub("source") } }
        } };
        using var mount = new MountInstance(profile);
        mount.Vfs.Delete("original");
        using (var write = mount.Vfs.OpenWrite("new", true, out _)) write.Write("new"u8);
        mount.ClearLocalChanges("overlay", "");
        Assert.IsTrue(mount.Vfs.TryLookup("original", out _));
        Assert.IsFalse(mount.Vfs.TryLookup("new", out _));
        Assert.AreEqual("source", File.ReadAllText(temp.Sub("source", "original")));
    }

    [TestMethod]
    public void Evicting_one_cached_path_leaves_the_rest_of_the_cache()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Sub("source", "sub"));
        File.WriteAllText(temp.Sub("source", "a"), "a");
        var profile = new MountProfile { StoreRoot = temp.Sub("store"), Layers = new()
        {
            new() { Id = "cache", Type = "cache" },
            new() { Id = "dir", Type = "dir", Source = new() { Path = temp.Sub("source") } }
        } };
        using var mount = new MountInstance(profile);
        mount.Vfs.List("");
        mount.Vfs.List("sub");
        Assert.IsTrue(mount.Cache!.TryReadObject("", out _));
        Assert.IsTrue(mount.Cache.TryReadObject("sub", out _));

        mount.ApplyLayerAction("cache", "sub", LayerActions.EvictCache);

        Assert.IsFalse(mount.Cache.TryReadObject("sub", out _), "the evicted path should be gone");
        Assert.IsTrue(mount.Cache.TryReadObject("", out _),
            "a cached path is not visible state, so evicting one says nothing about the others");
    }
}
