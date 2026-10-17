using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class PredictionWriteTests
{
    [TestMethod]
    public void A_write_passes_through_predict_and_a_read_only_cache_to_the_overlay()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        source.AddFile("a", "old");
        var overlay = new OverlayLayer(temp.Sub("overlay"));
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => source.Generation, 1048576, writable: false);
        using var prediction = new PredictionCacheLayer(1048576, () => source.Generation,
            new ReadPredictionOptions(), temp.Sub("model.json"));
        prediction.Attach(new VirtualFileSystem(new ILayer[] { cache, overlay, source }));
        var vfs = new VirtualFileSystem(new ILayer[] { prediction, cache, overlay, source });

        // A guess that was correct until the write.
        using (var stale = new MemoryStream("stale"u8.ToArray()))
            prediction.Store("a", "v1", new VfsEntry { Name = "a", IsDir = false, Size = 5 }, stale);
        Assert.IsTrue(prediction.TryOpenRead("a", out _, out var served));
        served.Dispose();

        using (var write = vfs.OpenWrite("a", true, out _)) write.Write("new"u8);

        Assert.IsTrue(prediction.Invalidated > 0, "the successful write invalidates the stale guess");
        Assert.IsFalse(prediction.TryOpenRead("a", out _, out _), "the prediction layer no longer serves it");
        Assert.IsTrue(overlay.TryGetEntry("a", out _), "the write landed in the overlay");
        Assert.IsFalse(cache.HasDurableWrites, "the read-only cache never accepts the write");
        using (var read = vfs.OpenRead("a", out _))
        using (var text = new StreamReader(read))
            Assert.AreEqual("new", text.ReadToEnd());
    }

    [TestMethod]
    public void A_write_invalidates_the_held_parent_listing_too()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        source.AddFile("dir/file", "x");
        var overlay = new OverlayLayer(temp.Sub("overlay"));
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => source.Generation, 1048576, writable: false);
        using var prediction = new PredictionCacheLayer(1048576, () => source.Generation,
            new ReadPredictionOptions(), temp.Sub("model.json"));
        prediction.Attach(new VirtualFileSystem(new ILayer[] { cache, overlay, source }));
        var vfs = new VirtualFileSystem(new ILayer[] { prediction, cache, overlay, source });

        prediction.StoreObject("dir", VirtualObject.Directory("dir", new[]
            { new VfsEntry { Name = "file", IsDir = false, Size = 1 } }));
        Assert.IsTrue(prediction.TryReadObject("dir", out _));

        vfs.CreateDirectory("dir/new");

        Assert.IsFalse(prediction.TryReadObject("dir", out _),
            "the parent listing a higher layer held is stale after the write");
        Assert.IsTrue(prediction.Invalidated > 0);
    }
}
