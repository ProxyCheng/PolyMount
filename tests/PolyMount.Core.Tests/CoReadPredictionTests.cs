using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class CoReadPredictionTests
{
    /// <summary>One process reading a trigger and its followers close together. The gap after
    /// it is what lets the next read close the burst; the model closes lazily.</summary>
    private static async Task Burst(CoReadPrediction model, long triggerStream, long followerStream,
        params string[] paths)
    {
        for (int i = 0; i < paths.Length; i++)
            using (model.Observe(paths[i], i == 0 ? triggerStream : followerStream, false)) { }
        await Task.Delay(400);
    }

    private static async Task Until(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !condition()) await Task.Delay(20);
        Assert.IsTrue(condition(), because);
    }

    [TestMethod]
    public async Task A_repeated_burst_teaches_the_set_and_the_next_trigger_preloads_it()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        foreach (var name in new[] { "m", "p1", "p2", "q" }) source.AddFile(name, name);
        var vfs = new VirtualFileSystem(new ILayer[] { source });
        var loaded = new ConcurrentBag<string>();
        using var model = new CoReadPrediction(vfs,
            new CoReadOptions { BurstGapMs = 300, MinCoReadCount = 1, MinConfidence = 0, TickIntervalMs = 10 },
            temp.Sub("co-read.json"),
            (path, stream, origin, group) =>
            {
                loaded.Add(path);
                using var read = vfs.OpenRead(path, out _);
                read.CopyTo(Stream.Null);
                return true;
            });

        long a = RequestDiagnostics.StreamId(77, 1);
        long b = RequestDiagnostics.StreamId(77, 2);
        // One process, two threads: a manifest and its payloads in one burst. The set has to
        // repeat before it is evidence, so the same burst is read twice.
        await Burst(model, a, b, "m", "p1", "p2");
        await Burst(model, a, b, "m", "p1", "p2");

        using (model.Observe("m", a, false)) { }    // the trigger path is read again
        await Until(() => model.Stats.BurstCompleted >= 2, "the confirmed set was preloaded");

        Assert.AreEqual(1, model.Stats.Groups, "the trigger's set repeated and is eligible");

        var paths = loaded.ToList();
        CollectionAssert.Contains(paths, "p1");
        CollectionAssert.Contains(paths, "p2");
        CollectionAssert.DoesNotContain(paths, "q");
        Assert.IsTrue(model.Stats.BurstPredictions >= 2);
    }

    [TestMethod]
    public async Task One_burst_is_not_evidence()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        foreach (var name in new[] { "m", "p1", "p2" }) source.AddFile(name, name);
        var vfs = new VirtualFileSystem(new ILayer[] { source });
        var loaded = new ConcurrentBag<string>();
        using var model = new CoReadPrediction(vfs,
            new CoReadOptions { BurstGapMs = 300, MinCoReadCount = 1, MinConfidence = 0, TickIntervalMs = 10 },
            temp.Sub("co-read.json"),
            (path, stream, origin, group) => { loaded.Add(path); return true; });

        long a = RequestDiagnostics.StreamId(78, 1);
        long b = RequestDiagnostics.StreamId(78, 2);
        await Burst(model, a, b, "m", "p1", "p2");
        using (model.Observe("m", a, false)) { }
        await Task.Delay(200);

        Assert.AreEqual(0, loaded.Count, "a set seen once is a sample, not a rule");
        Assert.AreEqual(0, model.Stats.Groups);
    }

    [TestMethod]
    public async Task A_trigger_whose_set_does_not_repeat_never_preloads()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        foreach (var name in new[] { "m", "p1", "p2", "p3", "p4" }) source.AddFile(name, name);
        var vfs = new VirtualFileSystem(new ILayer[] { source });
        var loaded = new ConcurrentBag<string>();
        using var model = new CoReadPrediction(vfs,
            new CoReadOptions { BurstGapMs = 300, MinCoReadCount = 1, MinConfidence = 0, TickIntervalMs = 10 },
            temp.Sub("co-read.json"),
            (path, stream, origin, group) => { loaded.Add(path); return true; });

        long a = RequestDiagnostics.StreamId(79, 1);
        long b = RequestDiagnostics.StreamId(79, 2);
        // Disjoint follower sets: the first path merely happened to be read first, it does not
        // bring a batch, and no number of such bursts adds up to confidence.
        await Burst(model, a, b, "m", "p1", "p2");
        await Burst(model, a, b, "m", "p3", "p4");
        await Burst(model, a, b, "m", "p1", "p2");
        using (model.Observe("m", a, false)) { }
        await Task.Delay(200);

        Assert.AreEqual(0, loaded.Count);
        Assert.AreEqual(0, model.Stats.Groups);
    }

    [TestMethod]
    public async Task A_group_that_keeps_going_unread_is_closed()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        foreach (var name in new[] { "m", "p1", "p2" }) source.AddFile(name, name);
        var vfs = new VirtualFileSystem(new ILayer[] { source });
        var loaded = new ConcurrentBag<string>();
        using var model = new CoReadPrediction(vfs,
            new CoReadOptions
            {
                BurstGapMs = 300, MinCoReadCount = 1, MinConfidence = 0, TickIntervalMs = 10,
                GroupWasteStreak = 2
            },
            temp.Sub("co-read.json"),
            (path, stream, origin, group) => { loaded.Add(path); return true; });

        long a = RequestDiagnostics.StreamId(80, 1);
        long b = RequestDiagnostics.StreamId(80, 2);
        await Burst(model, a, b, "m", "p1", "p2");
        await Burst(model, a, b, "m", "p1", "p2");
        using (model.Observe("m", a, false)) { }
        await Until(() => model.Stats.BurstCompleted >= 2, "the confirmed set was preloaded");

        // What the layer reports for two preloaded entries nobody read.
        model.ReportGroup("m", false);
        model.ReportGroup("m", false);
        Assert.AreEqual(1, model.Stats.ClosedGroups, "a run of unread guesses closes its group");
        Assert.AreEqual(0, model.Stats.Groups);

        loaded.Clear();
        await Burst(model, a, b, "m", "p1", "p2");
        using (model.Observe("m", a, false)) { }
        await Task.Delay(200);
        Assert.AreEqual(0, loaded.Count, "a closed group stops asking");
    }

    [TestMethod]
    public async Task A_batch_is_capped_by_files()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        foreach (var name in new[] { "m", "a1", "a2", "a3", "a4", "a5" }) source.AddFile(name, name);
        var vfs = new VirtualFileSystem(new ILayer[] { source });
        var loaded = new ConcurrentBag<string>();
        using var model = new CoReadPrediction(vfs,
            new CoReadOptions
            {
                BurstGapMs = 300, MinCoReadCount = 1, MinConfidence = 0, TickIntervalMs = 10,
                MaxBatchFiles = 2
            },
            temp.Sub("co-read.json"),
            (path, stream, origin, group) => { loaded.Add(path); return true; });

        long a = RequestDiagnostics.StreamId(81, 1);
        long b = RequestDiagnostics.StreamId(81, 2);
        await Burst(model, a, b, "m", "a1", "a2", "a3", "a4", "a5");
        await Burst(model, a, b, "m", "a1", "a2", "a3", "a4", "a5");
        using (model.Observe("m", a, false)) { }
        await Until(() => model.Stats.BurstPredictions >= 2, "the best two members were fetched");
        await Task.Delay(100);

        Assert.AreEqual(2, model.Stats.BurstPredictions, "an over-budget set yields its best members, not all of them");
        Assert.AreEqual(2, loaded.Count);
    }

    [TestMethod]
    public async Task The_layer_composes_successor_and_co_read_strategies()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        foreach (var name in new[] { "m", "p1", "p2", "s1", "s2" }) source.AddFile(name, name);
        var options = ReadPredictionProfiles.IdleOptions();
        // The successor needs one lucky transition to be confident, and it would then hold the
        // same members first; this test is about the co-read strategy reaching the shared layer,
        // not about two strategies racing for one file.
        options.MinConfidence = 0.99;
        var coRead = new CoReadOptions { BurstGapMs = 300, MinCoReadCount = 1, MinConfidence = 0, TickIntervalMs = 10 };
        using var layer = new PredictionCacheLayer(1 << 20, () => "v1", options, coRead,
            new[] { "successor", "co-read" }, temp.Sub("prediction.json"));
        layer.Attach(new VirtualFileSystem(new ILayer[] { source }));
        var vfs = new VirtualFileSystem(new ILayer[] { layer, source });

        for (int round = 0; round < 2; round++)
        {
            foreach (var path in new[] { "m", "p1", "p2" })
                using (var read = vfs.OpenRead(path, out _)) read.CopyTo(Stream.Null);
            await Task.Delay(400);
        }
        using (var read = vfs.OpenRead("m", out _)) read.CopyTo(Stream.Null);
        await Until(() => layer.BurstGroups == 1, "the co-read strategy learned its set in the shared layer");

        // By this point another strategy may already have fetched the set — the layer holds one
        // copy and a preload of something it already holds is not fetched again — so what this
        // test pins is that the co-read strategy learned through the same layer and that the
        // shared cache answers with the burst.
        Assert.IsTrue(layer.TryOpenRead("p1", out _, out var held), "the shared cache holds the burst");
        held.Dispose();
        Assert.IsTrue(layer.LearnedTransitions > 0, "the successor strategy still learns its table");
        Assert.IsTrue(layer.ActiveStreams > 0);
    }

    [TestMethod]
    public async Task A_member_the_layer_already_holds_is_not_fetched_again()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        foreach (var name in new[] { "m", "p1", "p2" }) source.AddFile(name, name);
        var coRead = new CoReadOptions { BurstGapMs = 300, MinCoReadCount = 1, MinConfidence = 0, TickIntervalMs = 10 };
        using var layer = new PredictionCacheLayer(1 << 20, () => "v1", ReadPredictionProfiles.IdleOptions(),
            coRead, new[] { "co-read" }, temp.Sub("prediction.json"));
        layer.Attach(new VirtualFileSystem(new ILayer[] { source }));
        var vfs = new VirtualFileSystem(new ILayer[] { layer, source });

        for (int round = 0; round < 2; round++)
        {
            foreach (var path in new[] { "m", "p1", "p2" })
                using (var read = vfs.OpenRead(path, out _)) read.CopyTo(Stream.Null);
            await Task.Delay(400);
        }
        using (var read = vfs.OpenRead("m", out _)) read.CopyTo(Stream.Null);
        await Until(() => layer.BurstCompleted >= 2, "the set was preloaded once");

        using (var read = vfs.OpenRead("m", out _)) read.CopyTo(Stream.Null);
        await Task.Delay(150);
        Assert.AreEqual(2, layer.BurstPreloads, "the same members were not fetched a second time");
        Assert.IsTrue(layer.TryOpenRead("p1", out _, out var held));
        held.Dispose();
    }

    [TestMethod]
    public async Task The_co_read_model_survives_a_restart()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        foreach (var name in new[] { "m", "p1", "p2" }) source.AddFile(name, name);
        var vfs = new VirtualFileSystem(new ILayer[] { source });
        string modelPath = temp.Sub("co-read.json");
        var options = new CoReadOptions { BurstGapMs = 300, MinCoReadCount = 1, MinConfidence = 0, TickIntervalMs = 10 };
        long a = RequestDiagnostics.StreamId(88, 1);
        long b = RequestDiagnostics.StreamId(88, 2);

        using (var first = new CoReadPrediction(vfs, options, modelPath))
        {
            await Burst(first, a, b, "m", "p1", "p2");
            await Burst(first, a, b, "m", "p1", "p2");
        }

        var loaded = new ConcurrentBag<string>();
        using var restored = new CoReadPrediction(vfs, options, modelPath,
            (path, stream, origin, group) => { loaded.Add(path); return true; });
        Assert.AreEqual(1, restored.Stats.Groups, "the confirmed set was persisted");
        using (restored.Observe("m", a, false)) { }
        await Until(() => restored.Stats.BurstCompleted >= 2, "the restored model preloaded the set");
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
        options.MinConfidence = 0.99;   // see the composition test: one transition is not evidence
        var coRead = new CoReadOptions { BurstGapMs = 300, MinCoReadCount = 1, MinConfidence = 0, TickIntervalMs = 10 };
        using var layer = new PredictionCacheLayer(1 << 20, () => "v1", options, coRead,
            new[] { "successor", "stride", "spatial", "co-read" }, temp.Sub("prediction.json"));
        layer.Attach(new VirtualFileSystem(new ILayer[] { source }));
        var vfs = new VirtualFileSystem(new ILayer[] { layer, source });
        for (int round = 0; round < 2; round++)
        {
            foreach (var path in new[] { "m", "p1", "p2" })
                using (var read = vfs.OpenRead(path, out _)) read.CopyTo(Stream.Null);
            await Task.Delay(400);
        }
        using (var read = vfs.OpenRead("m", out _)) read.CopyTo(Stream.Null);
        await Until(() => layer.BurstGroups == 1, "co-read participates in the same layer");
        Assert.IsTrue(layer.LearnedTransitions > 0, "the successor strategy still learns");
        Assert.IsTrue(layer.ActiveStreams > 0);
        Assert.IsTrue(layer.TryOpenRead("p1", out _, out var held), "the shared cache holds the set");
        held.Dispose();
    }
}
