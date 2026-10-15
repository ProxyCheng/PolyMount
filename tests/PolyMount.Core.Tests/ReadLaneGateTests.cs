using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;

namespace PolyMount.Core.Tests;

/// <summary>
/// Sharing one source out between priority classes. The claims are the ones the design is for:
/// a class keeps what is reserved for it, the reader's class is never held up by read-ahead
/// being busy, read-ahead stops taking spare room while the reader is waiting, and a request a
/// reader turns out to want stops waiting the moment the reader asks for it.
/// </summary>
[TestClass]
public sealed class ReadLaneGateTests
{
    private const int Reader = ReadPriority.Demand;
    private const int ReadAhead = ReadPriority.Speculative;

    /// <summary>Waits for another thread to bring a condition about.</summary>
    private static bool Until(Func<bool> condition, int milliseconds = 5000)
    {
        long deadline = Environment.TickCount64 + milliseconds;
        while (!condition() && Environment.TickCount64 < deadline) Thread.Sleep(2);
        return condition();
    }

    /// <summary>One request at the source. It keeps its place until <see cref="Hold"/> is set,
    /// so a test observes what the gate is doing instead of racing work that finishes in
    /// microseconds.</summary>
    private sealed class Request
    {
        public readonly ManualResetEventSlim Hold = new(false);
        public readonly ManualResetEventSlim Admitted = new(false);
        public Thread Runner = null!;
        public Exception? Failure;
    }

    private static Request Start(ReadLaneGate gate, int priority)
    {
        var request = new Request();
        request.Runner = new Thread(() =>
        {
            try
            {
                using (gate.Enter(priority)) { request.Admitted.Set(); request.Hold.Wait(TimeSpan.FromSeconds(10)); }
            }
            catch (Exception ex) { request.Failure = ex; request.Admitted.Set(); }
        });
        request.Runner.Start();
        return request;
    }

    /// <summary>Waits for the request to reach the source, then reports whether it did.</summary>
    private static bool Admitted(Request request, int milliseconds = 5000) =>
        request.Admitted.Wait(milliseconds) && request.Failure == null;

    private static int InFlight(ReadLaneGate gate, int priority) => gate.Snapshot()[priority].InFlight;
    private static int Queued(ReadLaneGate gate, int priority) => gate.Snapshot()[priority].Queued;

    private static void Finish(params Request[] requests)
    {
        foreach (var request in requests) request.Hold.Set();
        foreach (var request in requests) Assert.IsTrue(request.Runner.Join(TimeSpan.FromSeconds(10)), "a request never finished");
        foreach (var request in requests) Assert.IsNull(request.Failure, request.Failure?.Message);
    }

    [TestMethod]
    public void TheSourceServesNoMoreThanItsCapacityAtOnce()
    {
        var gate = new ReadLaneGate(2, new ReadLevel(2, 2));
        var requests = Enumerable.Range(0, 6).Select(_ => Start(gate, Reader)).ToArray();
        Assert.IsTrue(Until(() => InFlight(gate, Reader) == 2), "the source never filled up");
        Thread.Sleep(100);
        Assert.AreEqual(2, InFlight(gate, Reader), "the source served past its capacity");
        Assert.AreEqual(4, Queued(gate, Reader), "the rest should be waiting");

        Finish(requests);
        Assert.AreEqual(0, InFlight(gate, Reader));
        Assert.AreEqual(6, gate.Snapshot()[Reader].Requests);
    }

    [TestMethod]
    public void EveryClassKeepsWhatIsReservedForIt()
    {
        // The reader's class is guaranteed all of the source bar read-ahead's single reserved
        // request, so with the reader filling it, read-ahead's own still runs.
        var gate = new ReadLaneGate(4, new ReadLevel(3, 4), new ReadLevel(1, 1));
        var readAhead = Start(gate, ReadAhead);
        var readers = Enumerable.Range(0, 3).Select(_ => Start(gate, Reader)).ToArray();
        Assert.IsTrue(Until(() => InFlight(gate, Reader) == 3 && InFlight(gate, ReadAhead) == 1),
            $"the two classes did not both run: reader={InFlight(gate, Reader)} read-ahead={InFlight(gate, ReadAhead)}");
        Assert.AreEqual(0, Queued(gate, Reader), "the reader did not get the share kept for it");

        // Read-ahead is at its reserved request and that is the most it may hold.
        var extra = Start(gate, ReadAhead);
        Assert.IsTrue(Until(() => gate.Snapshot()[ReadAhead].Requests == 2), "the extra request was not published");
        Thread.Sleep(100);
        Assert.IsFalse(extra.Admitted.IsSet, "read-ahead held more than its share");
        Assert.AreEqual(1, Queued(gate, ReadAhead));

        Finish(extra);
        Finish(readers);
        Finish(readAhead);
    }

    [TestMethod]
    public void ReadAheadTakesSpareRoomOnlyWhileTheReaderIsNotWaiting()
    {
        // One request kept for the reader, one for read-ahead, and one either may use.
        var gate = new ReadLaneGate(3, new ReadLevel(1, 3), new ReadLevel(1, 2));
        var reader = Start(gate, Reader);
        Assert.IsTrue(Admitted(reader), "the reader did not reach the source");

        // With nothing waiting, read-ahead may use its share and the spare room.
        var first = Start(gate, ReadAhead);
        var second = Start(gate, ReadAhead);
        Assert.IsTrue(Until(() => InFlight(gate, ReadAhead) == 2), "read-ahead did not use the spare room");

        // A reader arriving queues — the source is full — and read-ahead stops adding.
        var waiting = Start(gate, Reader);
        Assert.IsTrue(Until(() => Queued(gate, Reader) == 1), "the second reader was not queued");
        var late = Start(gate, ReadAhead);
        Assert.IsTrue(Until(() => gate.Snapshot()[ReadAhead].Requests == 3), "the late read-ahead request was not published");
        Thread.Sleep(100);
        Assert.IsFalse(late.Admitted.IsSet, "read-ahead took room while the reader was waiting");

        // The reader has room kept for it, so it takes the first place given back, ahead of the
        // read-ahead request waiting for the same room.
        second.Hold.Set();
        Assert.IsTrue(Admitted(waiting), "the waiting reader did not get the freed room");
        Assert.AreEqual(2, InFlight(gate, Reader), "the reader did not fill what read-ahead gave up");
        Assert.IsFalse(late.Admitted.IsSet, "read-ahead ran ahead of the reader waiting for the same room");

        Finish(second, reader, first, waiting, late);
    }

    [TestMethod]
    public void ALowerClassYieldsWhileAMoreUrgentOneWaits()
    {
        // A class above its floor takes spare room only while nothing more urgent is waiting,
        // even when that room would otherwise go unused.
        var gate = new ReadLaneGate(6, new ReadLevel(1, 6), new ReadLevel(1, 1), new ReadLevel(1, 2));
        var reader = Start(gate, 0);
        var middle = Start(gate, 1);
        var low = Start(gate, 2);
        Assert.IsTrue(Until(() => gate.Snapshot().Sum(stat => stat.InFlight) == 3), "the source is not three quarters idle");
        Assert.AreEqual(1, InFlight(gate, 2), "the lowest class did not get the share reserved for it");

        // The middle class is full, so its next request waits with room still to spare...
        var blocked = Start(gate, 1);
        Assert.IsTrue(Until(() => Queued(gate, 1) == 1), "the middle class did not fill up");

        // ...and the lowest class does not take the spare room while it is waiting.
        var late = Start(gate, 2);
        Assert.IsTrue(Until(() => gate.Snapshot()[2].Requests == 2), "the late request was not published");
        Thread.Sleep(100);
        Assert.IsFalse(late.Admitted.IsSet, "a less urgent class took room while a more urgent one was waiting");
        Assert.AreEqual(1, Queued(gate, 2));

        // Draining the middle class lets both of the waiting requests through.
        middle.Hold.Set();
        Assert.IsTrue(Admitted(blocked), "the queued middle request did not run");
        Assert.IsTrue(Admitted(late), "the freed room did not reach the request waiting for it");

        Finish(late, blocked, middle, low, reader);
    }

    [TestMethod]
    public void PromotionMovesAQueuedRequestIntoTheReadersClassAndRunsIt()
    {
        var gate = new ReadLaneGate(2, new ReadLevel(1, 2), new ReadLevel(1, 1));
        var holder = Start(gate, ReadAhead);
        Assert.IsTrue(Admitted(holder), "read-ahead did not take the source");

        var queued = gate.Begin(ReadAhead);
        using var promotedHold = new ManualResetEventSlim(false);
        bool admitted = false;
        var waiter = new Thread(() =>
        {
            using (queued.Admit()) { admitted = true; promotedHold.Wait(TimeSpan.FromSeconds(10)); }
        });
        waiter.Start();
        Assert.IsTrue(Until(() => gate.Snapshot()[ReadAhead].Requests == 2), "the second request was not published");
        Thread.Sleep(100);
        Assert.IsFalse(admitted, "read-ahead held more than its share");

        // The reader arriving is what lets it through: the request becomes the reader's, so it
        // is admitted in the reader's class instead of waiting in read-ahead's.
        queued.Promote(Reader);
        Assert.IsTrue(Until(() => admitted), "a promoted request stayed in the queue");
        Assert.AreEqual(1, gate.Snapshot()[ReadAhead].Promotions);
        Assert.AreEqual(1, InFlight(gate, ReadAhead), "the promoted request did not leave its class");
        Assert.AreEqual(1, InFlight(gate, Reader), "the promoted request did not join the reader's class");

        promotedHold.Set();
        Assert.IsTrue(waiter.Join(TimeSpan.FromSeconds(5)));
        Finish(holder);
        Assert.AreEqual(0, InFlight(gate, Reader));
        Assert.AreEqual(0, InFlight(gate, ReadAhead));
    }

    [TestMethod]
    public void ARequestOfTheSameOrLowerPriorityIsNotAClaim()
    {
        var gate = new ReadLaneGate(2, new ReadLevel(1, 1), new ReadLevel(1, 1));
        var holder = Start(gate, ReadAhead);
        Assert.IsTrue(Admitted(holder));

        var queued = gate.Begin(ReadAhead);
        bool admitted = false;
        var waiter = new Thread(() => { using (queued.Admit()) admitted = true; });
        waiter.Start();
        Assert.IsTrue(Until(() => gate.Snapshot()[ReadAhead].Requests == 2));
        Thread.Sleep(100);

        queued.Promote(ReadAhead);
        Thread.Sleep(100);
        Assert.IsFalse(admitted, "a request of the same priority was let through as a claim");
        Assert.AreEqual(0, gate.Snapshot()[ReadAhead].Promotions);

        Finish(holder);
        Assert.IsTrue(waiter.Join(TimeSpan.FromSeconds(5)), "the request never got the place it was waiting for");
    }

    [TestMethod]
    public void APlaceIsHandedToWhoeverIsNextInTheSameClass()
    {
        var gate = new ReadLaneGate(1, new ReadLevel(1, 1));
        var order = new List<string>();
        using var secondHold = new ManualResetEventSlim(false);
        using var thirdHold = new ManualResetEventSlim(false);
        var first = gate.Enter(Reader);

        var second = gate.Begin(Reader);
        var secondThread = new Thread(() =>
        {
            using (second.Admit()) { lock (order) order.Add("second"); secondHold.Wait(TimeSpan.FromSeconds(10)); }
        });
        secondThread.Start();
        var third = gate.Begin(Reader);
        var thirdThread = new Thread(() =>
        {
            using (third.Admit()) { lock (order) order.Add("third"); thirdHold.Wait(TimeSpan.FromSeconds(10)); }
        });
        thirdThread.Start();
        Assert.IsTrue(Until(() => gate.Snapshot()[Reader].Requests == 3), "the queued requests were not published");
        Thread.Sleep(100);
        lock (order) Assert.AreEqual(0, order.Count, "the source served more than it has capacity for");

        first.Dispose();
        Assert.IsTrue(Until(() => { lock (order) return order.Count == 1; }), "the freed place was not handed on");
        lock (order) Assert.AreEqual("second", order[0], "the source let a later request past the one waiting longest");
        Assert.AreEqual(1, InFlight(gate, Reader), "the source ran two at once, or none after the hand-over");

        secondHold.Set();
        Assert.IsTrue(Until(() => { lock (order) return order.Count == 2; }), "the second place was not handed on");
        lock (order) Assert.AreEqual("third", order[1]);
        Assert.AreEqual(1, InFlight(gate, Reader), "the source was not kept busy across the hand-over");

        thirdHold.Set();
        Assert.IsTrue(thirdThread.Join(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(0, InFlight(gate, Reader));
    }

    [TestMethod]
    public void TheClassesSharesHaveToFitInTheSource()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new ReadLaneGate(2, new ReadLevel(2, 2), new ReadLevel(1, 1)));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new ReadLaneGate(4, new ReadLevel(3, 2)));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new ReadLaneGate(4, new ReadLevel(0, 1)));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new ReadLaneGate(4, new ReadLevel(1, 5)));
        new ReadLaneGate(4, new ReadLevel(2, 4), new ReadLevel(2, 4));
    }
}
