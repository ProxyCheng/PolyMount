using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using Serilog.Events;

namespace PolyMount.Core.Tests;

/// <summary>The handoff every recorded event crosses: many request threads in, one writer out.</summary>
[TestClass]
public sealed class LogRingTests
{
    [TestMethod]
    public void Entries_come_out_in_the_order_they_went_in()
    {
        var ring = new LogRing(1024);
        for (int i = 0; i < 500; i++) Assert.IsTrue(ring.TryWrite(LogEventLevel.Information, "m", new object?[] { i }));
        for (int i = 0; i < 500; i++)
        {
            Assert.IsTrue(ring.TryRead(out var level, out var template, out var args));
            Assert.AreEqual(LogEventLevel.Information, level);
            Assert.AreEqual("m", template);
            Assert.AreEqual(i, args[0]);
        }
        Assert.AreEqual(0, ring.Dropped);
    }

    /// <summary>A full ring turns entries away and says how many, rather than making the thread
    /// that logs wait — that thread is serving the request being measured.</summary>
    [TestMethod]
    public void A_full_ring_drops_instead_of_blocking()
    {
        var ring = new LogRing(64);
        for (int i = 0; i < 64; i++) Assert.IsTrue(ring.TryWrite(LogEventLevel.Information, "m", new object?[] { i }));
        Assert.IsFalse(ring.TryWrite(LogEventLevel.Information, "m", new object?[] { 64 }));
        Assert.AreEqual(1, ring.Dropped);
        Assert.AreEqual(64, ring.Capacity);
    }

    /// <summary>Closing drains what was already handed over, and refuses what comes after.</summary>
    [TestMethod]
    public void Closing_drains_what_it_holds_then_refuses_more()
    {
        var ring = new LogRing(64);
        for (int i = 0; i < 10; i++) ring.TryWrite(LogEventLevel.Warning, "m", new object?[] { i });
        ring.Close();
        for (int i = 0; i < 10; i++) Assert.IsTrue(ring.TryRead(out _, out _, out var args), $"entry {i} was lost on close");
        Assert.IsFalse(ring.TryRead(out _, out _, out _), "a drained and closed ring reported more entries");
        Assert.IsFalse(ring.TryWrite(LogEventLevel.Information, "m", new object?[] { 99 }), "a closed ring took a write");
        Assert.AreEqual(1, ring.Dropped, "a write refused after the close was not counted");
    }

    /// <summary>Many producers, one reader: every entry that was accepted is read exactly once.</summary>
    [TestMethod]
    public void Concurrent_producers_lose_nothing_the_ring_accepted()
    {
        var ring = new LogRing(1 << 16);
        const int threads = 8, each = 2000;
        long accepted = 0;
        var writers = new List<Thread>();
        using var barrier = new Barrier(threads);
        for (int t = 0; t < threads; t++)
        {
            var writer = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < each; i++)
                    if (ring.TryWrite(LogEventLevel.Information, "m", new object?[] { i })) Interlocked.Increment(ref accepted);
            });
            writer.Start();
            writers.Add(writer);
        }
        foreach (var writer in writers) writer.Join();
        ring.Close();
        long read = 0;
        while (ring.TryRead(out _, out _, out _)) read++;
        Assert.AreEqual(accepted, read, "the ring accepted entries it never handed over");
        Assert.AreEqual(0, ring.Dropped, "a ring this large had no reason to drop");
    }
}
