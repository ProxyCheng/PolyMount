using Serilog.Events;

namespace PolyMount.Core;

/// <summary>
/// A bounded handoff from many request threads to one writer. A producer claims a slot with an
/// interlocked ticket and then publishes it; the single consumer walks the slots in ticket order,
/// so ordering is preserved and neither side takes a lock.
///
/// Why this exists at all: rendering a log line is the expensive part (~1-3 µs of formatting and
/// allocation), and a logging library renders on the thread that logs. On a request path that is
/// the whole cost — the machine can have idle cores and it does not help, because the thread that
/// is slow is the one serving the request. Handing the values over and rendering them elsewhere
/// moves that cost off the request without changing the line that comes out.
///
/// A full ring drops and counts rather than blocking: the threads it would block are the ones
/// being measured.
/// </summary>
internal sealed class LogRing
{
    private sealed class Slot
    {
        /// <summary>Zero while the slot is free, otherwise the ticket that filled it plus one. The
        /// consumer waits for exactly this before reading the rest of the slot.</summary>
        public long Sequence;
        public LogEventLevel Level;
        public string Template = "";
        public object?[]? Args;
    }

    private readonly Slot[] _slots;
    private long _write, _read;
    private volatile bool _closed;

    public LogRing(int capacity = 262144)
    {
        _slots = new Slot[capacity];
        for (int i = 0; i < capacity; i++) _slots[i] = new Slot();
    }

    /// <summary>How many entries a full ring had to turn away.</summary>
    public long Dropped;

    public int Capacity => _slots.Length;

    /// <summary>Claims a slot, fills it and publishes it. False when the ring is full: the caller
    /// has already been counted, and must not wait.</summary>
    public bool TryWrite(LogEventLevel level, string template, object?[] args)
    {
        // A closed ring refuses and counts rather than accepting an entry nothing will read: a
        // write that arrives after the recording was asked to stop is lost either way, and being
        // counted is the difference between a number the reader can see and a silent hole.
        if (_closed) { Interlocked.Increment(ref Dropped); return false; }
        long ticket = Interlocked.Increment(ref _write) - 1;
        // One read of the consumer's position, which is a hint rather than a promise: a producer
        // that overwrites a slot the consumer is about to reach loses one line instead of a
        // recording, and this is the cheapest way to say "the ring is full".
        if (ticket - Volatile.Read(ref _read) >= _slots.Length)
        {
            Interlocked.Increment(ref Dropped);
            return false;
        }
        var slot = _slots[(int)(ticket % _slots.Length)];
        slot.Level = level;
        slot.Template = template;
        slot.Args = args;
        Volatile.Write(ref slot.Sequence, ticket + 1);
        return true;
    }

    /// <summary>The next entry in ticket order. Waits for a producer that has claimed a slot but
    /// not yet published it, and returns false only once the ring is closed and drained.</summary>
    public bool TryRead(out LogEventLevel level, out string template, out object?[] args)
    {
        var spins = new SpinWait();
        while (true)
        {
            long next = Volatile.Read(ref _read);
            var slot = _slots[(int)(next % _slots.Length)];
            if (Volatile.Read(ref slot.Sequence) == next + 1)
            {
                level = slot.Level;
                template = slot.Template;
                args = slot.Args ?? Array.Empty<object?>();
                slot.Args = null;
                slot.Template = "";
                Volatile.Write(ref slot.Sequence, 0); // free it for a producer
                Volatile.Write(ref _read, next + 1);
                return true;
            }
            if (Volatile.Read(ref _write) <= next)
            {
                // Nothing has been claimed beyond this point: an idle recording, or the end of one.
                if (_closed)
                {
                    // A producer that claimed its ticket a moment ago may not have published it
                    // yet, and its line was written before the close: give it a moment to appear
                    // rather than exiting on top of it.
                    Thread.Sleep(1);
                    if (Volatile.Read(ref _write) <= next)
                    {
                        level = default; template = ""; args = Array.Empty<object?>();
                        return false;
                    }
                    continue;
                }
                if (spins.NextSpinWillYield) Thread.Sleep(1);
                else spins.SpinOnce();
                continue;
            }
            // A ticket past ours exists but is not published yet: its producer is microseconds away.
            if (spins.NextSpinWillYield) Thread.Sleep(0);
            else spins.SpinOnce();
        }
    }

    /// <summary>No entry may be claimed after this. What is already published is still drained, and
    /// so is what a producer had claimed by the time this is called — which is what lets a caller
    /// stop a recording and know the file holds everything written before it decided to.</summary>
    public void Close() => _closed = true;
}
