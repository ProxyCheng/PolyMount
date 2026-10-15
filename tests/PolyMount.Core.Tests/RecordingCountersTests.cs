using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;

namespace PolyMount.Core.Tests;

/// <summary>The counters a recording is: what it can and cannot lose, and what it says about the
/// paths past the ones it ranks.</summary>
[TestClass]
public sealed class RecordingCountersTests
{
    [TestMethod]
    public void Operations_are_ranked_by_the_time_they_took()
    {
        var counters = new RecordingCounters();
        counters.Count("VFS.Lookup", "a", 1);
        counters.Count("VFS.List", "b", 40);
        counters.Count("VFS.List", "b", 160);
        var counts = counters.Snapshot();
        Assert.AreEqual(3, counts.Events);
        Assert.AreEqual("VFS.List", counts.Operations[0].Operation, "the operation list is not ranked by total time");
        Assert.AreEqual(200, counts.Operations[0].TotalMs, 0.001);
        Assert.AreEqual(160, counts.Operations[0].MaxMs, 0.001);
        Assert.AreEqual(1, counts.Operations[0].Over100, "a request past 100 ms was not counted as one");
        Assert.AreEqual(201, counts.TotalMs, 0.001);
    }

    /// <summary>The counts are what has to be right, and every request thread writes them.</summary>
    [TestMethod]
    public void Counts_are_exact_under_concurrent_writers()
    {
        var counters = new RecordingCounters();
        const int threads = 8, each = 2000;
        var writers = new List<Thread>();
        using var barrier = new Barrier(threads);
        for (int i = 0; i < threads; i++)
        {
            var writer = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (int n = 0; n < each; n++) counters.Count("VFS.Lookup", n % 16 == 0 ? "hot" : "cold", 1 + n % 7);
            });
            writer.Start();
            writers.Add(writer);
        }
        foreach (var writer in writers) writer.Join();
        var counts = counters.Snapshot();
        Assert.AreEqual((long)threads * each, counts.Operations.Single().Count, "a count was lost between threads");
        Assert.AreEqual((long)threads * each, counts.Paths.Sum(path => path.Count), "a path count was lost between threads");
    }

    /// <summary>Paths past the ranking are pooled rather than forgotten: the totals still describe
    /// everything that happened, and the reader is told how much the list does not account for.</summary>
    [TestMethod]
    public void Paths_past_the_ranking_are_rolled_up_not_dropped()
    {
        var counters = new RecordingCounters();
        for (int i = 0; i < 200; i++) counters.Count("VFS.Lookup", "path-" + i, 1 + i);
        var counts = counters.Snapshot(topPaths: 5);
        Assert.AreEqual(5, counts.Paths.Count);
        Assert.AreEqual(195, counts.PathsUnlisted);
        // Every event is in the operation totals whatever the path list did with it.
        Assert.AreEqual(200, counts.Operations.Single().Count);
        Assert.AreEqual(200, counts.Paths.Sum(path => path.Count) + counts.EventsUnlisted);
        Assert.AreEqual(counts.TotalMs, counts.Paths.Sum(path => path.TotalMs) + counts.MsUnlisted, 0.001);
    }
}
