using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Core.Ipc;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public class ProbeTests
{
    private TempDir _t = null!;
    private OverlayLayer _overlay = null!;
    private LruCacheLayer _cache = null!;
    private FakeSourceLayer _source = null!;
    private VirtualFileSystem _vfs = null!;

    [TestInitialize]
    public void Setup()
    {
        _t = new TempDir();
        _overlay = new OverlayLayer(_t.Sub("overlay"), _t.Sub("state", "tombstones"));
        _source = new FakeSourceLayer();
        _cache = new LruCacheLayer(_t.Sub("cache"), () => _source.Generation, 1024 * 1024);
        _vfs = new VirtualFileSystem(new ILayer[] { _overlay, _cache, _source });
    }

    [TestCleanup]
    public void Cleanup() => _t.Dispose();

    private LayerProbe Layer(IEnumerable<LayerProbe> probes, string name) =>
        probes.First(p => p.Name == name);

    [TestMethod]
    public void Source_only_file_is_effective_at_source()
    {
        _source.AddFile("a.txt", "hello");

        var p = _vfs.Probe("a.txt");

        Assert.IsTrue(Layer(p, "fake").Effective);
        Assert.AreEqual(5, Layer(p, "fake").Size);
        Assert.IsFalse(Layer(p, "overlay").Exists);
        Assert.IsFalse(Layer(p, "cache").Exists);
        Assert.IsFalse(Layer(p, "fake").Shadowed);
    }

    [TestMethod]
    public void Overlay_shadows_source()
    {
        _source.AddFile("a.txt", "src");
        using (_overlay.Create("a.txt", out _)) { }

        var p = _vfs.Probe("a.txt");

        Assert.IsTrue(Layer(p, "overlay").Effective);
        Assert.IsTrue(Layer(p, "fake").Exists);
        Assert.IsTrue(Layer(p, "fake").Shadowed, "source copy is hidden by overlay");
        Assert.IsFalse(Layer(p, "fake").Effective);
    }

    [TestMethod]
    public void Tombstone_hides_source_and_nothing_is_effective()
    {
        _source.AddFile("a.txt", "src");
        using (_overlay.Create("a.txt", out _)) { }
        _overlay.Delete("a.txt", shadowedBelow: true);

        var p = _vfs.Probe("a.txt");

        Assert.IsFalse(_vfs.Probe("a.txt").Any(l => l.Effective), "tombstone hides everything");
        Assert.IsTrue(Layer(p, "overlay").Tombstoned);
        Assert.IsTrue(Layer(p, "fake").Exists);
        Assert.IsTrue(Layer(p, "fake").Shadowed);
    }

    [TestMethod]
    public void Cache_backfill_becomes_effective_over_source()
    {
        _source.AddFile("a.txt", "payload");
        using (_vfs.OpenRead("a.txt", out _)) { } // triggers backfill into cache

        var p = _vfs.Probe("a.txt");

        Assert.IsTrue(Layer(p, "cache").Exists);
        Assert.IsTrue(Layer(p, "cache").Effective);
        Assert.IsTrue(Layer(p, "fake").Shadowed);
    }
}
