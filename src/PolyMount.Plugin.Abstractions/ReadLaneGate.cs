using System.Diagnostics;
using PolyMount.Plugins.Ipc;

namespace PolyMount.Plugins;

/// <summary>A source layer that serves reads at more than one priority, and what that cost and
/// saved. The mount reports these levels for it; a layer that serves one priority has nothing
/// to say here.</summary>
public interface IReadLaneSource
{
    ReadLaneGate Lanes { get; }
}

/// <summary>One priority class's share of a source: what is kept for it whatever else is
/// happening, and the most it may ever hold. Both are counts of requests at the source at once.
/// </summary>
public readonly record struct ReadLevel(int Floor, int Ceiling);

/// <summary>
/// Admission control for one source, class by class.
/// <para>
/// The source serves a fixed number of requests at once, and the classes share it by
/// reservation rather than by comparison. Each class is <em>guaranteed</em> its floor: what is
/// kept for a class is subtracted from what every other class may take, so a class can be busy
/// without that ever being the reason a reader queues. Above its floor a class may use spare
/// capacity up to its ceiling — spare meaning room no other class's floor is sitting on, and
/// taken only while nothing more urgent is waiting. A class at its floor keeps its floor while
/// more urgent work queues; it does not add beyond it.
/// </para>
/// <para>
/// So the reader's own class is guaranteed nearly the whole source, and read-ahead's class has a
/// floor of its own — one request, so prediction always makes progress — with a small ceiling.
/// Read-ahead's ceiling is a constant rather than whatever the reader leaves over, because what
/// is left over is exactly what disappears when the reader is busiest.
/// </para>
/// <para>
/// A request is published before it is admitted, so that callers who want the same thing can
/// join it rather than ask the source twice. That is what makes promotion necessary: a reader
/// that finds read-ahead waiting for its own path would otherwise be queued behind the class it
/// just joined. It calls <see cref="Admission.Promote"/>, which moves that request to the
/// reader's class, lets it issue immediately, and counts it — the guess the reader then
/// confirmed by asking. The reader's read happens once either way, so this adds no load; it
/// replaces the request the reader would have issued itself.
/// </para>
/// </summary>
public sealed class ReadLaneGate
{
    private const int MaxLevels = 16;
    private const int MaxCapacity = 256;

    private readonly int _capacity;
    private readonly ReadLevel[] _levels;
    private readonly int[] _inFlight;
    private readonly LinkedList<Admission>[] _queues;
    private readonly long[] _requests, _promotions, _waitTicks;
    private int _total;
    private readonly object _gate = new();

    public ReadLaneGate(int capacity, params ReadLevel[] levels)
    {
        if (capacity is < 1 or > MaxCapacity)
            throw new ArgumentOutOfRangeException(nameof(capacity), $"A source serves 1-{MaxCapacity} requests at once");
        if (levels.Length is < 1 or > MaxLevels)
            throw new ArgumentOutOfRangeException(nameof(levels), $"A gate serves 1-{MaxLevels} priority classes");
        int reserved = 0;
        foreach (var level in levels)
        {
            if (level.Floor < 1 || level.Ceiling < level.Floor || level.Ceiling > capacity)
                throw new ArgumentOutOfRangeException(nameof(levels),
                    $"A class's share must be 1 <= floor <= ceiling <= {capacity}");
            reserved += level.Floor;
        }
        // Every class's floor is kept for it out of the same source, so the floors have to fit.
        if (reserved > capacity)
            throw new ArgumentOutOfRangeException(nameof(levels),
                $"The classes' floors total {reserved}, which is more than the source's {capacity}");
        _capacity = capacity;
        _levels = levels;
        _inFlight = new int[levels.Length];
        _queues = new LinkedList<Admission>[levels.Length];
        _requests = new long[levels.Length];
        _promotions = new long[levels.Length];
        _waitTicks = new long[levels.Length];
        for (int level = 0; level < levels.Length; level++) _queues[level] = new LinkedList<Admission>();
    }

    /// <summary>Requests the source serves at once, across every class.</summary>
    public int Capacity => _capacity;
    public int Levels => _levels.Length;

    /// <summary>Reserves a class for one request that other callers may join. Nothing is held
    /// until <see cref="Admission.Admit"/>, which is the point at which a more urgent read can
    /// still promote the request out of its queue.</summary>
    public Admission Begin(int priority) => new(this, priority);

    /// <summary>Admits one request that nobody can join, for exactly the length of the call.</summary>
    public IDisposable Enter(int priority) => Begin(priority).Admit();

    /// <summary>Every class's share of the source, for whoever reports on it.</summary>
    public List<ReadLaneStat> Snapshot()
    {
        var stats = new List<ReadLaneStat>(_levels.Length);
        lock (_gate)
            for (int level = 0; level < _levels.Length; level++)
                stats.Add(new ReadLaneStat
                {
                    Priority = level,
                    Floor = _levels[level].Floor,
                    Ceiling = _levels[level].Ceiling,
                    Requests = Interlocked.Read(ref _requests[level]),
                    InFlight = _inFlight[level],
                    Queued = _queues[level].Count,
                    WaitMs = TimeSpan.FromTicks(Interlocked.Read(ref _waitTicks[level])).TotalMilliseconds,
                    Promotions = Interlocked.Read(ref _promotions[level])
                });
        return stats;
    }

    /// <summary>One source request, and the class it was admitted in.</summary>
    public sealed class Admission : IDisposable
    {
        private readonly ReadLaneGate _owner;
        private readonly int _priority;
        private LinkedListNode<Admission>? _node;
        private bool _promoted, _waiting;
        /// <summary>The class holding this request's slot, or -1 while it holds none.</summary>
        private int _held;
        private bool _released;

        internal Admission(ReadLaneGate owner, int priority) { _owner = owner; _priority = priority; _held = -1; }

        public int Priority => _priority;

        /// <summary>Waits for this request's turn to be at the source, and returns the permit
        /// that gives the class back.</summary>
        public IDisposable Admit()
        {
            Interlocked.Increment(ref _owner._requests[_priority]);
            long start = Stopwatch.GetTimestamp();
            lock (_owner._gate)
            {
                // A request a reader has claimed runs in the reader's class, already admitted.
                if (_promoted) { }
                else if (_owner.Admits(_priority))
                {
                    _owner._inFlight[_priority]++;
                    _owner._total++;
                    _held = _priority;
                }
                else
                {
                    _waiting = true;
                    _node = _owner._queues[_priority].AddLast(this);
                    while (_waiting) Monitor.Wait(_owner._gate);
                }
            }
            Interlocked.Add(ref _owner._waitTicks[_priority], Stopwatch.GetElapsedTime(start).Ticks);
            return this;
        }

        /// <summary>A read more urgent than this request has joined it rather than issuing its
        /// own. It becomes that read's request: it moves to the claiming class, is admitted
        /// there at once, and is counted as a claim — read-ahead the reader turned out to want.
        /// A read of the same or lower priority is not a claim and does nothing.</summary>
        public void Promote(int priority)
        {
            if (priority >= _priority) return;
            lock (_owner._gate)
            {
                if (_released || _promoted) return;
                _promoted = true;
                Interlocked.Increment(ref _owner._promotions[_priority]);
                bool queued = _waiting;
                _waiting = false;
                if (_node != null) { _owner._queues[_priority].Remove(_node); _node = null; }
                // The reader's read happens once either way: this moves one request between
                // classes rather than adding one, so the source sees what it would have.
                if (_held >= 0) { _owner._inFlight[_held]--; _owner._total--; }
                _owner._inFlight[priority]++;
                _owner._total++;
                _held = priority;
                if (queued) Monitor.PulseAll(_owner._gate);
                // A request that had reached the source vacates its class, so whatever was
                // waiting behind that class gets its chance rather than waiting for a
                // completion that is no longer coming.
                else _owner.Dispatch();
            }
        }

        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_released) return;
                _released = true;
                if (_held < 0) return;
                _owner._inFlight[_held]--;
                _owner._total--;
                _held = -1;
                _owner.Dispatch();
            }
        }

        /// <summary>Takes the slot `Dispatch` just handed over, so a class stays as busy as it is
        /// allowed without ever running more than its share at once.</summary>
        internal void Granted()
        {
            _node = null;
            _waiting = false;
            _held = _priority;
        }
    }

    /// <summary>Whether a request of <paramref name="priority"/> may start now: inside its own
    /// guarantee, or on capacity that is neither reserved for another class nor wanted by a more
    /// urgent one.</summary>
    private bool Admits(int priority)
    {
        if (_inFlight[priority] >= _levels[priority].Ceiling) return false;
        if (_inFlight[priority] >= _levels[priority].Floor)
            for (int above = 0; above < priority; above++)
                if (_queues[above].Count > 0) return false;
        return _total + ReservedExcept(priority) < _capacity;
    }

    /// <summary>What the other classes' floors are still owed — the capacity a class may not
    /// take, because it is being kept for someone else.</summary>
    private int ReservedExcept(int priority)
    {
        int reserved = 0;
        for (int other = 0; other < _levels.Length; other++)
            if (other != priority) reserved += Math.Max(0, _levels[other].Floor - _inFlight[other]);
        return reserved;
    }

    /// <summary>Hands freed capacity to whoever can use it, most urgent class first.</summary>
    private void Dispatch()
    {
        for (int priority = 0; priority < _levels.Length; priority++)
            while (_queues[priority].Count > 0 && Admits(priority))
            {
                var next = _queues[priority].First!.Value;
                _queues[priority].RemoveFirst();
                _inFlight[priority]++;
                _total++;
                next.Granted();
                Monitor.PulseAll(_gate);
            }
    }
}
