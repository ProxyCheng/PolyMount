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
}
