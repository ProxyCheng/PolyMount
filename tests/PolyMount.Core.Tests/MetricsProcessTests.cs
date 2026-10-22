using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Core.Ipc;

namespace PolyMount.Core.Tests;

/// <summary>
/// Who asked, beside what answered. The layer rings say which layer served a request; these say
/// which process made it, which is the one question a mount's own metrics could not answer — and
/// the dimension exists for a pie whose tail is folded, so what is pinned here is that the fold
/// loses nothing, that a process is still named while its window lasts, and that a request with no
/// caller is not attributed to one.
/// </summary>
[TestClass]
public class MetricsProcessTests
{
    /// <summary>A collector watched, as one is the moment a front end asks it for a window — which
    /// is also what turns counting on.</summary>
    private static MetricsCollector At(Func<long> clock)
    {
        var collector = new MetricsCollector(clock);
        collector.Watch();
        return collector;
    }

    /// <summary>Slots are the collector's own unit: a tenth of a second each, a hundred to the
    /// second the process dimension is kept at.</summary>
    private static long Slots(double seconds) => (long)(seconds * 100);

    [TestMethod]
    public void A_request_is_attributed_to_its_caller()
    {
        long slot = 1_000;
        var collector = At(() => slot);
        collector.RecordProcessAccess(4321, write: false, bytes: 4096);
        collector.RecordProcessAccess(4321, write: true, bytes: 512);
        collector.RecordProcessAccess(9999, write: false, bytes: 1024);

        var metrics = collector.Snapshot("m", 60);
        var one = metrics.Processes.Single(row => row.Pid == 4321);
        Assert.AreEqual(1, one.Reads);
        Assert.AreEqual(1, one.Writes);
        Assert.AreEqual(4096, one.ReadBytes);
        Assert.AreEqual(512, one.WriteBytes);
        Assert.AreEqual(1, metrics.Processes.Single(row => row.Pid == 9999).Reads);
        Assert.AreNotEqual(one.Color, metrics.Processes.Single(row => row.Pid == 9999).Color,
            "two processes are two colours");
    }

    [TestMethod]
    public void A_request_with_no_caller_is_nobody_s()
    {
        long slot = 1_000;
        var collector = At(() => slot);
        collector.RecordRead("cache", TimeSpan.Zero, 1024);
        // What the service fetches for its own read-ahead is done for a guess rather than for a
        // caller: it is counted as a request, and attributed to no process.
        collector.RecordProcessAccess(0, write: false, bytes: 1024);

        var metrics = collector.Snapshot("m", 60);
        Assert.AreEqual(0, metrics.Processes.Count, "nobody asked for it");
    }

    [TestMethod]
    public void A_process_that_has_gone_quiet_is_still_named_while_its_window_lasts()
    {
        long slot = 1_000;
        var collector = At(() => slot);
        collector.RecordProcessAccess(4321, write: false);
        slot += Slots(30);
        Assert.AreEqual(1, collector.Snapshot("m", 60).Processes.Count, "half a minute back in a one-minute window");
        slot += Slots(31);
        Assert.AreEqual(0, collector.Snapshot("m", 60).Processes.Count, "and gone once it falls out of it");
    }

    /// <summary>A pid range no real process sits in. The fold groups by process name, so low ids
    /// are a trap: on a build runner they are actual processes, several of them share a name, and
    /// they collapse into fewer rows than the test put in — the test then measures the machine's
    /// process table instead of the fold.</summary>
    private const int UnusedPid = 2_000_000_000;

    [TestMethod]
    public void The_tail_is_folded_rather_than_dropped()
    {
        long slot = 1_000;
        var collector = At(() => slot);
        for (int i = 0; i < 20; i++)
            for (int n = 0; n <= i; n++)
                collector.RecordProcessAccess(UnusedPid + i, write: false);

        var metrics = collector.Snapshot("m", 60);
        Assert.AreEqual(ProcessFold.Named + 1, metrics.Processes.Count, "the named ones and one bucket");
        var other = metrics.Processes.Single(row => row.Other);
        Assert.AreEqual(20 * 21 / 2 - metrics.Processes.Where(row => !row.Other).Sum(row => row.Reads), other.Reads,
            "the bucket is everything the named rows are not");
        Assert.AreEqual(20 * 21 / 2, metrics.Processes.Sum(row => row.Reads), "nothing is lost in the fold");
    }

    [TestMethod]
    public void A_process_that_cannot_be_named_is_named_by_its_id()
    {
        long slot = 1_000;
        var collector = At(() => slot);
        collector.RecordProcessAccess(int.MaxValue, write: false);

        var metrics = collector.Snapshot("m", 60);
        var row = metrics.Processes.Single();
        Assert.AreEqual(int.MaxValue, row.Pid);
        Assert.AreEqual("", row.Name, "a process that cannot be read has no name to give");
        Assert.IsTrue(row.Color.StartsWith('#'), "it still gets a colour of its own");
    }

    [TestMethod]
    public void This_process_can_be_named()
    {
        string? name = ProcessNames.Of(Environment.ProcessId);
        Assert.IsFalse(string.IsNullOrEmpty(name), "the test host is a process like any other");
        Assert.AreEqual(name, ProcessNames.Of(Environment.ProcessId), "and it is answered from the cache");
        Assert.IsNull(ProcessNames.Of(0), "no id is no process");
    }

    [TestMethod]
    public void A_window_is_answered_in_whole_seconds()
    {
        long slot = 1_000;
        var collector = At(() => slot);
        collector.RecordProcessAccess(4321, write: false);
        Assert.AreEqual(1, collector.Snapshot("m", 1).Processes.Single().Reads, "the second in progress");
        // The next second has nothing of its own, so a window on it alone is empty while the minute
        // that covers both still holds it.
        slot += Slots(1.5);
        Assert.AreEqual(0, collector.Snapshot("m", 1).Processes.Count);
        Assert.AreEqual(1, collector.Snapshot("m", 60).Processes.Single().Reads);
    }

    [TestMethod]
    public void Nothing_is_counted_until_something_asks_here_either()
    {
        long slot = 1_000;
        var idle = new MetricsCollector(() => slot);
        idle.RecordProcessAccess(4321, write: false);
        Assert.AreEqual(0, idle.Snapshot("m", 60).Processes.Count, "an unwatched instance counts nobody");
        idle.Watch();
        idle.RecordProcessAccess(4321, write: false);
        Assert.AreEqual(1, idle.Snapshot("m", 60).Processes.Single().Reads, "being asked is what starts it");
    }

    /// <summary>A read served by a layer, taken while a request is in hand. The collector finds the
    /// caller in the scope the record is running inside rather than being told: neither the layer
    /// nor the VFS below the boundary knows who asked, and the boundary is the only place WinFsp
    /// can name them.</summary>
    private static void ServeAs(MetricsCollector collector, RequestDiagnostics diagnostics, int processId, string layer, bool write)
    {
        using var boundary = RequestDiagnostics.Begin("WinFsp.Read", "/x", diagnostics, processId);
        // The VFS and the layer open scopes of their own and name no caller; that the caller still
        // reaches the collector is the whole of what is pinned here.
        using var nested = RequestDiagnostics.Begin("VFS.OpenRead", "/x", diagnostics);
        using var deepest = RequestDiagnostics.Begin("Layer.GetEntry", "/x", diagnostics);
        if (write) collector.RecordWrite(layer, TimeSpan.Zero, 1);
        else collector.RecordRead(layer, TimeSpan.Zero, 1);
    }

    /// <summary>A tracer for the tests that need a request around the record. A real one rather
    /// than a stand-in: the caller is found in this type's ambient scope, and what is being pinned
    /// is that the scopes the VFS opens below the boundary do not lose it.</summary>
    private static RequestDiagnostics Tracing(TempDir temp) => new(temp.Sub("requests.log"));

    /// <summary>A write with no request scope around it — the commit a staging handle makes when
    /// it is flushed or cleaned up — is still charged to the process whose handle it is. Those
    /// callbacks cannot ask WinFsp who is calling, so the boundary marks the caller the handle
    /// remembered; without the mark the write cards put every commit in the bucket for nobody.</summary>
    [TestMethod]
    public void A_commit_is_charged_to_the_caller_the_handle_remembered()
    {
        long slot = 1_000;
        var collector = At(() => slot);
        using (RequestDiagnostics.ActingFor(4321))
            collector.RecordWrite("overlay", TimeSpan.Zero, 1);
        // Outside that mark the same commit is the service's own work, which is what a reader of
        // the card sees as "no process to attribute it to".
        collector.RecordWrite("overlay", TimeSpan.Zero, 1);

        var metrics = collector.Snapshot("m", 60);
        Assert.AreEqual(1, metrics.Processes.Single(row => row.Pid == 4321)
            .ByLayer.Single(layer => layer.Name == "overlay").Writes);
        Assert.AreEqual(1, metrics.Unattributed.Single(layer => layer.Name == "overlay").Writes);
        Assert.AreEqual(2, metrics.Layers.Single(layer => layer.Name == "overlay").Writes,
            "the layer's own count is the same either way");
    }

    [TestMethod]
    public void A_layer_is_charged_to_the_caller_of_the_request_it_served()
    {
        long slot = 1_000;
        var collector = At(() => slot);
        using var temp = new TempDir();
        using var diagnostics = Tracing(temp);
        ServeAs(collector, diagnostics, 4321, "cache", write: false);
        ServeAs(collector, diagnostics, 4321, "svn", write: false);
        ServeAs(collector, diagnostics, 9999, "cache", write: false);
        ServeAs(collector, diagnostics, 9999, "cache", write: false);

        var metrics = collector.Snapshot("m", 60);
        var one = metrics.Processes.Single(row => row.Pid == 4321);
        Assert.AreEqual(2, one.ByLayer.Count, "the two layers it was served by");
        Assert.AreEqual(1, one.ByLayer.Single(row => row.Name == "cache").Reads);
        Assert.AreEqual(1, one.ByLayer.Single(row => row.Name == "svn").Reads);
        var other = metrics.Processes.Single(row => row.Pid == 9999);
        Assert.AreEqual(2, other.ByLayer.Single(row => row.Name == "cache").Reads,
            "the same layer is counted apart for the two callers");
    }

    [TestMethod]
    public void A_layer_served_with_nobody_in_scope_is_nobody_s()
    {
        long slot = 1_000;
        var collector = At(() => slot);
        using var temp = new TempDir();
        using var diagnostics = Tracing(temp);
        ServeAs(collector, diagnostics, 4321, "cache", write: false);
        // Read-ahead runs in a task whose flow was suppressed, so there is no scope around it and
        // the caller it finds is nobody — the same reading the boundary takes when a request names
        // no process.
        collector.RecordRead("svn", TimeSpan.Zero, 4096);

        var metrics = collector.Snapshot("m", 60);
        Assert.AreEqual(1, metrics.Processes.Count, "the tool that asked");
        Assert.AreEqual("svn", metrics.Unattributed.Single().Name);
        Assert.AreEqual(1, metrics.Unattributed.Single().Reads, "the layer the service read for itself");
    }

    [TestMethod]
    public void Every_layer_a_process_used_adds_up_to_what_the_layers_served()
    {
        long slot = 1_000;
        var collector = At(() => slot);
        using var temp = new TempDir();
        using var diagnostics = Tracing(temp);
        // Two callers and the service's own work, over two layers: whatever a reader does with the
        // rows, the column totals have to be the layer ring's own counts.
        ServeAs(collector, diagnostics, 4321, "cache", write: false);
        ServeAs(collector, diagnostics, 4321, "svn", write: true);
        ServeAs(collector, diagnostics, 9999, "cache", write: false);
        collector.RecordRead("cache", TimeSpan.Zero, 4096);

        var metrics = collector.Snapshot("m", 60);
        var byLayer = metrics.Processes
            .SelectMany(row => row.ByLayer)
            .Concat(metrics.Unattributed)
            .GroupBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (Reads: group.Sum(row => row.Reads), Writes: group.Sum(row => row.Writes)));
        foreach (var layer in metrics.Layers)
        {
            Assert.AreEqual(layer.Hits, byLayer[layer.Name].Reads, $"reads on {layer.Name}");
            Assert.AreEqual(layer.Writes, byLayer[layer.Name].Writes, $"writes on {layer.Name}");
        }
        Assert.AreEqual(metrics.TotalReads, byLayer.Values.Sum(pair => pair.Reads), "and nothing is left over");
    }

    [TestMethod]
    public void The_fold_carries_the_layers_of_what_it_folds()
    {
        long slot = 1_000;
        var collector = At(() => slot);
        using var temp = new TempDir();
        using var diagnostics = Tracing(temp);
        // More callers than a window names, each served by both layers, so the bucket has to hold
        // the layers of the processes it swallowed or the columns stop adding up at the fold.
        int callers = ProcessFold.Named + 4;
        for (int i = 0; i < callers; i++)
        {
            ServeAs(collector, diagnostics, UnusedPid + i, "cache", write: false);
            ServeAs(collector, diagnostics, UnusedPid + i, "svn", write: false);
        }

        var metrics = collector.Snapshot("m", 60);
        Assert.AreEqual(ProcessFold.Named + 1, metrics.Processes.Count, "the named ones and one bucket");
        var other = metrics.Processes.Single(row => row.Other);
        Assert.AreEqual(2, other.ByLayer.Count);
        Assert.AreEqual(4, other.ByLayer.Single(row => row.Name == "cache").Reads, "the folded tail's own layers");
        Assert.AreEqual(callers, metrics.Layers.Single(layer => layer.Name == "cache").Hits,
            "every caller read the one layer, named or folded");
        Assert.AreEqual(2 * callers, metrics.Processes.SelectMany(row => row.ByLayer).Sum(row => row.Reads),
            "and the rows still carry every read the layers counted");
    }
}
