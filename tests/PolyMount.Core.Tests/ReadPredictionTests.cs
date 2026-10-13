using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class ReadPredictionTests
{
    [TestMethod]
    public async Task Directory_access_sequence_predicts_complete_listing_into_lower_cache()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer(); source.AddFile("a/file", "a"); source.AddFile("b/child/file", "b");
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => source.Generation, 1048576);
        using var prediction = new PredictionCacheLayer(1048576, () => source.Generation,
            new ReadPredictionOptions { Enabled = true, IdleDelayMs = 100 }, temp.Sub("directory-model.json"));
        prediction.Attach(new VirtualFileSystem(new ILayer[] { cache, source }));
        var vfs = new VirtualFileSystem(new ILayer[] { prediction, cache, source });
        foreach (var path in new[] { "a", "b", "a", "b" }) vfs.List(path);
        cache.Purge();
        vfs.List("a");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!cache.TryReadObject("b", out _) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.IsTrue(cache.TryReadObject("b", out var directory));
        Assert.IsTrue(directory.Header.IsDir);
        Assert.AreEqual("child", directory.Children!.Single().Name);
        Assert.IsTrue(directory.Children!.Single().IsDir);
        Assert.IsFalse(prediction.TryGetEntry("b", out _));
        Assert.AreEqual("cache", vfs.Probe("b").Single(p => p.Effective).Name);
    }

    [TestMethod]
    public async Task Prediction_layer_fills_lower_cache_but_never_serves_frontend_hits()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer(); source.AddFile("a", "a"); source.AddFile("b", "payload");
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => source.Generation, 1048576);
        using var prediction = new PredictionCacheLayer(1048576, () => source.Generation,
            new ReadPredictionOptions { Enabled = true, IdleDelayMs = 100 }, temp.Sub("model.json"));
        prediction.Attach(new VirtualFileSystem(new ILayer[] { cache, source }));
        var vfs = new VirtualFileSystem(new ILayer[] { prediction, cache, source });
        foreach (var path in new[] { "a", "b", "a", "b" }) using (vfs.OpenRead(path, out _)) { }
        cache.Purge();
        using (vfs.OpenRead("a", out _)) { }
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!cache.TryGetEntry("b", out _) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.IsTrue(cache.TryGetEntry("b", out _));
        Assert.IsFalse(prediction.TryGetEntry("b", out _));
        Assert.IsTrue(prediction.MemoryBytes <= 1048576);
        int opens = source.OpenReadCount;
        using (var reader = new StreamReader(vfs.OpenRead("b", out _))) Assert.AreEqual("payload", reader.ReadToEnd());
        Assert.AreEqual(opens, source.OpenReadCount);
        source.BumpGeneration("new");
        Assert.IsFalse(cache.TryGetEntry("b", out _));
    }

    [TestMethod]
    public async Task Learned_next_file_is_preloaded_and_model_survives_restart()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer(); source.AddFile("a", "a"); source.AddFile("b", "b");
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => "1", 1048576);
        var vfs = new VirtualFileSystem(new ILayer[] { cache, source });
        var options = new ReadPredictionOptions { Enabled = true, IdleDelayMs = 100 };
        string model = temp.Sub("prediction.json");
        using (var prediction = new ReadPrediction(vfs, options, model))
        {
            vfs.Prediction = prediction;
            foreach (var path in new[] { "a", "b", "a", "b" }) using (vfs.OpenRead(path, out _)) { }
            await cache.WaitForPendingFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        cache.Purge();
        using var restored = new ReadPrediction(vfs, options, model); vfs.Prediction = restored;
        using (vfs.OpenRead("a", out _)) { }
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (restored.Completed == 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.AreEqual(1L, restored.Completed);
        Assert.IsTrue(cache.TryGetEntry("b", out _));
        int opens = source.OpenReadCount;
        using (vfs.OpenRead("b", out _)) { }
        Assert.AreEqual(opens, source.OpenReadCount);
        vfs.Prediction = null;
    }
}
