using System.Text.Json;

namespace PolyMount.Core;

public sealed class ReadPredictionOptions
{
    public bool Enabled { get; set; }
    public int MaxTransitions { get; set; } = 4096;
    public long MaxFileBytes { get; set; } = 8L * 1024 * 1024;
    /// <summary>Quiet period required before preloading starts when nothing is in flight.
    /// Zero — the default — means learning and predicting run continuously, bounded only by
    /// the in-flight limit; raise it to keep read-ahead out of the way of a busy reader.</summary>
    public int IdleDelayMs { get; set; }
    /// <summary>How many demands may be in flight while preloading still runs. The loader
    /// spends most of its time inside a demand, so a low value leaves it no window.</summary>
    public int MaxActiveDemandForPreload { get; set; } = 1;
    /// <summary>How often the gate is evaluated. Too coarse relative to a read and the
    /// window never lands inside one.</summary>
    public int TickIntervalMs { get; set; } = 100;
    public int MaxInFlight { get; set; } = 1;
    /// <summary>How many distinct successors one path may accumulate. Without this a single
    /// divergence would replace the evidence for the previous one; with it the winner is
    /// decided by how often it was seen rather than by which was seen last.</summary>
    public int MaxSuccessorsPerKey { get; set; } = 4;
    /// <summary>Share of a path's observations the leading successor must hold before it is
    /// read ahead. Read-ahead spends real source bandwidth, so a coin flip is not worth it.</summary>
    public double MinConfidence { get; set; } = 0.6;
    /// <summary>How long an unobserved transition is remembered. Without this a sequence the
    /// loader stopped using keeps being predicted, and the stale guess propagates forward.
    /// Zero keeps transitions until the capacity bound recycles them.</summary>
    public int RetentionMs { get; set; } = 3600000;
}

/// <summary>
/// Recommended pacing tiers. Preloading cannot reduce the number of source reads, only
/// move them earlier so they overlap the reader; how much of that is possible is a
/// trade-off against competing with the foreground load for the source.
/// </summary>
public static class ReadPredictionProfiles
{
    public const string Idle = "idle";
    public const string Steady = "steady";
    public const string Eager = "eager";
    public const string Custom = "custom";
    public static IReadOnlyList<string> Names { get; } = [Idle, Steady, Eager, Custom];

    /// <summary>Today's behaviour: one speculative load, only in a quiet gap.</summary>
    public static ReadPredictionOptions IdleOptions() => new()
    { Enabled = true, TickIntervalMs = 50, IdleDelayMs = 0, MaxActiveDemandForPreload = 2, MaxInFlight = 1, MaxTransitions = 8192 };

    /// <summary>Preloads while a few reads are in flight, so a loader that pauses between
    /// resources still gets files ahead of asking for them.</summary>
    public static ReadPredictionOptions SteadyOptions() => new()
    { Enabled = true, TickIntervalMs = 25, IdleDelayMs = 0, MaxActiveDemandForPreload = 8, MaxInFlight = 2, MaxTransitions = 32768 };

    /// <summary>Keeps up with a continuously busy loader. Competes with it for the source,
    /// which helps when the source is latency-bound and hurts when it is bandwidth-bound.</summary>
    public static ReadPredictionOptions EagerOptions() => new()
    { Enabled = true, TickIntervalMs = 10, IdleDelayMs = 0, MaxActiveDemandForPreload = 32, MaxInFlight = 4, MaxTransitions = 131072 };

    public static ReadPredictionOptions Resolve(string? profile, Func<ReadPredictionOptions> custom) => profile switch
    {
        Steady => SteadyOptions(),
        Eager => EagerOptions(),
        Custom => custom(),
        _ => IdleOptions()
    };
}

/// <summary>Bounded next-file predictor. One speculative load, only after demand becomes idle.</summary>
public sealed class ReadPrediction : IDisposable
{
    /// <summary>One observed successor of a path, with the evidence behind it.</summary>
    private sealed record Transition(string To, int Hits, long LastSeen);
    private const int MaxHits = 10000;
    private readonly object _gate = new();
    // Path -> its ranked successors. Bounded on both axes: keys by MaxTransitions,
    // successors per key by MaxSuccessorsPerKey.
    private readonly Dictionary<string, List<Transition>> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly VirtualFileSystem _vfs;
    private readonly ReadPredictionOptions _options;
    private readonly string _modelPath;
    private readonly Action<string>? _preload;
    private readonly Timer _timer;
    private readonly CancellationTokenSource _stop = new();
    private string? _previous;
    private readonly Queue<string> _candidates = new();
    private long _lastDemand;
    private int _activeDemand;
    private readonly List<Task> _inFlight = new();
    private long _epoch;
    private const int SweepInterval = 50;
    private int _sweepCounter;
    /// <summary>Distinct observed transitions, across every path's successor set.</summary>
    public int LearnedTransitions { get { lock (_gate) return _nodes.Sum(p => p.Value.Count); } }
    public long Predictions { get; private set; }
    public long Completed { get; private set; }

    public ReadPrediction(VirtualFileSystem vfs, ReadPredictionOptions options, string modelPath, Action<string>? preload = null)
    {
        if (options.MaxTransitions is < 1 or > 1048576 || options.MaxFileBytes is <= 0 or > 256L * 1024 * 1024 ||
            options.IdleDelayMs is < 0 or > 60000 || options.TickIntervalMs is < 10 or > 10000 ||
            options.MaxInFlight is < 1 or > 64 || options.MaxActiveDemandForPreload is < 0 or > 1024 ||
            options.MaxSuccessorsPerKey is < 1 or > 16 || options.MinConfidence is < 0 or > 1 ||
            options.RetentionMs is < 0 or > 7 * 24 * 3600000)
            throw new ArgumentException("Invalid read prediction budgets");
        _vfs = vfs; _options = options; _modelPath = modelPath; _preload = preload;
        try
        {
            if (File.Exists(modelPath) && new FileInfo(modelPath).Length <= 8 * 1024 * 1024)
            {
                long horizon = DateTime.UtcNow.Ticks - TimeSpan.FromMilliseconds(options.RetentionMs).Ticks;
                // A file from an older model shape fails to deserialize and is dropped, as is
                // one whose transitions have all expired: an old model is not resumed.
                foreach (var node in JsonSerializer.Deserialize<Dictionary<string, List<Transition>>>(File.ReadAllText(modelPath)) ?? [])
                {
                    if (_nodes.Count >= options.MaxTransitions) break;
                    var kept = node.Value
                        .Where(t => options.RetentionMs <= 0 || t.LastSeen >= horizon)
                        .OrderByDescending(t => t.Hits)
                        .Take(options.MaxSuccessorsPerKey)
                        .ToList();
                    if (kept.Count > 0) _nodes[node.Key] = kept;
                }
            }
        }
        catch (IOException) { }
        catch (JsonException) { }
        using (ExecutionContext.SuppressFlow())
            _timer = new Timer(_ => Tick(), null, options.TickIntervalMs, options.TickIntervalMs);
    }
    public IDisposable Demand(string path)
    {
        lock (_gate)
        {
            _activeDemand++; _lastDemand = Environment.TickCount64;
            if (path.Length > 4096) return new DemandScope(this);
            if (_previous != null && !string.Equals(_previous, path, StringComparison.OrdinalIgnoreCase))
                Observe(_previous, path);
            _previous = path;
            // Nominate the path itself; what to fetch is resolved when read-ahead runs.
            // Deferring that decision means a nomination cannot act on evidence that has
            // since been contradicted. Keep a small queue so a later unrelated access does
            // not erase earlier nominations.
            if (_nodes.ContainsKey(path) && !_candidates.Contains(path))
            {
                if (_candidates.Count >= 16) _candidates.Dequeue();
                _candidates.Enqueue(path);
            }
        }
        return new DemandScope(this);
    }
    private sealed class DemandScope(ReadPrediction owner) : IDisposable
    {
        public void Dispose() { lock (owner._gate) { owner._activeDemand--; owner._lastDemand = Environment.TickCount64; } }
    }
    /// <summary>Records that <paramref name="to"/> followed <paramref name="from"/>, ranking it
    /// against the path's other successors rather than replacing them.</summary>
    private void Observe(string from, string to)
    {
        if (!_nodes.TryGetValue(from, out var successors))
        {
            if (_nodes.Count >= _options.MaxTransitions)
                _nodes.Remove(_nodes.MinBy(p => p.Value.Sum(t => t.Hits)).Key);
            _nodes[from] = successors = new();
        }
        int index = successors.FindIndex(t => t.To.Equals(to, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            successors[index] = successors[index] with { Hits = Math.Min(MaxHits, successors[index].Hits + 1), LastSeen = DateTime.UtcNow.Ticks };
        else
        {
            successors.Add(new(to, 1, DateTime.UtcNow.Ticks));
            if (successors.Count > _options.MaxSuccessorsPerKey)
                successors.Remove(successors.MinBy(t => t.Hits)!);
        }
    }

    /// <summary>The successor of <paramref name="path"/> if it holds enough of that path's
    /// observations to be worth the bandwidth — SPP's path confidence, applied per step.</summary>
    private string? ConfidentSuccessor(string path)
    {
        if (!_nodes.TryGetValue(path, out var successors) || successors.Count == 0) return null;
        int total = successors.Sum(t => t.Hits);
        if (total <= 0) return null;
        var top = successors.MaxBy(t => t.Hits)!;
        return (double)top.Hits / total >= _options.MinConfidence ? top.To : null;
    }

    /// <summary>Drops transitions that have not been observed for the retention window, so a
    /// pattern the loader has moved on from stops being predicted.</summary>
    private void ForgetStale()
    {
        if (_options.RetentionMs <= 0 || ++_sweepCounter < SweepInterval) return;
        _sweepCounter = 0;
        long horizon = DateTime.UtcNow.Ticks - TimeSpan.FromMilliseconds(_options.RetentionMs).Ticks;
        foreach (var key in _nodes.Keys.ToArray())
        {
            var transitions = _nodes[key];
            if (transitions.All(t => t.LastSeen >= horizon)) continue;
            var kept = transitions.Where(t => t.LastSeen >= horizon).ToList();
            if (kept.Count == 0) _nodes.Remove(key);
            else _nodes[key] = kept;
        }
    }

    private void Tick()
    {
        lock (_gate)
        {
            ForgetStale();
            _inFlight.RemoveAll(task => task.IsCompleted);
            if (_stop.IsCancellationRequested || _activeDemand > _options.MaxActiveDemandForPreload || _candidates.Count == 0 ||
                _inFlight.Count >= _options.MaxInFlight ||
                (_activeDemand == 0 && Environment.TickCount64 - _lastDemand < _options.IdleDelayMs)) return;
            if (ConfidentSuccessor(_candidates.Dequeue()) is not { } candidate) return;
            Predictions++;
            long epoch = _epoch;
            using (ExecutionContext.SuppressFlow()) _inFlight.Add(Task.Run(() =>
            {
                try
                {
                    if (_stop.IsCancellationRequested || !_vfs.TryLookup(candidate, out var entry) ||
                        (!entry.IsDir && entry.Size > _options.MaxFileBytes) || (entry.IsDir && _preload == null)) return;
                    if (_preload == null && _vfs.Layers.OfType<ICacheLayer>().Any(c => c.TryGetEntry(candidate, out _))) return;
                    lock (_gate) if (_activeDemand > _options.MaxActiveDemandForPreload || _stop.IsCancellationRequested || epoch != _epoch) return;
                    using var trace = RequestDiagnostics.Begin("Predict.Preload", candidate, _vfs.Diagnostics);
                    if (_preload != null) _preload(candidate);
                    else { using var stream = _vfs.OpenRead(candidate, out _, speculative: true); }
                    lock (_gate) if (epoch == _epoch) Completed++;
                }
                catch (IOException) { }
                catch (Exception ex) { using var trace = RequestDiagnostics.Begin("Predict.Failed", candidate, _vfs.Diagnostics); trace?.Stage(ex.GetType().Name); }
            }));
        }
    }
    public void Reset()
    {
        lock (_gate)
        {
            // Do not reset live demand counts or abort an already-started I/O.
            // Queued candidates from the old model cannot begin after this reset.
            _epoch++;
            _nodes.Clear(); _previous = null; _candidates.Clear();
            Predictions = 0; Completed = 0;
            Directory.CreateDirectory(Path.GetDirectoryName(_modelPath)!);
            string temp = _modelPath + ".tmp";
            File.WriteAllText(temp, "[]");
            File.Move(temp, _modelPath, true);
        }
    }
    public void Dispose()
    {
        _timer.Dispose(); _stop.Cancel();
        Task[] running;
        lock (_gate) running = _inFlight.ToArray();
        try { Task.WaitAll(running); } catch (AggregateException) { }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_modelPath)!);
            var temp = _modelPath + ".tmp";
            lock (_gate) File.WriteAllText(temp, JsonSerializer.Serialize(_nodes));
            File.Move(temp, _modelPath, true);
        }
        catch (IOException) { }
        _stop.Dispose();
    }
}
