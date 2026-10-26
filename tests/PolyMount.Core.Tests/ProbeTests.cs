using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public class ProbeTests
{
    [TestMethod]
    public void Removed_directory_member_leaves_lower_object_shadowed_not_effective()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer(); source.AddFile("file", "source");
        var overlay = new OverlayLayer(temp.Sub("overlay"));
        var vfs = new VirtualFileSystem(new ILayer[] { overlay, source });
        vfs.Delete("file");
        var probe = vfs.Probe("file");
        Assert.IsFalse(probe.Any(p => p.Effective));
        Assert.IsTrue(probe.Single(p => p.Name == "fake").Shadowed);
        Assert.IsFalse(probe.Single(p => p.Name == "overlay").Exists);
    }

    [TestMethod]
    public void Probe_names_the_glyph_of_the_layer_that_won()
    {
        // The Explorer badge is chosen by this field, so it has to come from the winning layer's
        // own descriptor rather than from its colour.
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Sub("source"));
        File.WriteAllText(temp.Sub("source", "a.txt"), "a");
        var profile = new MountProfile
        {
            StoreRoot = temp.Sub("store"),
            Layers = new() { new() { Id = "dir", Type = "dir", Source = new() { Path = temp.Sub("source") } } }
        };
        using var mount = new MountInstance(profile);

        var probe = mount.Probe("a.txt");

        Assert.IsTrue(probe.Found);
        Assert.AreEqual("dir", probe.EffectiveLayer);
        Assert.AreEqual("folder", probe.EffectiveIcon);
        Assert.AreEqual(OverlayGlyphs.SlotOf("folder"), OverlayGlyphs.SlotOf(probe.EffectiveIcon));
    }
}
