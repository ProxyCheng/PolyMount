using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Core.Ipc;

namespace PolyMount.Core.Tests;

/// <summary>
/// The collector keeps cells per layer per slice of time rather than a ring of events, so what it
/// answers is a sum over the cells a window covers. These pin the two things that buys - nothing is
/// ever truncated by traffic, and a report says how much time it really watched - and the one thing
/// it costs, which is that a window is answered in whole cells.
/// </summary>
[TestClass]
public class MetricsCellTests
{
    /// <summary>A collector with a caller-controlled clock, watched - as one is the moment a front
    /// end asks it for a window, which is what turns counting on.</summary>
    private static MetricsCollector At(Func<long> clock)
    {
        var collector = new MetricsCollector(clock);
        collector.Watch();
        return collector;
    }

    [TestMethod]
    public void Nothing_is_counted_until_something_asks()
    {
        long slot = 1_000;
        var idle = new MetricsCollector(() => slot);
        idle.RecordRead("cache", TimeSpan.FromMicroseconds(500), 1024);
        // Nothing is watching, so nothing is counted. The activity stamp moves anyway: the warm-up
        // reads it to stay out of a busy mount's way, and an unwatched mount is not an idle one.
        Assert.IsTrue(idle.IdleFor < TimeSpan.FromSeconds(5), "activity is stamped whether or not it is counted");
        Assert.AreEqual(0, idle.Snapshot("m", 1).TotalReads, "an unwatched instance counts nothing");

        idle.Watch();
        idle.RecordRead("cache", TimeSpan.FromMicroseconds(500), 1024);
        Assert.AreEqual(1, idle.Snapshot("m", 1).TotalReads, "being asked is what starts it");
        Assert.IsTrue(idle.Watched);
    }

    [TestMethod]
    public void A_window_adds_up_the_cells_it_covers()
    {
        long slot = 1_000;
        var collector = At(() => slot);
        collector.RecordRead("cache", TimeSpan.FromMicroseconds(500), 1024, cached: true);
        collector.RecordRead("cache", TimeSpan.FromMicroseconds(1500), 2048);
        collector.RecordWrite("cache", TimeSpan.Zero, 4096);
        collector.RecordRead("svn", TimeSpan.FromMicroseconds(2000), 0);

        var metrics = collector.Snapshot("m", 1);
        var cache = metrics.Layers.Single(l => l.Name == "cache");
        var svn = metrics.Layers.Single(l => l.Name == "svn");

        Assert.AreEqual(2, cache.Hits, "cache reads");
        Assert.AreEqual(1, cache.Writes, "cache writes");
        Assert.AreEqual(3072, cache.Bytes, "every read carries the bytes it resolved, cached or not");
        Assert.AreEqual(4096, cache.WriteBytes);
        Assert.AreEqual(1.0, cache.AvgCostMs, 1e-9, "A means are over the reads in the window");
        Assert.AreEqual(1.5, cache.MaxCostMs, 1e-9);
        Assert.AreEqual(1, svn.Hits);
        Assert.AreEqual(3, metrics.TotalReads);
        Assert.AreEqual(1, metrics.TotalWrites);
        Assert.AreEqual(100.0 / 3, metrics.CacheHitPercent, 1e-9, "one of the three reads in the window was served by a cache");
    }

    [TestMethod]
    public void A_write_is_timed_apart_from_a_read_and_from_the_bytes_it_carried()
    {
        long slot = 1_000;
        var collector = At(() => slot);
        collector.RecordWrite("overlay", TimeSpan.FromMilliseconds(4), 0);
        collector.RecordWrite("overlay", TimeSpan.FromMilliseconds(2), 0);
        // What an application writes into the staging handle is bytes, not a layer operation: it
        // must not become a third write beside the two the layer really applied, or the average
        // would be measured over memory copies.
        collector.RecordWriteBytes("overlay", 12_000);

        var overlay = collector.Snapshot("m", 1).Layers.Single(l => l.Name == "overlay");
        Assert.AreEqual(2, overlay.Writes, "the staging bytes are not a write of their own");
        Assert.AreEqual(12_000, overlay.WriteBytes);
        Assert.AreEqual(3.0, overlay.AvgWriteCostMs, 1e-9, "a write's mean is over the writes applied in the window");
        Assert.AreEqual(4.0, overlay.MaxWriteCostMs, 1e-9);
        Assert.AreEqual(0.0, overlay.AvgCostMs, 1e-9, "a write's duration is not a read's");
    }

    [TestMethod]
    public void Traffic_cannot_push_a_window_out_of_the_ring()
    {
        long slot = 1_000;
        var collector = At(() => slot);
        // Five thousand is what the collector used to hold, and everything past it was dropped no
        // matter what window was asked for. Ten thousand in one cell is ten thousand reported.
        for (int i = 0; i < 10_000; i++) collector.RecordRead("cache", TimeSpan.Zero, 8);

        var metrics = collector.Snapshot("m", 1);
        Assert.AreEqual(10_000, metrics.TotalReads, "a window is bounded by time, not by a sample count");
        Assert.AreEqual(10_000, metrics.Layers.Single(l => l.Name == "cache").Hits);
    }

    [TestMethod]
    public void A_report_says_how_much_time_it_really_watched()
    {
        long slot = 500_000;
        var collector = At(() => slot);
        collector.RecordRead("cache", TimeSpan.Zero, 8);

        // A process ten milliseconds old has watched ten milliseconds, however long a window it is
        // asked for; a rate divided by the minute it was asked about would be short by six hundred.
        slot += 1;
        collector.RecordRead("cache", TimeSpan.Zero, 8);
        var fresh = collector.Snapshot("m", 60);
        Assert.AreEqual(0.02, fresh.MeasuredSeconds, 1e-9, "two hundred-millisecond cells at ten milliseconds each");

        // Once it has been up longer than the window, the window is what it watched - including the
        // cells nothing happened in, because an idle stretch is a measured zero and not a gap.
        slot += 600;
        var settled = collector.Snapshot("m", 1);
        Assert.AreEqual(1.0, settled.MeasuredSeconds, 1e-9);
        // A window nothing happened in reports no layers, because a layer with no events is not a
        // layer that served nothing to measure. What every configured layer did over the window is
        // the mount's answer to give, not the collector's - it lists them from the chain.
        Assert.AreEqual(0, settled.Layers.Count, "the reads are a minute old now; the window holds nothing");
        Assert.AreEqual(0, settled.TotalReads);
    }

    [TestMethod]
    public void A_window_is_answered_in_whole_cells()
    {
        long slot = 1_000;
        var collector = At(() => slot);
        collector.RecordRead("cache", TimeSpan.Zero, 8);

        // Whatever it is asked for, a window reaches back over the cells it covers - never less
        // than the one the traffic landed in, and never more than the ring holds. What a reader
        // must not do is divide these amounts by the window it asked for: the report carries the
        // span it really covered for exactly that reason.
        var narrow = collector.Snapshot("m", 0.001);
        Assert.AreEqual(1, narrow.TotalReads, "a window below one cell is still one cell");
        Assert.AreEqual(MetricsCollector.CellMilliseconds / 1000.0, narrow.MeasuredSeconds, 1e-9);

        var wide = collector.Snapshot("m", 86_400);
        Assert.AreEqual(1, wide.TotalReads);
        Assert.AreEqual(MetricsCollector.CellMilliseconds / 1000.0, wide.MeasuredSeconds, 1e-9,
            "a day asked of a collector a moment old is answered with the moment it has watched");

        // And an hour later the same request is answered with the ring, which is all it keeps.
        slot += 360_000;
        var aged = collector.Snapshot("m", 86_400);
        Assert.AreEqual(MetricsCollector.CellCount * MetricsCollector.CellMilliseconds / 1000.0, aged.MeasuredSeconds, 1e-9);
        Assert.AreEqual(0, aged.TotalReads, "the read is long out of the ring by then");
    }

    [TestMethod]
    public void Activity_is_recorded_even_when_nobody_asks()
    {
        long slot = 1_000;
        var collector = At(() => slot);
        collector.RecordRead("cache", TimeSpan.Zero, 8);
        // The warm-up reads IdleFor to stay out of a busy mount's way, so the stamp has to move
        // whether or not anything is being collected for a window.
        Assert.IsTrue(collector.IdleFor < TimeSpan.FromSeconds(5), "a read the collector was just told about is not idle");
    }
}
