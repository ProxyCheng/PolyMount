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
            (path, _, _) => { lock (predicted) predicted.Add(path); });

        // a -> b five times, then one divergence to c. Keeping a single slot per path would
        // let that one divergence discard the five observations behind b.
        for (int i = 0; i < 5; i++) { using (prediction.Demand("a")) { } using (prediction.Demand("b")) { } }
        using (prediction.Demand("a")) { }
        using (prediction.Demand("c")) { }
        // Every order is recorded now, so the count is not the point: what matters is that
        // the divergence did not displace the evidence behind b.
        Assert.IsTrue(prediction.LearnedTransitions >= 3, "both successors of a are kept");

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
            (path, _, _) => { lock (predicted) predicted.Add(path); });

        // Two successors seen once each: no leader worth spending source bandwidth on.
        using (prediction.Demand("a")) { } using (prediction.Demand("b")) { }
        using (prediction.Demand("a")) { } using (prediction.Demand("c")) { }
        await Task.Delay(800);                       // let the earlier nominations settle
        lock (predicted) predicted.Clear();
        using (prediction.Demand("a")) { }
        await Task.Delay(600);
        lock (predicted) Assert.IsFalse(predicted.Contains("b") || predicted.Contains("c"),
            "a tied successor is a coin flip, not a prediction");
    }

    [TestMethod]
    public async Task One_previous_sequence_predicts_during_a_single_active_demand()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer(); source.AddFile("a", "a"); source.AddFile("b", "b");
        var vfs = new VirtualFileSystem(new[] { source });
        using var loaded = new ManualResetEventSlim();
        using var prediction = new ReadPrediction(vfs, new ReadPredictionOptions { IdleDelayMs = 100 },
            temp.Sub("one-run.json"), (path, _, _) => { if (path == "b") loaded.Set(); });
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
        Assert.AreEqual(PolyMount.Plugins.Ipc.ControlCommands.ClearCache,
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
        var options = new ReadPredictionOptions { Enabled = true, IdleDelayMs = 100, PrefetchDepth = 1 };
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

    [TestMethod]
    public async Task A_longer_history_tells_apart_successors_a_single_path_conflates()
    {
        const string walk = "axpb xq axpb xq";
        async Task<int> PreloadsAfterWalk(int depth)
        {
            using var temp = new TempDir();
            var source = new FakeSourceLayer();
            foreach (var name in new[] { "a", "x", "p", "b", "q", "z" }) source.AddFile(name, name);
            var predicted = new List<string>();
            using var prediction = new ReadPrediction(new VirtualFileSystem(new[] { source }),
                new ReadPredictionOptions { ContextDepth = depth, PrefetchDepth = 1 }, temp.Sub("context.json"),
                (path, _, _) => { lock (predicted) predicted.Add(path); });
            // a -> x -> p and b -> x -> q: x alone is a coin flip, (a,x) and (b,x) are not.
            foreach (var step in walk.Split(' '))
                foreach (var name in step) using (prediction.Demand(name.ToString())) { }
            await Task.Delay(800);
            lock (predicted) predicted.Clear();
            using (prediction.Demand("a")) { }
            using (prediction.Demand("x")) { }
            await Task.Delay(600);
            lock (predicted) return predicted.Count(p => p == "p");
        }

        Assert.AreEqual(1, await PreloadsAfterWalk(2), "the longer history identifies the successor");
        Assert.AreEqual(0, await PreloadsAfterWalk(1), "the last path alone cannot tell them apart");
    }

    [TestMethod]
    public async Task Read_ahead_follows_the_path_while_each_step_is_confident()
    {
        async Task<List<string>> Walk(int depth)
        {
            using var temp = new TempDir();
            var source = new FakeSourceLayer();
            foreach (var name in new[] { "a", "b", "c" }) source.AddFile(name, name);
            var predicted = new List<string>();
            using var prediction = new ReadPrediction(new VirtualFileSystem(new[] { source }),
                new ReadPredictionOptions { ContextDepth = 1, PrefetchDepth = depth }, temp.Sub("chain.json"),
                (path, _, _) => { lock (predicted) predicted.Add(path); });
            foreach (var path in new[] { "a", "b", "c", "a", "b", "c" }) using (prediction.Demand(path)) { }
            await Task.Delay(800);
            lock (predicted) predicted.Clear();
            using (prediction.Demand("a")) { }
            await Task.Delay(600);
            lock (predicted) return predicted.Distinct().ToList();
        }

        var one = await Walk(1);
        CollectionAssert.Contains(one, "b");
        CollectionAssert.DoesNotContain(one, "c", "one step ahead stops at the first successor");
        var two = await Walk(2);
        CollectionAssert.Contains(two, "b");
        CollectionAssert.Contains(two, "c", "a second confident step follows the path");
    }

    [TestMethod]
    public async Task A_lowered_throttle_suspends_read_ahead()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        foreach (var name in new[] { "a", "b" }) source.AddFile(name, name);
        var predicted = new List<string>();
        const long stream = 1;
        using var prediction = new ReadPrediction(new VirtualFileSystem(new[] { source }),
            new ReadPredictionOptions { ContextDepth = 1 }, temp.Sub("throttle.json"),
            (path, _, _) => { lock (predicted) predicted.Add(path); });
        foreach (var path in new[] { "a", "b", "a", "b" }) using (prediction.Demand(path, stream)) { }
        await Task.Delay(800);

        prediction.Throttle = 0;
        lock (predicted) predicted.Clear();
        using (prediction.Demand("a", stream)) { }
        await Task.Delay(600);
        lock (predicted) Assert.AreEqual(0, predicted.Count, "a throttled predictor issues nothing");

        prediction.Throttle = 1;
        using (prediction.Demand("a", stream)) { }
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline) { lock (predicted) if (predicted.Count > 0) break; await Task.Delay(25); }
        lock (predicted) Assert.AreEqual("b", predicted.FirstOrDefault(), "and resumes when released");
    }

    [TestMethod]
    public async Task Reading_the_files_of_a_listed_directory_in_order_preloads_the_next_sibling()
    {
        using var temp = new TempDir();
        var source = SourceWith("dir", 6);
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => source.Generation, 1048576);
        var vfs = new VirtualFileSystem(new ILayer[] { cache, source });
        vfs.List("dir");                                    // the reader enumerates: the listing is held
        int listings = source.ListedDirectories.Count;
        // The origin is told apart here, so what the table predicts cannot be read as what the
        // stride predicted: both may name the same file, and only one of them is the stride.
        var strided = new List<string>();
        using var prediction = new ReadPrediction(vfs, new ReadPredictionOptions { PrefetchDepth = 3 },
            temp.Sub("siblings.json"), (path, _, origin) => { if (origin == PreloadOrigin.Sibling) lock (strided) strided.Add(path); });

        // Two files of one directory, in the order a listing hands them over.
        using (prediction.Demand("dir/f0")) { }
        using (prediction.Demand("dir/f1")) { }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (prediction.SiblingCompleted < 3 && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.AreEqual(1L, prediction.SiblingPredictions, "the walk nominates the next sibling and nothing else");
        Assert.AreEqual(3L, prediction.SiblingCompleted, "and the chain that already exists carries it on");
        lock (strided) CollectionAssert.AreEqual(new[] { "dir/f2", "dir/f3", "dir/f4" }, strided,
            "one position at a time, stopping at the configured depth");
        Assert.AreEqual(listings, source.ListedDirectories.Count, "naming a sibling costs no listing");
    }

    [TestMethod]
    public async Task A_sibling_already_read_is_not_nominated_again()
    {
        using var temp = new TempDir();
        var source = SourceWith("dir", 5);
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => source.Generation, 1048576);
        var vfs = new VirtualFileSystem(new ILayer[] { cache, source });
        vfs.List("dir");
        var strided = new List<string>();
        const long stream = 1;
        using var prediction = new ReadPrediction(vfs, new ReadPredictionOptions { PrefetchDepth = 1 },
            temp.Sub("covered.json"), (path, _, origin) => { if (origin == PreloadOrigin.Sibling) lock (strided) strided.Add(path); });

        using (prediction.Demand("dir/f0", stream)) { }
        using (prediction.Demand("dir/f1", stream)) { }
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (prediction.SiblingCompleted < 1 && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.AreEqual(1L, prediction.SiblingPredictions);
        lock (strided) Assert.AreEqual(1, strided.Count(p => p == "dir/f2"), "the next sibling is fetched once");

        // The reader catches up with the file that was fetched for it, which moves the walk on,
        // and then reads the same ascending pair again: the position behind it is one that has
        // been taken already, by the read-ahead or by the read itself.
        using (prediction.Demand("dir/f2", stream)) { }
        await Task.Delay(300);
        long issued = prediction.SiblingPredictions;
        lock (strided) strided.Clear();
        using (prediction.Demand("dir/f0", stream)) { }
        using (prediction.Demand("dir/f1", stream)) { }
        await Task.Delay(400);
        Assert.AreEqual(issued, prediction.SiblingPredictions, "a covered sibling is not nominated a second time");
        lock (strided) Assert.AreEqual(0, strided.Count(p => p == "dir/f2"));
    }

    [TestMethod]
    public async Task A_sibling_nomination_is_governed_by_the_same_throttle()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        foreach (var dir in new[] { "one", "two" })
            for (int i = 0; i < 4; i++) source.AddFile($"{dir}/f{i}", $"{dir}/f{i}");
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => source.Generation, 1048576);
        var vfs = new VirtualFileSystem(new ILayer[] { cache, source });
        foreach (var dir in new[] { "one", "two" }) vfs.List(dir);
        const long stream = 1;
        using var prediction = new ReadPrediction(vfs, new ReadPredictionOptions { PrefetchDepth = 1 },
            temp.Sub("stride-throttle.json"), (path, _, _) => { });

        using (prediction.Demand("one/f0", stream)) { }
        using (prediction.Demand("one/f1", stream)) { }
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (prediction.SiblingCompleted < 1 && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.AreEqual(1L, prediction.SiblingCompleted, "a stride runs while the feedback says guesses pay off");

        // The same dial the owning cache lowers for a bad successor guess, lowered for both.
        prediction.Throttle = 0;
        using (prediction.Demand("two/f0", stream)) { }
        using (prediction.Demand("two/f1", stream)) { }
        await Task.Delay(500);
        Assert.AreEqual(1L, prediction.SiblingPredictions, "a throttled predictor issues no stride either");

        prediction.Throttle = 1;
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (prediction.SiblingCompleted < 2 && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.AreEqual(2L, prediction.SiblingCompleted, "and releasing it resumes the walk that was waiting");
    }

    [TestMethod]
    public async Task Nothing_is_issued_while_the_in_flight_bound_is_reached()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        foreach (var dir in new[] { "one", "two" })
            for (int i = 0; i < 4; i++) source.AddFile($"{dir}/f{i}", $"{dir}/f{i}");
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => source.Generation, 1048576);
        var vfs = new VirtualFileSystem(new ILayer[] { cache, source });
        foreach (var dir in new[] { "one", "two" }) vfs.List(dir);
        using var gate = new ManualResetEventSlim();
        var started = new List<string>();
        const long stream = 1;
        using var prediction = new ReadPrediction(vfs, new ReadPredictionOptions { PrefetchDepth = 1, MaxInFlight = 1 },
            temp.Sub("stride-budget.json"), (path, _, _) => { lock (started) started.Add(path); gate.Wait(TimeSpan.FromSeconds(10)); });

        using (prediction.Demand("one/f0", stream)) { }
        using (prediction.Demand("one/f1", stream)) { }
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true) { lock (started) if (started.Count >= 1) break; if (DateTime.UtcNow > deadline) Assert.Fail("the stride never started"); await Task.Delay(25); }

        // A second walk nominates its own sibling while the first fetch is still out. One slot
        // is the whole budget, so the nomination waits instead of being issued.
        using (prediction.Demand("two/f0", stream)) { }
        using (prediction.Demand("two/f1", stream)) { }
        await Task.Delay(500);
        lock (started) Assert.AreEqual(1, started.Count, "the in-flight bound is the whole of the budget");

        gate.Set();
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (prediction.SiblingCompleted < 2 && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.AreEqual(2L, prediction.SiblingCompleted, "the nomination waits for a slot rather than being dropped");
    }

    [TestMethod]
    public async Task The_stride_counts_its_hits_and_waste_apart_from_the_tables()
    {
        using var temp = new TempDir();
        var source = SourceWith("dir", 5);
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => source.Generation, 1048576);
        using var prediction = new PredictionCacheLayer(1048576, () => source.Generation,
            new ReadPredictionOptions { PrefetchDepth = 1 }, temp.Sub("stride-model.json"));
        prediction.Attach(new VirtualFileSystem(new ILayer[] { cache, source }));
        var vfs = new VirtualFileSystem(new ILayer[] { prediction, cache, source });
        vfs.List("dir");

        foreach (var path in new[] { "dir/f0", "dir/f1" }) using (vfs.OpenRead(path, out _)) { }
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (prediction.SiblingCompleted < 1 && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.AreEqual(1L, prediction.SiblingPreloads, "the stride guessed, and the counters say so");
        Assert.AreEqual(0L, prediction.SiblingHits, "an entry nobody has read back is not a hit yet");
        Assert.IsTrue(prediction.TrackedDirectories >= 1, "the directory is still being walked");

        int opened = source.OpenReadCount;
        using (vfs.OpenRead("dir/f2", out _)) { }
        Assert.AreEqual(opened, source.OpenReadCount, "the reader is served by the stride, not by the source");
        Assert.AreEqual(1L, prediction.SiblingHits, "reading a stride's entry is a hit of its own");
        Assert.AreEqual(0L, prediction.SiblingWasted);
        Assert.IsTrue(prediction.SiblingHits <= prediction.Used, "the stride's counts are subsets of the layer's");
        Assert.IsTrue(prediction.SiblingPreloads <= prediction.PreloadsStarted);
    }

    [TestMethod]
    public async Task A_walk_with_no_held_listing_is_never_listed_for()
    {
        using var temp = new TempDir();
        var source = SourceWith("dir", 5);
        var vfs = new VirtualFileSystem(new ILayer[] { source });
        using var prediction = new ReadPrediction(vfs, new ReadPredictionOptions { PrefetchDepth = 3 },
            temp.Sub("no-listing.json"), (path, _, _) => { });

        foreach (var path in new[] { "dir/f0", "dir/f1", "dir/f2" }) using (prediction.Demand(path)) { }
        await Task.Delay(400);
        Assert.AreEqual(0L, prediction.SiblingPredictions, "with no held listing there is no position to continue from");
        Assert.AreEqual(0, prediction.TrackedDirectories);
        Assert.AreEqual(0, source.ListedDirectories.Count, "and none is enumerated to find one");
    }

    [TestMethod]
    public async Task Two_interleaved_streams_learn_their_own_successors()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        foreach (var name in new[] { "a0", "a1", "a2", "a3", "b0", "b1", "b2", "b3" })
            source.AddFile(name, name);
        var predicted = new List<(string Path, long Stream)>();
        using var prediction = new ReadPrediction(new VirtualFileSystem(new[] { source }),
            new ReadPredictionOptions { ContextDepth = 1, PrefetchDepth = 1 }, temp.Sub("streams.json"),
            (path, stream, _) => { lock (predicted) predicted.Add((path, stream)); });

        // Two callers walk their own order, interleaved. A single global "previous" would
        // record a0->b0, b0->a1, ... and neither caller would ever be predicted.
        const long streamA = 1, streamB = 2;
        var a = new[] { "a0", "a1", "a2", "a3" };
        var b = new[] { "b0", "b1", "b2", "b3" };
        for (int cycle = 0; cycle < 2; cycle++)
            for (int i = 0; i < 4; i++)
            {
                using (prediction.Demand(a[i], streamA)) { }
                using (prediction.Demand(b[i], streamB)) { }
            }
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (predicted) if (predicted.Any(p => p.Path == "a1") && predicted.Any(p => p.Path == "b1")) break;
            await Task.Delay(25);
        }

        lock (predicted)
        {
            CollectionAssert.Contains(predicted.Select(p => p.Path).ToList(), "a1");
            CollectionAssert.Contains(predicted.Select(p => p.Path).ToList(), "b1");
            Assert.IsTrue(predicted.Where(p => p.Path.StartsWith("a")).All(p => p.Stream == streamA));
            Assert.IsTrue(predicted.Where(p => p.Path.StartsWith("b")).All(p => p.Stream == streamB));
        }
        Assert.IsTrue(prediction.ActiveStreams >= 2, "the two callers have separate histories");
    }

    [TestMethod]
    public void A_thread_change_inside_one_process_reuses_the_stream_it_was_reading()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        foreach (var name in new[] { "a", "b" }) source.AddFile(name, name);
        using var prediction = new ReadPrediction(new VirtualFileSystem(new[] { source }),
            new ReadPredictionOptions { ContextDepth = 1 }, temp.Sub("alias.json"));

        long first = RequestDiagnostics.StreamId(4242, 1);
        long second = RequestDiagnostics.StreamId(4242, 2);
        using (prediction.Demand("a", first)) { }
        using (prediction.Demand("b", first)) { }
        using (prediction.Demand("a", first)) { }
        // The same process, a different pool thread. The stream whose next prediction is b is
        // the one this caller was using, so the new thread id is aliased to it rather than
        // starting a cold history.
        using (prediction.Demand("b", second)) { }
        Assert.AreEqual(1, prediction.ActiveStreams);
    }

    [TestMethod]
    public async Task Spatial_prefetch_expands_a_listed_directory_but_only_the_matching_prefix()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        foreach (var name in new[]
                 {
                     "d/world@a.ilevel", "d/world@b.ilevel", "d/world@c.ilevel",
                     "d/other@a.ilevel", "d/other@b.ilevel", "d/other@c.ilevel"
                 })
            source.AddFile(name, name);
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => source.Generation, 1048576);
        var vfs = new VirtualFileSystem(new ILayer[] { cache, source });
        vfs.List("d");                                         // the engine enumerates the directory
        var spatial = new List<string>();
        using var prediction = new ReadPrediction(vfs, new ReadPredictionOptions
        {
            SpatialEnabled = true, SpatialTriggerFiles = 2, SpatialMaxFiles = 8,
            SpatialSmallDirectory = 0, SpatialMinPrefix = 3, PrefetchDepth = 1
        }, temp.Sub("spatial.json"), (path, _, origin) =>
        {
            if (origin == PreloadOrigin.Spatial) lock (spatial) spatial.Add(path);
        });

        using (prediction.Demand("d", 1, listing: true)) { }
        using (prediction.Demand("d/world@a.ilevel", 1)) { }
        using (prediction.Demand("d/world@b.ilevel", 1)) { }
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline) { lock (spatial) if (spatial.Count > 0) break; await Task.Delay(25); }

        lock (spatial)
        {
            CollectionAssert.Contains(spatial, "d/world@c.ilevel");
            CollectionAssert.DoesNotContain(spatial, "d/other@a.ilevel");
        }
        Assert.IsTrue(prediction.SpatialPredictions >= 1);
    }

    [TestMethod]
    public async Task Spatial_prefetch_does_not_run_without_a_directory_enumeration()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        foreach (var name in new[] { "d/x1", "d/x2", "d/x3", "d/x4" }) source.AddFile(name, name);
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => source.Generation, 1048576);
        var vfs = new VirtualFileSystem(new ILayer[] { cache, source });
        vfs.List("d");                                         // held, but the caller never listed it
        using var prediction = new ReadPrediction(vfs, new ReadPredictionOptions
        {
            SpatialEnabled = true, SpatialTriggerFiles = 2, SpatialSmallDirectory = 16, PrefetchDepth = 1
        }, temp.Sub("no-listing.json"));

        using (prediction.Demand("d/x1", 1)) { }
        using (prediction.Demand("d/x2", 1)) { }
        await Task.Delay(400);
        Assert.AreEqual(0L, prediction.SpatialPredictions,
            "a directory reached by direct probes is not a set the caller asked to consume");
    }

    [TestMethod]
    public async Task Spatial_prefetch_refuses_a_large_directory_whose_files_share_no_prefix()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        foreach (var name in new[] { "d/a1", "d/a2", "d/b1", "d/b2", "d/c1", "d/c2" }) source.AddFile(name, name);
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => source.Generation, 1048576);
        var vfs = new VirtualFileSystem(new ILayer[] { cache, source });
        vfs.List("d");
        using var prediction = new ReadPrediction(vfs, new ReadPredictionOptions
        {
            SpatialEnabled = true, SpatialTriggerFiles = 2, SpatialMaxFiles = 8,
            SpatialSmallDirectory = 0, SpatialMinPrefix = 3, PrefetchDepth = 1
        }, temp.Sub("no-prefix.json"));

        using (prediction.Demand("d", 1, listing: true)) { }
        using (prediction.Demand("d/a1", 1)) { }
        using (prediction.Demand("d/b1", 1)) { }
        await Task.Delay(400);
        Assert.AreEqual(0L, prediction.SpatialPredictions);
    }

    [TestMethod]
    public async Task A_small_listed_directory_is_expanded_even_without_a_common_prefix()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        foreach (var name in new[] { "d/a", "d/b", "d/c" }) source.AddFile(name, name);
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => source.Generation, 1048576);
        var vfs = new VirtualFileSystem(new ILayer[] { cache, source });
        vfs.List("d");
        var spatial = new List<string>();
        using var prediction = new ReadPrediction(vfs, new ReadPredictionOptions
        {
            SpatialEnabled = true, SpatialTriggerFiles = 2, SpatialMaxFiles = 8,
            SpatialSmallDirectory = 16, PrefetchDepth = 1
        }, temp.Sub("small-dir.json"), (path, _, origin) =>
        {
            if (origin == PreloadOrigin.Spatial) lock (spatial) spatial.Add(path);
        });

        using (prediction.Demand("d", 1, listing: true)) { }
        using (prediction.Demand("d/a", 1)) { }
        using (prediction.Demand("d/b", 1)) { }
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline) { lock (spatial) if (spatial.Count > 0) break; await Task.Delay(25); }
        lock (spatial) CollectionAssert.Contains(spatial, "d/c");
    }

    /// <summary>An in-memory source holding <paramref name="count"/> single-letter files under
    /// one directory, named so that ordinal order is f0, f1, ... — the order a listing is
    /// enumerated in.</summary>
    private static FakeSourceLayer SourceWith(string dir, int count)
    {
        var source = new FakeSourceLayer();
        for (int i = 0; i < count; i++) source.AddFile($"{dir}/f{i}", $"{dir}/f{i}");
        return source;
    }
}
