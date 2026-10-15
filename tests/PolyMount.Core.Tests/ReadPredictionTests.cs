using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class ReadPredictionTests
{
    [TestMethod]
    public async Task A_divergent_successor_does_not_erase_the_established_one()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        foreach (var name in new[] { "a", "b", "c" }) source.AddFile(name, name);
        var predicted = new List<string>();
        using var prediction = new ReadPrediction(new VirtualFileSystem(new[] { source }),
            new ReadPredictionOptions(), temp.Sub("divergence.json"),
            path => { lock (predicted) predicted.Add(path); });

        // a -> b five times, then one divergence to c. Keeping a single slot per path would
        // let that one divergence discard the five observations behind b.
        for (int i = 0; i < 5; i++) { using (prediction.Demand("a")) { } using (prediction.Demand("b")) { } }
        using (prediction.Demand("a")) { }
        using (prediction.Demand("c")) { }
        Assert.AreEqual(3, prediction.LearnedTransitions, "both successors of a are kept");

        lock (predicted) predicted.Clear();
        using (prediction.Demand("a")) { }
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline) { lock (predicted) if (predicted.Count > 0) break; await Task.Delay(25); }
        lock (predicted) Assert.AreEqual("b", predicted.FirstOrDefault(), "the well-evidenced successor still leads");
    }

    [TestMethod]
    public async Task A_coin_flip_successor_is_not_read_ahead()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        foreach (var name in new[] { "a", "b", "c" }) source.AddFile(name, name);
        var predicted = new List<string>();
        using var prediction = new ReadPrediction(new VirtualFileSystem(new[] { source }),
            new ReadPredictionOptions(), temp.Sub("coinflip.json"),
            path => { lock (predicted) predicted.Add(path); });

        // Two successors seen once each: no leader worth spending source bandwidth on.
        using (prediction.Demand("a")) { } using (prediction.Demand("b")) { }
        using (prediction.Demand("a")) { } using (prediction.Demand("c")) { }
        lock (predicted) predicted.Clear();
        using (prediction.Demand("a")) { }
        await Task.Delay(600);
        lock (predicted) Assert.AreEqual(0, predicted.Count, "a tied successor is a coin flip, not a prediction");
    }

    [TestMethod]
    public async Task One_previous_sequence_predicts_during_a_single_active_demand()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer(); source.AddFile("a", "a"); source.AddFile("b", "b");
        var vfs = new VirtualFileSystem(new[] { source });
        using var loaded = new ManualResetEventSlim();
        using var prediction = new ReadPrediction(vfs, new ReadPredictionOptions { IdleDelayMs = 100 },
            temp.Sub("one-run.json"), path => { if (path == "b") loaded.Set(); });
        using (prediction.Demand("a")) { }
        using (prediction.Demand("b")) { }
        using (prediction.Demand("a"))
            Assert.IsTrue(await Task.Run(() => loaded.Wait(TimeSpan.FromSeconds(5))));
    }

    [TestMethod]
    public void Reset_erases_persisted_model_and_keeps_live_demand_accounting()
    {
        using var temp = new TempDir();
        string model = temp.Sub("reset.json");
        var vfs = new VirtualFileSystem(Array.Empty<ILayer>());
        var options = new ReadPredictionOptions { IdleDelayMs = 100 };
        using (var prediction = new ReadPrediction(vfs, options, model))
        {
            using (prediction.Demand("a")) { }
            using (prediction.Demand("b")) { }
            Assert.AreEqual(1, prediction.LearnedTransitions);
            using (prediction.Demand("a")) prediction.Reset();
            Assert.AreEqual(0, prediction.LearnedTransitions);
            Assert.AreEqual("[]", File.ReadAllText(model));
        }
        using var restored = new ReadPrediction(vfs, options, model);
        Assert.AreEqual(0, restored.LearnedTransitions);
        Assert.AreEqual(PolyMount.Core.Ipc.ControlCommands.ClearCache,
            LayerRegistry.Describe("prediction-cache").Actions.Single().Command);
    }

    [TestMethod]
    public async Task A_predicted_directory_lands_in_the_layer_that_guessed_it()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer(); source.AddFile("a/file", "a"); source.AddFile("b/child/file", "b");
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => source.Generation, 1048576);
        using var prediction = new PredictionCacheLayer(1048576, () => source.Generation,
            new ReadPredictionOptions { Enabled = true }, temp.Sub("directory-model.json"));
        prediction.Attach(new VirtualFileSystem(new ILayer[] { cache, source }));
        var vfs = new VirtualFileSystem(new ILayer[] { prediction, cache, source });
        foreach (var path in new[] { "a", "b", "a", "b" }) vfs.List(path);
        prediction.Clear();                                   // keep the learned order, drop the data

        vfs.List("a");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!prediction.TryReadObject("b", out _) && DateTime.UtcNow < deadline) await Task.Delay(50);

        Assert.IsTrue(prediction.TryReadObject("b", out var directory), "the guess should be held by the layer that made it");
        Assert.IsTrue(directory.Header.IsDir);
        Assert.AreEqual("child", directory.Children!.Single().Name);
        Assert.AreEqual("prediction-cache", vfs.Probe("b").Single(p => p.Effective).Name, "and it should serve the read");
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

    [TestMethod]
    public void Named_profiles_map_to_increasingly_eager_options()
    {
        var idle = ReadPredictionProfiles.Resolve(ReadPredictionProfiles.Idle, () => throw new Exception("custom unused"));
        var steady = ReadPredictionProfiles.Resolve(ReadPredictionProfiles.Steady, () => throw new Exception("custom unused"));
        var eager = ReadPredictionProfiles.Resolve(ReadPredictionProfiles.Eager, () => throw new Exception("custom unused"));
        Assert.IsTrue(idle.MaxActiveDemandForPreload >= 1);
        // Continuous by default: no quiet period is required before read-ahead may start.
        Assert.AreEqual(0, idle.IdleDelayMs);
        Assert.IsTrue(steady.MaxActiveDemandForPreload > idle.MaxActiveDemandForPreload);
        Assert.IsTrue(steady.TickIntervalMs < idle.TickIntervalMs);
        Assert.IsTrue(eager.MaxInFlight > steady.MaxInFlight);
        Assert.IsTrue(eager.MaxTransitions > steady.MaxTransitions);
        // An unrecognised value must fall back to the shipped behaviour, not to something eager.
        var unknown = ReadPredictionProfiles.Resolve("nonsense", () => throw new Exception("custom unused"));
        Assert.AreEqual(idle.MaxActiveDemandForPreload, unknown.MaxActiveDemandForPreload);
        Assert.AreEqual(idle.IdleDelayMs, unknown.IdleDelayMs);
    }

    [TestMethod]
    public void Custom_profile_uses_the_layer_parameters()
    {
        using var temp = new TempDir();
        var config = new LayerConfig { Id = "predict", Type = "prediction-cache", Parameters = new()
        {
            ["Profile"] = System.Text.Json.JsonSerializer.SerializeToElement(ReadPredictionProfiles.Custom),
            ["MaxActiveDemand"] = System.Text.Json.JsonSerializer.SerializeToElement(9),
            ["TickIntervalMs"] = System.Text.Json.JsonSerializer.SerializeToElement(30),
        } };
        var resolved = ReadPredictionProfiles.Resolve("custom", () => LayerRegistry.PredictionOptionsForTest(config));
        Assert.AreEqual(9, resolved.MaxActiveDemandForPreload);
        Assert.AreEqual(30, resolved.TickIntervalMs);
        Assert.AreEqual(ReadPredictionProfiles.IdleOptions().MaxInFlight, resolved.MaxInFlight);
    }

    [TestMethod]
    public void Kept_entries_that_are_read_are_hits_and_ones_evicted_unread_are_waste()
    {
        using var temp = new TempDir();
        // A one-child listing costs about 1 KB, so this holds two and the third evicts one.
        using var layer = new PredictionCacheLayer(2500, () => "v1", new ReadPredictionOptions(), temp.Sub("held.json"));
        var children = new[] { new VfsEntry { Name = "x" } };
        layer.StoreObject("kept", VirtualObject.Directory("kept", children));
        layer.StoreObject("unread", VirtualObject.Directory("unread", children));

        Assert.IsTrue(layer.TryReadObject("kept", out _), "a kept entry is served from this layer");
        Assert.AreEqual(1, layer.CacheHits);
        Assert.AreEqual(0, layer.Wasted);

        layer.StoreObject("third", VirtualObject.Directory("third", children));
        Assert.IsFalse(layer.TryReadObject("unread", out _), "the untouched entry is the one LRU takes");
        Assert.AreEqual(1, layer.Wasted, "evicting an entry nobody read is the guess not paying off");
        Assert.IsTrue(layer.TryReadObject("kept", out _), "the entry that was used survives");
    }

    [TestMethod]
    public void A_kept_file_is_served_from_memory()
    {
        using var temp = new TempDir();
        using var layer = new PredictionCacheLayer(65536, () => "v1", new ReadPredictionOptions(), temp.Sub("held.json"));
        using (var content = new MemoryStream(new byte[] { 7, 8, 9 }))
            layer.Store("data", "v1", new VfsEntry { Name = "data", IsDir = false, Size = 3 }, content);

        Assert.IsTrue(layer.TryOpenRead("data", out var entry, out var stream));
        using (stream)
        {
            Assert.AreEqual(3, entry.Size);
            Assert.AreEqual(7, stream.ReadByte());
            Assert.IsTrue(stream.CanSeek, "the mounted file system needs a seekable handle");
        }
        Assert.AreEqual(1, layer.CacheHits);
    }

    [TestMethod]
    public void An_entry_larger_than_the_whole_budget_is_refused()
    {
        using var temp = new TempDir();
        using var layer = new PredictionCacheLayer(4096, () => "v1", new ReadPredictionOptions(), temp.Sub("held.json"));
        layer.StoreObject("huge", VirtualObject.Directory("huge",
            Enumerable.Range(0, 500).Select(i => new VfsEntry { Name = new string('x', 200) + i }).ToArray()));
        Assert.IsFalse(layer.TryReadObject("huge", out _));
        Assert.AreEqual(0L, layer.MemoryBytes);
    }
}
