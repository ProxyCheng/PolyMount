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
}
