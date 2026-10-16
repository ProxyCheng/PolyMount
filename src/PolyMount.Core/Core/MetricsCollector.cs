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

    /// <summary>How finely the processes that used the mount are kept: a second, not the tenth of
    /// one the layers get. What reads this dimension is two pies whose subject is a process's share
    /// of a minute, so a second is resolution enough — and it has to be coarser, because a ring at
    /// the layers' granularity costs half a megabyte per key, which is affordable for the handful of
    /// layers a chain is configured with and not for a key space of whoever happens to be reading.
    /// The cells, the per-thread shards and the watch gate are the ones already here: only the
    /// stride differs.</summary>
    internal const int ProcessCellMilliseconds = 1000;

    /// <summary>Two minutes of them: the longest window anything asks for is the access window's
    /// sixty seconds, and a query may land between cells.</summary>
    internal const int ProcessCellCount = 120;

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
        /// <summary>Cost in nanoseconds, not microseconds: a read served from memory resolves in a
        /// fraction of one, and whole microseconds throw that away - the layer then reads as free
        /// while the time it took is real, and it lands in whatever account is left over.</summary>
        public long CostNs;
        public long MaxCostNs;
        /// <summary>Requests as the file system boundary saw them, and the time they took. Only the
        /// request ring uses these: a request belongs to no layer — that is the whole reason they
        /// are kept apart — and the layers it was served by are already counted beside it.</summary>
        public long Requests;
        /// <summary>Nanoseconds, like the layer cost beside it, and for the same reason.</summary>
        public long RequestNs;
    }

    private sealed class Ring
    {
        public readonly Cell[] Cells = new Cell[CellCount];
    }

    /// <summary>What one second holds for the processes that were using the mount in it. A
    /// dictionary rather than the fixed fields a layer's cell has, because processes are a key
    /// space nothing bounds: whoever happens to be reading is in it, and they come and go.</summary>
    private sealed class ProcessCell
    {
        /// <summary>Which second this cell holds, so a cell whose time has passed is recognised
        /// rather than summed; the writer that finds it stale resets it.</summary>
        public long Slot;
        public readonly Dictionary<int, ProcessCounts> Pids = new();
    }

    private sealed class ProcessRing
    {
        public readonly ProcessCell[] Cells = new ProcessCell[ProcessCellCount];
        public ProcessRing()
        {
            for (int i = 0; i < Cells.Length; i++) Cells[i] = new ProcessCell();
        }
    }

    /// <summary>What one process has done on one thread, in the second the thread is in. Nothing
    /// here is cleared by a reader: it is flushed into the ring's current second and cleared in
    /// place, the way a layer's counts are, so the objects are reused and a record allocates
    /// nothing.</summary>
    private sealed class ProcessCounts
    {
        public long Reads, ReadBytes, Writes, WriteBytes;
        public void Clear() => Reads = ReadBytes = Writes = WriteBytes = 0;
    }

    /// <summary>What one thread has counted in the cell it is in. A record is a plain add here —
    /// no atomic, no shared line — and the cells, which every thread shares, are written once per
    /// cell per thread instead of once per record.
    ///
    /// That is the whole difference between being watched and not: a counter every thread adds to
    /// is a cache line being handed around, and it is why libraries that count on hot paths stripe
    /// their counters (Java's LongAdder exists for this; .NET's own event counters are per-thread)
    /// instead of doing one atomic add per event. The same shape is already in this repository, in
    /// <see cref="RecordingCounters"/>: "plain arithmetic on purpose: a shard belongs to one
    /// thread, so there is nobody to be atomic against".
    ///
    /// A reader adds the shards that have not been flushed yet, so a count is visible the moment it
    /// happens rather than at the next cell boundary.</summary>
    private sealed class Shard
    {
        public long Slot = long.MinValue;
        public string Layer = "";
        public Counts? Current;
        public readonly Dictionary<string, Counts> Layers = new(StringComparer.OrdinalIgnoreCase);
        public readonly Counts Requests = new();
        /// <summary>What each process that this thread has been asked for has done, in the second
        /// this shard is in. Emptied into the process ring on the second, not the cell: the two
        /// dimensions have their own strides.</summary>
        public readonly Dictionary<int, ProcessCounts> Processes = new();
        public long ProcessSlot = long.MinValue;
        /// <summary>The process this thread last recorded, so the common case — one thread serving
        /// one reader — is a comparison rather than a dictionary lookup.</summary>
        public int LastProcessId = -1;
        public ProcessCounts? LastProcess;
    }

    /// <summary>One layer's running counts on one thread. A class rather than a struct: the hot
    /// path adds to it through a reference, and a struct in a dictionary would be copied in and
    /// out of it on every record.</summary>
    private sealed class Counts
    {
        public long Reads, Cached, Bytes, Writes, WriteBytes, CostNs, MaxNs;
        /// <summary>The request ring's two, used by the shard's own request counts.</summary>
        public long Requests, RequestNs;

        public void Clear()
        {
            Reads = Cached = Bytes = Writes = WriteBytes = CostNs = MaxNs = Requests = RequestNs = 0;
        }
    }

    /// <summary>What one layer's cells add up to over a window.</summary>
    private readonly record struct Sums(long Reads, long Cached, long Bytes, long Writes, long WriteBytes, long CostNs, long MaxCostNs);

    /// <summary>The slot this collector was created in: the ring cannot have watched time before
    /// it existed, and saying so is what keeps a rate off a window the process never covered.</summary>
    private readonly long _startedSlot;

    // One ring per layer. The cells are what a window is summed from; they are written once per
    // cell per thread, by that thread's shard, so the atomic adds are rare rather than per record.
    private readonly ConcurrentDictionary<string, Ring> _layers = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Every thread's shard, so a reader can add what has not been flushed yet. Guarded by
    /// its own lock: a thread registers once, and a reader copies it out and sums without holding
    /// it.</summary>
    private readonly List<Shard> _shards = new();
    private readonly object _shardGate = new();

    [ThreadStatic] private static MetricsCollector? _shardOwner;
    [ThreadStatic] private static Shard? _ownShard;
    /// <summary>Every request, and how long it took, whatever answered it. The per-layer rings can
    /// only charge a request to a layer that served it, so a lookup no layer answered for — which is
    /// most of what an engine probing for files it may not have produces — would otherwise be time
    /// nobody counted.</summary>
    private readonly Ring _requests = new();

    /// <summary>Who made the requests: one cell per second, each holding what every process that
    /// used the mount in that second did. One ring for all of them rather than one per process —
    /// the pids are the keys inside a cell, so the ring's size is the history's size and not
    /// something a busy day of short-lived tools can grow.</summary>
    private readonly ProcessRing _processes = new();
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

    /// <summary>One request, at the boundary that took it: whether a layer answered or not, and
    /// however many layers were asked on the way. Recorded beside the layer's own count rather than
    /// instead of it, so the two can be put next to each other later — the difference between them
    /// is the time nothing accounted for.</summary>
    public void RecordRequest(TimeSpan elapsed)
    {
        long now = Environment.TickCount64;
        Volatile.Write(ref _lastActivityMs, now);
        if (now >= Volatile.Read(ref _watchedUntilMs)) return;
        var counts = ShardFor(_slot()).Requests;
        counts.Requests++;
        counts.RequestNs += elapsed.Ticks * 100;
    }

    /// <summary>One process's request, at the boundary it came from, counted beside the layer that
    /// answered it. Who asked is a question the layers cannot answer: a layer's counts say what
    /// served the traffic, not which tool caused it.
    ///
    /// The id is the caller the file system saw, which WinFsp can only name while it is handling a
    /// Create, an Open or a Rename — the mount adapter keeps it on the handle for the callbacks that
    /// ask later (see <c>WinFspMount</c>), and passes 0 when there is nobody to name, which is what
    /// the service's own read-ahead is: work done for a guess rather than for a caller.
    ///
    /// <paramref name="bytes"/> is what actually moved, which only the read and the write callbacks
    /// know; the metadata calls pass nothing.</summary>
    public void RecordProcessAccess(int processId, bool write, long bytes = 0)
    {
        if (processId <= 0) return;
        long now = Environment.TickCount64;
        Volatile.Write(ref _lastActivityMs, now);
        if (now >= Volatile.Read(ref _watchedUntilMs)) return;
        var shard = ShardFor(_slot());
        ProcessCounts counts;
        if (shard.LastProcessId == processId && shard.LastProcess != null) counts = shard.LastProcess;
        else
        {
            if (!shard.Processes.TryGetValue(processId, out counts!))
                shard.Processes[processId] = counts = new ProcessCounts();
            shard.LastProcessId = processId;
            shard.LastProcess = counts;
        }
        if (write)
        {
            counts.Writes++;
            if (bytes > 0) counts.WriteBytes += bytes;
        }
        else
        {
            counts.Reads++;
            if (bytes > 0) counts.ReadBytes += bytes;
        }
    }

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
        Counts counts = LayerCounts(ShardFor(_slot()), layer);
        if (write)
        {
            counts.Writes++;
            if (bytes > 0) counts.WriteBytes += bytes;
        }
        else
        {
            counts.Reads++;
            if (cached) counts.Cached++;
            if (bytes > 0) counts.Bytes += bytes;
            // Ticks are hundreds of nanoseconds, so this is exact: no rounding, and nothing that
            // happened is dropped for being small.
            long cost = elapsed.Ticks * 100;
            if (cost > 0)
            {
                counts.CostNs += cost;
                if (cost > counts.MaxNs) counts.MaxNs = cost;
            }
        }
    }

    /// <summary>This thread's shard for the cell it is in, flushing the previous one if the cell
    /// has moved on. A thread that stops writing leaves its last shard unflushed on purpose: a
    /// reader adds it, and flushing it would need somebody to run at a moment nobody is there.</summary>
    private Shard ShardFor(long slot)
    {
        var shard = _ownShard;
        if (shard == null || !ReferenceEquals(_shardOwner, this))
        {
            // Both strides are stamped here: a shard that has just been made holds its first cell
            // and its first second, and a reader that found either unset would skip what it holds.
            shard = new Shard { Slot = slot, ProcessSlot = slot / ProcessCellsPerCell };
            _shardOwner = this;
            _ownShard = shard;
            lock (_shardGate) _shards.Add(shard);
            return shard;
        }
        if (shard.Slot != slot)
        {
            Flush(shard, shard.Slot);
            shard.Slot = slot;
        }
        // The processes have their own stride: a second, not the tenth of one the layers are kept
        // at, because that is the resolution the pies that read them are drawn at.
        long processSlot = slot / ProcessCellsPerCell;
        if (shard.ProcessSlot != processSlot)
        {
            FlushProcesses(shard, shard.ProcessSlot);
            shard.ProcessSlot = processSlot;
        }
        return shard;
    }

    /// <summary>How many of the ring's cells one process cell spans.</summary>
    private static readonly long ProcessCellsPerCell = Math.Max(1, ProcessCellMilliseconds / CellMilliseconds);

    /// <summary>The counts this thread is holding for the layer it last touched. Consecutive
    /// records usually belong to the same layer, so the common case is three comparisons; the
    /// dictionary is only reached when the layer changes.</summary>
    private static Counts LayerCounts(Shard shard, string layer)
    {
        // The layer name is the same string instance every time it is recorded - the file system
        // holds one per layer - so the common case is a reference check rather than a comparison.
        if (ReferenceEquals(shard.Current, null) || !(ReferenceEquals(shard.Layer, layer) ||
            string.Equals(shard.Layer, layer, StringComparison.OrdinalIgnoreCase)))
        {
            if (!shard.Layers.TryGetValue(layer, out var counts)) shard.Layers[layer] = counts = new Counts();
            shard.Layer = layer;
            shard.Current = counts;
        }
        return shard.Current;
    }

    /// <summary>Moves a shard's counts into the cells of the slot they belong to. This is the only
    /// place on the write path that is atomic, and it runs once per cell per thread — a hundred
    /// times a second rather than fifteen thousand. The counts are cleared in place rather than
    /// dropped, so the objects are reused and nothing is allocated here either.</summary>
    private void Flush(Shard shard, long slot)
    {
        if (slot == long.MinValue) return;
        foreach (var (layer, counts) in shard.Layers)
        {
            if (counts.Reads == 0 && counts.Cached == 0 && counts.Bytes == 0 && counts.Writes == 0 && counts.WriteBytes == 0 && counts.CostNs == 0)
                continue;
            ref Cell cell = ref CellAt(layer, slot);
            if (counts.Reads > 0) Interlocked.Add(ref cell.Reads, counts.Reads);
            if (counts.Cached > 0) Interlocked.Add(ref cell.Cached, counts.Cached);
            if (counts.Bytes > 0) Interlocked.Add(ref cell.Bytes, counts.Bytes);
            if (counts.Writes > 0) Interlocked.Add(ref cell.Writes, counts.Writes);
            if (counts.WriteBytes > 0) Interlocked.Add(ref cell.WriteBytes, counts.WriteBytes);
            if (counts.CostNs > 0) Interlocked.Add(ref cell.CostNs, counts.CostNs);
            if (counts.MaxNs > 0)
            {
                long seen;
                while (counts.MaxNs > (seen = Volatile.Read(ref cell.MaxCostNs)) &&
                       Interlocked.CompareExchange(ref cell.MaxCostNs, counts.MaxNs, seen) != seen)
                {
                }
            }
            counts.Clear();
        }
        if (shard.Requests.Requests > 0)
        {
            ref Cell cell = ref CellAt(_requests, slot);
            Interlocked.Add(ref cell.Requests, shard.Requests.Requests);
            Interlocked.Add(ref cell.RequestNs, shard.Requests.RequestNs);
            shard.Requests.Clear();
        }
    }

    /// <summary>Moves a shard's per-process counts into the cell of the second they belong to. The
    /// cells are shared by every thread, so the merge happens under the cell's own lock — which is
    /// paid once per process per second per thread rather than once per request, the same bargain
    /// the layer cells make with their atomics. The counts are cleared in place and the dictionary
    /// keeps its entries, so a thread serving the same few processes allocates nothing here either.
    /// </summary>
    private void FlushProcesses(Shard shard, long processSlot)
    {
        if (processSlot == long.MinValue || shard.Processes.Count == 0) return;
        var cell = _processes.Cells[processSlot % ProcessCellCount];
        lock (cell)
        {
            if (cell.Slot < processSlot)
            {
                cell.Pids.Clear();
                cell.Slot = processSlot;
            }
            foreach (var (processId, counts) in shard.Processes)
            {
                if (!cell.Pids.TryGetValue(processId, out var target)) cell.Pids[processId] = target = new ProcessCounts();
                target.Reads += counts.Reads;
                target.ReadBytes += counts.ReadBytes;
                target.Writes += counts.Writes;
                target.WriteBytes += counts.WriteBytes;
            }
        }
        foreach (var counts in shard.Processes.Values) counts.Clear();
    }

    /// <summary>What the threads are holding but have not flushed yet, as of now. Summed by the
    /// reader so a count is visible when it happens; the alternative - waiting for the next cell
    /// boundary - would make the first sample of a window short by up to a cell.</summary>
    private Shard[] PendingShards()
    {
        lock (_shardGate) return _shards.ToArray();
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
        return ref CellAt(ring, slot);
    }

    private static ref Cell CellAt(Ring ring, long slot)
    {
        ref Cell cell = ref ring.Cells[slot % CellCount];
        long stamped = Volatile.Read(ref cell.Slot);
        if (stamped < slot)
        {
            cell.Reads = 0;
            cell.Cached = 0;
            cell.Bytes = 0;
            cell.Writes = 0;
            cell.WriteBytes = 0;
            cell.CostNs = 0;
            cell.MaxCostNs = 0;
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

        long requests = 0, requestNs = 0;

        // What the threads are still holding. A count is visible when it happens rather than at the
        // next cell boundary - which keeps the first sample of a window honest, and keeps the two
        // counts of one request in the same window. A shard whose cell is older than the window
        // belongs to a thread that stopped writing before it, and to no window a reader asks about.
        //
        // A flush in flight at this instant can be counted twice - the cell it wrote and the shard
        // it has not cleared yet - which is one thread's counts for at most one cell. Losing them
        // instead would be just as wrong and no more useful, and neither moves a number anybody
        // reads at this resolution.
        {
            var cells = _requests.Cells;
            int index = (int)(oldest % CellCount);
            for (long slot = oldest; slot <= now; slot++)
            {
                if (Volatile.Read(ref cells[index].Slot) == slot)
                {
                    requests += Volatile.Read(ref cells[index].Requests);
                    requestNs += Volatile.Read(ref cells[index].RequestNs);
                }
                if (++index == CellCount) index = 0;
            }
        }

        var totals = new Dictionary<string, Sums>(StringComparer.OrdinalIgnoreCase);
        long reads = 0, cached = 0, writes = 0, costNs = 0;

        var shards = PendingShards();
        foreach (var shard in shards)
        {
            if (shard.Slot < oldest) continue;
            foreach (var (name, counts) in shard.Layers)
            {
                if (counts.Reads == 0 && counts.Writes == 0 && counts.Cached == 0 && counts.Bytes == 0 && counts.WriteBytes == 0) continue;
                totals.TryGetValue(name, out var held);
                totals[name] = new Sums(
                    held.Reads + counts.Reads,
                    held.Cached + counts.Cached,
                    held.Bytes + counts.Bytes,
                    held.Writes + counts.Writes,
                    held.WriteBytes + counts.WriteBytes,
                    held.CostNs + counts.CostNs,
                    Math.Max(held.MaxCostNs, counts.MaxNs));
                reads += counts.Reads;
                cached += counts.Cached;
                writes += counts.Writes;
                costNs += counts.CostNs;
            }
            requests += shard.Requests.Requests;
            requestNs += shard.Requests.RequestNs;
        }
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
                    long cellCost = Volatile.Read(ref cells[index].CostNs);
                    totals.TryGetValue(name, out var sum);
                    totals[name] = new Sums(
                        sum.Reads + cellReads,
                        sum.Cached + Volatile.Read(ref cells[index].Cached),
                        sum.Bytes + Volatile.Read(ref cells[index].Bytes),
                        sum.Writes + Volatile.Read(ref cells[index].Writes),
                        sum.WriteBytes + Volatile.Read(ref cells[index].WriteBytes),
                        sum.CostNs + cellCost,
                        Math.Max(sum.MaxCostNs, Volatile.Read(ref cells[index].MaxCostNs)));
                    reads += cellReads;
                    cached += Volatile.Read(ref cells[index].Cached);
                    writes += Volatile.Read(ref cells[index].Writes);
                    costNs += cellCost;
                }
                if (++index == CellCount) index = 0;
            }
        }

        // Who asked, over the same window and at the second the process cells are kept at. A cell
        // the ring has come back round to holds a newer second and is not summed, and what the
        // threads are still holding is added the way the layers' counts are, so the newest second
        // of a window is not short.
        long nowProcess = now / ProcessCellsPerCell;
        long processSlots = Math.Min(Math.Max(1, (long)Math.Round(windowSeconds * 1000 / ProcessCellMilliseconds)), ProcessCellCount);
        long oldestProcess = Math.Max(0, nowProcess - processSlots + 1);
        var callersById = new Dictionary<int, ProcessCounts>();
        void AddCaller(int processId, long callerReads, long callerReadBytes, long callerWrites, long callerWriteBytes)
        {
            if (!callersById.TryGetValue(processId, out var sum)) callersById[processId] = sum = new ProcessCounts();
            sum.Reads += callerReads;
            sum.ReadBytes += callerReadBytes;
            sum.Writes += callerWrites;
            sum.WriteBytes += callerWriteBytes;
        }
        for (long slot = oldestProcess; slot <= nowProcess; slot++)
        {
            var cell = _processes.Cells[slot % ProcessCellCount];
            lock (cell)
            {
                if (cell.Slot != slot) continue;
                foreach (var (processId, counts) in cell.Pids)
                    AddCaller(processId, counts.Reads, counts.ReadBytes, counts.Writes, counts.WriteBytes);
            }
        }
        foreach (var shard in shards)
        {
            if (shard.ProcessSlot < oldestProcess) continue;
            foreach (var (processId, counts) in shard.Processes)
                AddCaller(processId, counts.Reads, counts.ReadBytes, counts.Writes, counts.WriteBytes);
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
            RequestCount = requests,
            RequestMs = requestNs / 1_000_000.0,
            MeasuredSeconds = Math.Max(0, now - watchedFrom + 1) * CellMilliseconds / 1000.0,
            TotalReads = reads,
            TotalWrites = writes,
            CacheHitPercent = reads == 0 ? 0 : 100.0 * cached / reads,
            AvgCostMs = reads == 0 ? 0 : costNs / 1_000_000.0 / reads
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
                AvgCostMs = sum.Reads == 0 ? 0 : sum.CostNs / 1_000_000.0 / sum.Reads,
                MaxCostMs = sum.MaxCostNs / 1_000_000.0,
                Bytes = sum.Bytes,
                WriteBytes = sum.WriteBytes
            });
        }
        result.Layers = result.Layers.OrderByDescending(l => l.Hits).ToList();

        // Who asked, busiest first, with the tail folded — a window is a minute wide and a busy
        // mount sees more processes than a circle can draw. Names are resolved here and nowhere
        // else: the request path records an id and nothing more, because reading a process' name
        // opens it, and that is not a thing to do per request.
        var callers = new List<ProcessMetric>();
        foreach (var (processId, counts) in callersById)
        {
            if (counts.Reads == 0 && counts.Writes == 0) continue;
            string? name = ProcessNames.Of(processId);
            callers.Add(new ProcessMetric
            {
                Pid = processId,
                Name = name ?? "",
                // The same palette the layers use, keyed on the application rather than the id so a
                // process keeps one colour across restarts — and prefixed, because a process that
                // happens to be called "cache" would otherwise be handed the cache layer's colour.
                Color = LayerColors.Auto(name == null ? "app:pid:" + processId : "app:" + name.ToUpperInvariant()),
                Reads = counts.Reads,
                Writes = counts.Writes,
                ReadBytes = counts.ReadBytes,
                WriteBytes = counts.WriteBytes
            });
        }
        result.Processes = ProcessFold.Top(callers);
        foreach (var row in result.Processes) if (row.Other) row.Color = "#94A3B8";
        return result;
    }
}
