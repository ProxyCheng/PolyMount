using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class CoReadPredictionTests
{
    [TestMethod]
    public async Task A_burst_teaches_the_set_and_the_next_trigger_preloads_it()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        foreach (var name in new[] { "m", "p1", "p2", "q" }) source.AddFile(name, name);
        var vfs = new VirtualFileSystem(new ILayer[] { source });
        var loaded = new ConcurrentBag<string>();
        using var model = new CoReadPrediction(vfs,
            new CoReadOptions { BurstGapMs = 10, MinCoReadCount = 1, MinConfidence = 0, TickIntervalMs = 10 },
            temp.Sub("co-read.json"),
            (path, stream, origin) =>
            {
                loaded.Add(path);
                using var read = vfs.OpenRead(path, out _);
                read.CopyTo(Stream.Null);
            });

        long a = RequestDiagnostics.StreamId(77, 1);
        long b = RequestDiagnostics.StreamId(77, 2);
        // One process, two threads: a manifest and its payloads in one burst.
        using (model.Observe("m", a, false)) { }
        using (model.Observe("p1", b, false)) { }
        using (model.Observe("p2", a, false)) { }
        await Task.Delay(40);                       // longer than BurstGapMs: the burst closes
        using (model.Observe("m", a, false)) { }    // the trigger path is read again
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && model.Stats.BurstCompleted < 2) await Task.Delay(20);

        var paths = loaded.ToList();
        CollectionAssert.Contains(paths, "p1");
        CollectionAssert.Contains(paths, "p2");
        CollectionAssert.DoesNotContain(paths, "q");
        Assert.IsTrue(model.Stats.BurstPredictions >= 2);
    }

    [TestMethod]
    public async Task The_layer_composes_successor_and_co_read_strategies()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        foreach (var name in new[] { "m", "p1", "p2", "s1", "s2" }) source.AddFile(name, name);
        var options = ReadPredictionProfiles.IdleOptions();
        var coRead = new CoReadOptions { BurstGapMs = 10, MinCoReadCount = 1, MinConfidence = 0, TickIntervalMs = 10 };
        using var layer = new PredictionCacheLayer(1 << 20, () => "v1", options, coRead,
            new[] { "successor", "co-read" }, temp.Sub("prediction.json"));
        layer.Attach(new VirtualFileSystem(new ILayer[] { source }));
        var vfs = new VirtualFileSystem(new ILayer[] { layer, source });

        foreach (var path in new[] { "m", "p1", "p2" })
            using (var read = vfs.OpenRead(path, out _)) read.CopyTo(Stream.Null);
        await Task.Delay(40);
        using (var read = vfs.OpenRead("m", out _)) read.CopyTo(Stream.Null);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && layer.BurstCompleted < 2) await Task.Delay(20);

        Assert.IsTrue(layer.BurstPreloads >= 2, "the co-read strategy asked the shared layer to preload");
        Assert.IsTrue(layer.TryOpenRead("p1", out _, out var held), "the shared cache holds the burst");
        held.Dispose();
        Assert.IsTrue(layer.LearnedTransitions > 0, "the successor strategy still learns its table");
        Assert.IsTrue(layer.ActiveStreams > 0);
    }

    [TestMethod]
    public async Task The_co_read_model_survives_a_restart()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        foreach (var name in new[] { "m", "p1", "p2" }) source.AddFile(name, name);
        var vfs = new VirtualFileSystem(new ILayer[] { source });
        string modelPath = temp.Sub("co-read.json");
        var options = new CoReadOptions { BurstGapMs = 10, MinCoReadCount = 1, MinConfidence = 0, TickIntervalMs = 10 };
        long a = RequestDiagnostics.StreamId(88, 1);

        using (var first = new CoReadPrediction(vfs, options, modelPath))
        {
            using (first.Observe("m", a, false)) { }
            using (first.Observe("p1", a, false)) { }
            using (first.Observe("p2", a, false)) { }
            await Task.Delay(40);
            using (first.Observe("m", a, false)) { }   // closes and persists the links
            await Task.Delay(60);
        }

        var loaded = new ConcurrentBag<string>();
        using var restored = new CoReadPrediction(vfs, options, modelPath,
            (path, stream, origin) => loaded.Add(path));
        using (restored.Observe("m", a, false)) { }
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && restored.Stats.BurstCompleted < 2) await Task.Delay(20);
        CollectionAssert.Contains(loaded.ToList(), "p1");
        CollectionAssert.Contains(loaded.ToList(), "p2");
    }

    [TestMethod]
    public async Task All_four_strategies_share_one_layer()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        foreach (var name in new[] { "m", "p1", "p2" }) source.AddFile(name, name);
        var options = ReadPredictionProfiles.EagerOptions();
        options.SpatialEnabled = true;
        var coRead = new CoReadOptions { BurstGapMs = 10, MinCoReadCount = 1, MinConfidence = 0, TickIntervalMs = 10 };
        using var layer = new PredictionCacheLayer(1 << 20, () => "v1", options, coRead,
            new[] { "successor", "stride", "spatial", "co-read" }, temp.Sub("prediction.json"));
        layer.Attach(new VirtualFileSystem(new ILayer[] { source }));
        var vfs = new VirtualFileSystem(new ILayer[] { layer, source });
        foreach (var path in new[] { "m", "p1", "p2" })
            using (var read = vfs.OpenRead(path, out _)) read.CopyTo(Stream.Null);
        await Task.Delay(40);
        using (var read = vfs.OpenRead("m", out _)) read.CopyTo(Stream.Null);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && layer.BurstCompleted < 2) await Task.Delay(20);
        Assert.IsTrue(layer.BurstPreloads >= 2, "co-read participates in the same layer");
        Assert.IsTrue(layer.LearnedTransitions > 0, "the successor strategy still learns");
        Assert.IsTrue(layer.ActiveStreams > 0);
    }

}
