using System.Collections.Concurrent;
using PolyMount.Core.Ipc;

namespace PolyMount.Core;

/// <summary>
/// Counts what an instance is doing instead of writing a line about each thing it does, so that a
/// recording costs the request thread almost nothing — it is the instrument, and an instrument that
/// slows what it measures is worth less than no instrument at all.
///
/// Two properties make it cheap. Each thread counts into a <see cref="Shard"/> of its own, so
/// nothing on the request path is shared: no interlocked read-modify-write on a cache line thirty
/// cores are fighting over, just a dictionary lookup and an increment. And a path is tallied only
/// for requests where a path could matter — below <see cref="PathFloorMs"/> a request is too small
/// for its path to be the reason for anything, and hashing a long path per call cost more than the
/// counting around it.
///
/// The counts are exact: nothing here can be dropped by a queue, because there is no queue.
/// </summary>
public sealed class RecordingCounters
{
    /// <summary>The size at which a request is worth remembering the path of. What this has to
    /// skip is the storm of sub-microsecond metadata probes, whose paths would cost more to hash
    /// than they say; half a millisecond skipped those and every ordinary file open with them, so a
    /// recording of a light workload reported no paths at all. Everything below it is still counted
    /// by operation, which is where a large number of small calls shows up.</summary>
    public const double PathFloorMs = 0.05;

    /// <summary>How many distinct (operation, path) pairs one thread tallies before pooling the
    /// rest. Pooled rather than forgotten: the totals stay exact even when the ranking cannot list
    /// everything that made them up.</summary>
    private const int PathsPerShard = 4000;
    /// <summary>Time is accumulated in nanoseconds. Microseconds looked fine until a real
    /// recording reported 197 ms of work for 2.2 million requests: the whole reason these counts
    /// exist is the requests too small to be worth a line, and those are fractions of a
    /// microsecond each, so rounding them away emptied the total.</summary>
    private const double NanosPerMs = 1_000_000;

    private sealed class Tally
    {
        public string Operation = "", Path = "";
        public long Count, Over100, TotalNanos, MaxNanos;

        /// <summary>Adds one observation. Plain arithmetic on purpose: a shard belongs to one
        /// thread, so there is nobody to be atomic against.</summary>
        public void Add(double elapsedMs, bool over100)
        {
            long nanos = (long)Math.Round(elapsedMs * NanosPerMs);
            Count++;
            if (over100) Over100++;
            TotalNanos += nanos;
            if (nanos > MaxNanos) MaxNanos = nanos;
        }
    }

    /// <summary>One thread's counts. Read only by that thread while a recording runs, and merged
    /// once when it stops.</summary>
    private sealed class Shard
    {
        public readonly Dictionary<string, Tally> Operations = new(StringComparer.Ordinal);
        public readonly Dictionary<(string Operation, string Path), Tally> Paths = new();
        public readonly Tally Pooled = new();
        public long PooledPaths;
        public long Events;
    }

    /// <summary>Created on first use by whichever thread is counting, and enumerated at the end:
    /// <c>trackAllValues</c> is what makes the second part possible.</summary>
    private readonly ThreadLocal<Shard> _shard = new(() => new Shard(), trackAllValues: true);

    /// <summary>Records one finished request.</summary>
    public void Count(string operation, string path, double elapsedMs)
    {
        var shard = _shard.Value!;
        shard.Events++;
        if (!shard.Operations.TryGetValue(operation, out var tally))
            shard.Operations[operation] = tally = new Tally { Operation = operation };
        tally.Add(elapsedMs, elapsedMs >= 100);
        if (elapsedMs < PathFloorMs) return;
        if (shard.Paths.TryGetValue((operation, path), out var pathTally)) { pathTally.Add(elapsedMs, elapsedMs >= 100); return; }
        if (shard.Paths.Count < PathsPerShard)
        {
            shard.Paths[(operation, path)] = new Tally { Operation = operation, Path = path };
            shard.Paths[(operation, path)].Add(elapsedMs, elapsedMs >= 100);
            return;
        }
        shard.Pooled.Add(elapsedMs, elapsedMs >= 100);
        shard.PooledPaths++;
    }

    /// <summary>The counts, merged, ranked, with everything past the ranking rolled up into one
    /// figure so a reader can see how much of the window the list does not account for.</summary>
    public RecordingCounts Snapshot(int topPaths = 40)
    {
        var operations = new Dictionary<string, OperationCount>(StringComparer.Ordinal);
        var paths = new Dictionary<(string Operation, string Path), PathCount>();
        long pooledEvents = 0, pooledPaths = 0;
        double pooledMs = 0;
        foreach (var shard in _shard.Values)
        {
            foreach (var tally in shard.Operations.Values)
            {
                if (!operations.TryGetValue(tally.Operation, out var entry))
                    operations[tally.Operation] = entry = new OperationCount { Operation = tally.Operation };
                entry.Count += tally.Count;
                entry.Over100 += tally.Over100;
                entry.TotalMs += tally.TotalNanos / NanosPerMs;
                entry.MaxMs = Math.Max(entry.MaxMs, tally.MaxNanos / NanosPerMs);
            }
            foreach (var tally in shard.Paths.Values)
            {
                var key = (tally.Operation, tally.Path);
                if (!paths.TryGetValue(key, out var entry))
                    paths[key] = entry = new PathCount { Operation = tally.Operation, Path = tally.Path };
                entry.Count += tally.Count;
                entry.TotalMs += tally.TotalNanos / NanosPerMs;
                entry.MaxMs = Math.Max(entry.MaxMs, tally.MaxNanos / NanosPerMs);
            }
            pooledEvents += shard.Pooled.Count;
            pooledPaths += shard.PooledPaths;
            pooledMs += shard.Pooled.TotalNanos / NanosPerMs;
        }

        var rankedOperations = operations.Values.OrderByDescending(entry => entry.TotalMs).ToList();
        var rankedPaths = paths.Values.OrderByDescending(entry => entry.TotalMs).ToList();
        var listed = rankedPaths.Take(topPaths).ToList();
        var rest = rankedPaths.Skip(topPaths).ToList();
        return new RecordingCounts
        {
            Events = rankedOperations.Sum(entry => entry.Count),
            Over100 = rankedOperations.Sum(entry => entry.Over100),
            TotalMs = rankedOperations.Sum(entry => entry.TotalMs),
            MaxMs = rankedOperations.Count == 0 ? 0 : rankedOperations.Max(entry => entry.MaxMs),
            Operations = rankedOperations,
            Paths = listed,
            PathsUnlisted = rest.Count + pooledPaths,
            EventsUnlisted = rest.Sum(entry => entry.Count) + pooledEvents,
            MsUnlisted = rest.Sum(entry => entry.TotalMs) + pooledMs
        };
    }
}
