using System.Collections.Concurrent;
using System.Diagnostics;
using PolyMount.Core.Ipc;

namespace PolyMount.Core;

/// <summary>
/// Thread-safe, bounded, rolling-window record of read events, used to compute per-layer hit
/// rates and access cost. Each read records which layer served it and how long resolution took
/// (including probing the layers above the hit).
///
/// It is a ring of cells, not a ring of events: one cell per layer per <see cref="CellMilliseconds"/>
/// of time, each holding the sums of what happened in it. A write is therefore a slot calculation
/// and an atomic add - no allocation, no queue, no lock, and no clock read beyond the coarse
/// timestamp the ring is addressed with. A window is summed from the cells that fall in it, so a
/// minute costs a few hundred additions however many reads happened in it, and nothing is ever
/// truncated: an hour of traffic cannot push the last seconds out, because seconds are all that is
/// kept.
///
/// This is how metrics that must not disturb their subject are usually built: a database's
/// statistics are atomic tickers read later, an event tracer's buffers are drained by another
/// process, and a round-robin archive keeps one fixed-size file at several resolutions. What they
/// have in common is that the hot path writes and never aggregates, and that the amount of history
/// is a size rather than a capacity that traffic can exhaust.
/// </summary>
public sealed class MetricsCollector
{
    /// <summary>The granularity of the ring. It has to be finer than the shortest window anyone
    /// asks for - the performance window refreshes at up to 10 Hz, so a tenth of a second - by
    /// enough that the quantisation is small next to the window. A tenth of a second of cells
    /// would make a tenth-of-a-second window one cell wide, which is not a measurement.</summary>
    internal const int CellMilliseconds = 10;

    /// <summary>A minute of history: every window an instance is asked for fits inside it (the
    /// slowest refresh is two seconds, the access window is sixty, a recording polls one), and the
    /// report says which part of it was actually measured.</summary>
    internal const int CellCount = 6000;

    private struct Cell
    {
        /// <summary>Which slot this cell holds, so a cell whose time has passed is recognised
        /// rather than summed. Only the writer that finds it stale resets it.</summary>
        public long Slot;
        public long Reads;
        public long Cached;
        public long Bytes;
        public long Writes;
        public long WriteBytes;
        public long CostUs;
        public long MaxCostUs;
    }

    private sealed class Ring
    {
        public readonly Cell[] Cells = new Cell[CellCount];
    }

    /// <summary>What one layer's cells add up to over a window.</summary>
    private readonly record struct Sums(long Reads, long Cached, long Bytes, long Writes, long WriteBytes, long CostUs, long MaxCostUs);

    /// <summary>The slot this collector was created in: the ring cannot have watched time before
    /// it existed, and saying so is what keeps a rate off a window the process never covered.</summary>
    private readonly long _startedSlot;

    // One ring per layer. A layer's reads arrive on whichever thread WinFsp handed the request to,
    // so this is the one shared structure on the write path; the counters inside a cell are atomic.
    private readonly ConcurrentDictionary<string, Ring> _layers = new(StringComparer.OrdinalIgnoreCase);
    private long _lastActivityMs;

    /// <summary>Until when reads and writes are counted, on the coarse clock. Nobody watching means
    /// nothing to count: an event tracer's provider writes nothing until a controller enables it,
    /// and an EventCounter is not even created until a listener asks. The difference here is that
    /// enabling costs one comparison rather than a session, because the clock this decides on is
    /// already being read for the activity stamp.</summary>
    private long _watchedUntilMs;

    /// <summary>How long one request keeps collection on. Long enough that closing a window and
    /// reopening it keeps its history, short enough that an instance nobody has looked at for a
    /// minute stops paying - which is the point of arming at all.</summary>
    internal static readonly TimeSpan WatchLease = TimeSpan.FromSeconds(60);
    /// <summary>Where the time comes from. A test drives it to place events in known slots; the
    /// process uses the monotonic high-resolution timer, a user-mode read.</summary>
    private readonly Func<long> _slot;

    // Which layer the last write on this thread touched. Consecutive reads usually belong to the
    // same layer, and this turns the shared lookup into three comparisons; the owner check keeps
    // two mounts in one process from reading each other's rings.
    [ThreadStatic] private static MetricsCollector? _lastOwner;
    [ThreadStatic] private static string? _lastLayer;
    [ThreadStatic] private static Ring? _lastRing;

    public MetricsCollector() : this(Slot) { }

    /// <summary>For tests: the caller decides which slot "now" is.</summary>
    internal MetricsCollector(Func<long> slot)
    {
        _slot = slot;
        _startedSlot = slot();
    }

    /// <summary>How long the mount has gone without a read or a write, so background work can
    /// stay out of the way of whoever is using it. Updated on every event, collected or not:
    /// a mount nobody is watching is not an idle mount.</summary>
    public TimeSpan IdleFor => TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - Volatile.Read(ref _lastActivityMs)));

    /// <summary>Whether anything is being counted right now.</summary>
    public bool Watched => Environment.TickCount64 < Volatile.Read(ref _watchedUntilMs);

    /// <summary>Asks this instance to start counting, and to keep counting for a while after the
    /// asker stops asking. The act of collecting is what turns it on - a metrics request, a
    /// recording starting - which is how the tools that do this well work: nothing is measured
    /// until somebody is there to read it.</summary>
    public void Watch() => Volatile.Write(ref _watchedUntilMs, Environment.TickCount64 + (long)WatchLease.TotalMilliseconds);

    public void RecordRead(string layer, TimeSpan elapsed, long bytes, bool cached = false)
    {
        Record(layer, elapsed, bytes, cached, false);
    }
    public void RecordWrite(string layer, TimeSpan elapsed, long bytes) => Record(layer, elapsed, bytes, false, true);

    private void Record(string layer, TimeSpan elapsed, long bytes, bool cached, bool write)
    {
        // The activity stamp moves whether or not anyone is watching: the warm-up reads IdleFor to
        // stay out of a busy mount's way, and an unobserved mount is not an idle one.
        long now = Environment.TickCount64;
        // A release store, not an exchange: this is a timestamp, and nobody needs the value that
        // was there. The exchange was a lock-prefixed read-modify-write on a line every reading
        // thread shares, which cost more than the rest of a write put together.
        Volatile.Write(ref _lastActivityMs, now);
        // One comparison, on a clock reading that was already needed. Everything below this line -
        // the slot, the layer lookup, the atomic adds - is what nobody watching does not pay.
        if (now >= Volatile.Read(ref _watchedUntilMs)) return;
        long slot = _slot();
        ref Cell cell = ref CellAt(layer, slot);
        if (write)
        {
            Interlocked.Increment(ref cell.Writes);
            if (bytes > 0) Interlocked.Add(ref cell.WriteBytes, bytes);
        }
        else
        {
            Interlocked.Increment(ref cell.Reads);
            if (cached) Interlocked.Increment(ref cell.Cached);
            if (bytes > 0) Interlocked.Add(ref cell.Bytes, bytes);
            long cost = (long)elapsed.TotalMicroseconds;
            if (cost > 0)
            {
                Interlocked.Add(ref cell.CostUs, cost);
                long seen;
                while (cost > (seen = Volatile.Read(ref cell.MaxCostUs)) &&
                       Interlocked.CompareExchange(ref cell.MaxCostUs, cost, seen) != seen)
                {
                }
            }
        }
    }

    /// <summary>The cell for this layer and slot, reset if the ring has come round to a slot it
    /// still holds data from. The stamp is published after the counters are cleared, and a reader
    /// takes the stamp before the counters, so a cell is never summed as the newest slot while it
    /// still holds the previous one's. A writer that arrives late - descheduled for longer than
    /// the ring is deep, or after a resume - counts into the cell as it stands rather than wiping
    /// what someone else collected: its read is misattributed by the delay, which is a smaller
    /// error than losing a cell's worth of them.</summary>
    private ref Cell CellAt(string layer, long slot)
    {
        Ring ring;
        if (ReferenceEquals(_lastOwner, this) && string.Equals(_lastLayer, layer, StringComparison.OrdinalIgnoreCase))
            ring = _lastRing!;
        else
        {
            ring = _layers.GetOrAdd(layer, static _ => new Ring());
            _lastOwner = this;
            _lastLayer = layer;
            _lastRing = ring;
        }
        ref Cell cell = ref ring.Cells[slot % CellCount];
        long stamped = Volatile.Read(ref cell.Slot);
        if (stamped < slot)
        {
            cell.Reads = 0;
            cell.Cached = 0;
            cell.Bytes = 0;
            cell.Writes = 0;
            cell.WriteBytes = 0;
            cell.CostUs = 0;
            cell.MaxCostUs = 0;
            Volatile.Write(ref cell.Slot, slot);
        }
        return ref cell;
    }

    /// <summary>How many slots one cell covers, in the units the clock counts in.</summary>
    private static readonly long TicksPerCell = Math.Max(1, Stopwatch.Frequency * CellMilliseconds / 1000);

    // The slot the last write on this thread landed in, and the tick it stops being current at.
    // Dividing the timestamp by the cell width is a sixty-four-bit division, which costs more than
    // everything else a write does put together; a clock that only moves forward lets a thread
    // answer "same slot as last time" with one comparison.
    [ThreadStatic] private static long _cachedSlot;
    [ThreadStatic] private static long _cachedSlotEnds;

    /// <summary>The slot the current moment falls in. The high-resolution timer is a user-mode
    /// read on every platform this runs on, and it is monotonic, so a slot means the same thing
    /// for the life of the process.</summary>
    private static long Slot()
    {
        long ticks = Stopwatch.GetTimestamp();
        if (ticks < _cachedSlotEnds) return _cachedSlot;
        long slot = ticks / TicksPerCell;
        _cachedSlot = slot;
        _cachedSlotEnds = (slot + 1) * TicksPerCell;
        return slot;
    }

    public MountMetrics Snapshot(string mountName, double windowSeconds)
    {
        if (windowSeconds <= 0) windowSeconds = 300;
        long now = _slot();
        // Whole cells at both ends, and never more than the ring holds: a window is answered from
        // the cells it covers, which is what is kept. What comes back is therefore a multiple of
        // the cell, and how much of it was actually measured is reported rather than assumed - a
        // process a second old has a second of data, not a minute, and a rate divided by a minute
        // it does not have would be a lie.
        long slots = Math.Min(Math.Max(1, (long)Math.Round(windowSeconds * 1000 / CellMilliseconds)), CellCount);
        // Never before the clock's own zero: a window wider than the ring is wide can reach past
        // the start of time as this process counts it, and a negative slot is not an index.
        long oldest = Math.Max(0, now - slots + 1);

        var totals = new Dictionary<string, Sums>(StringComparer.OrdinalIgnoreCase);
        long reads = 0, cached = 0, writes = 0, costUs = 0;
        foreach (var (name, ring) in _layers)
        {
            var cells = ring.Cells;
            // Only the cells the window covers, walked in slot order: a tenth-of-a-second window
            // touches ten of them, and a minute touches the whole ring. A cell that holds a newer
            // slot than the one being asked for is one the ring has already come back round to.
            int index = (int)(oldest % CellCount);
            for (long slot = oldest; slot <= now; slot++)
            {
                // The stamp first, then the counters: a cell being reused for a newer slot is
                // cleared before its stamp moves, so a stamp that matches is a cell to trust.
                if (Volatile.Read(ref cells[index].Slot) == slot)
                {
                    long cellReads = Volatile.Read(ref cells[index].Reads);
                    long cellCost = Volatile.Read(ref cells[index].CostUs);
                    totals.TryGetValue(name, out var sum);
                    totals[name] = new Sums(
                        sum.Reads + cellReads,
                        sum.Cached + Volatile.Read(ref cells[index].Cached),
                        sum.Bytes + Volatile.Read(ref cells[index].Bytes),
                        sum.Writes + Volatile.Read(ref cells[index].Writes),
                        sum.WriteBytes + Volatile.Read(ref cells[index].WriteBytes),
                        sum.CostUs + cellCost,
                        Math.Max(sum.MaxCostUs, Volatile.Read(ref cells[index].MaxCostUs)));
                    reads += cellReads;
                    cached += Volatile.Read(ref cells[index].Cached);
                    writes += Volatile.Read(ref cells[index].Writes);
                    costUs += cellCost;
                }
                if (++index == CellCount) index = 0;
            }
        }

        // What was watched, as opposed to what was asked for: the window, the ring's depth, and
        // how long this collector has existed, whichever is smallest. An idle mount has been
        // watched as long as the window says - its zeroes are measurements - while a process a
        // second old has watched a second, and dividing by a minute it never saw would understate
        // every rate by sixty.
        long watchedFrom = Math.Max(oldest, _startedSlot);
        var result = new MountMetrics
        {
            Mount = mountName,
            WindowSeconds = windowSeconds,
            MeasuredSeconds = Math.Max(0, now - watchedFrom + 1) * CellMilliseconds / 1000.0,
            TotalReads = reads,
            TotalWrites = writes,
            CacheHitPercent = reads == 0 ? 0 : 100.0 * cached / reads,
            AvgCostMs = reads == 0 ? 0 : costUs / 1000.0 / reads
        };
        long events = reads + writes;
        foreach (var (name, sum) in totals)
        {
            if (sum.Reads == 0 && sum.Writes == 0) continue;
            result.Layers.Add(new LayerMetric
            {
                Name = name,
                Hits = sum.Reads,
                Writes = sum.Writes,
                AccessSharePercent = events == 0 ? 0 : 100.0 * (sum.Reads + sum.Writes) / events,
                SharePercent = reads == 0 ? 0 : 100.0 * sum.Reads / reads,
                AvgCostMs = sum.Reads == 0 ? 0 : sum.CostUs / 1000.0 / sum.Reads,
                MaxCostMs = sum.MaxCostUs / 1000.0,
                Bytes = sum.Bytes,
                WriteBytes = sum.WriteBytes
            });
        }
        result.Layers = result.Layers.OrderByDescending(l => l.Hits).ToList();
        return result;
    }
}
