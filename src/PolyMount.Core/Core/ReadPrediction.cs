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
    /// <summary>How many recent paths form the key. One is the last-address key; a longer
    /// window is the history-based key of GHB (Nesbit &amp; Smith), which tells apart
    /// successors a last-address key conflates.</summary>
    public int ContextDepth { get; set; } = 2;
    /// <summary>How many confident steps to follow when reading ahead. Read-ahead issues the
    /// same reads either way; depth decides how far ahead of the reader they land.</summary>
    public int PrefetchDepth { get; set; } = 2;
    /// <summary>How long a held entry may go unread before it counts against the guess for the
    /// feedback throttle. Eviction alone is too late a signal when the budget is generous.</summary>
    public int FeedbackWindowMs { get; set; } = 5000;
    /// <summary>Resolutions per quality window; the throttle is recomputed this often.</summary>
    public int FeedbackSamples { get; set; } = 32;
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

/// <summary>Bounded next-file predictor: a successor table learned from access order, and a
/// stride that names the next sibling of a directory being walked in order. Both nominate into
/// one queue and are issued under one budget, one throttle and one epoch.</summary>
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
    private readonly Action<string>? _siblingPreload;
    /// <summary>Stride read-ahead for a directory the reader is walking, beside the successor
    /// table. A loader enumerating a directory in order is not producing successors to learn:
    /// it is traversing a listing, and one position in that listing says what a key per sibling
    /// would spend the whole table's budget saying.</summary>
    private readonly SiblingStride _siblings;
    private readonly Timer _timer;
    private readonly CancellationTokenSource _stop = new();
    private string? _previous;
    private readonly List<string> _history = new();
    private readonly Queue<Nomination> _candidates = new();
    private long _lastDemand;
    private int _activeDemand;
    private readonly List<Task> _inFlight = new();
    private long _epoch;
    private const int SweepInterval = 50;
    /// <summary>How many nominations wait for a read-ahead slot. A queue beyond this is a
    /// reader that has moved on, so the ones behind are the ones it no longer wants.</summary>
    private const int MaxCandidates = 16;
    private int _sweepCounter;
    /// <summary>Distinct observed transitions, across every path's successor set.</summary>
    public int LearnedTransitions { get { lock (_gate) return _nodes.Sum(p => p.Value.Count); } }
    public long Predictions { get; private set; }
    public long Completed { get; private set; }
    /// <summary>Preloads issued because the reader was walking a directory in order, rather
    /// than because the successor table predicted them. A subset of <see cref="Predictions"/>,
    /// so what the stride contributes is separable from what the table contributes.</summary>
    public long SiblingPredictions { get; private set; }
    /// <summary>The same subset of <see cref="Completed"/>.</summary>
    public long SiblingCompleted { get; private set; }
    /// <summary>Directories currently being walked.</summary>
    public int TrackedDirectories => _siblings.TrackedDirectories;
    /// <summary>How much of the configured read-ahead to actually issue, 0..1. The owning
    /// cache lowers it when recent guesses have not been paying off.</summary>
    public double Throttle
    {
        get { lock (_gate) return _throttle; }
        set { lock (_gate) _throttle = Math.Clamp(value, 0, 1); }
    }
    private double _throttle = 1;

    /// <param name="preload">Fetches a path the successor table predicted.</param>
    /// <param name="siblingPreload">Fetches a path the stride nominated, when the owner wants the
    /// two origins told apart — it is what lets a cache count a stride's hits and waste without
    /// adding a second voting mechanism. Falls back to <paramref name="preload"/>.</param>
    public ReadPrediction(VirtualFileSystem vfs, ReadPredictionOptions options, string modelPath, Action<string>? preload = null,
        Action<string>? siblingPreload = null)
    {
        if (options.MaxTransitions is < 1 or > 1048576 || options.MaxFileBytes is <= 0 or > 256L * 1024 * 1024 ||
            options.IdleDelayMs is < 0 or > 60000 || options.TickIntervalMs is < 10 or > 10000 ||
            options.MaxInFlight is < 1 or > 64 || options.MaxActiveDemandForPreload is < 0 or > 1024 ||
            options.MaxSuccessorsPerKey is < 1 or > 16 || options.MinConfidence is < 0 or > 1 ||
            options.ContextDepth is < 1 or > 4 || options.PrefetchDepth is < 1 or > 8 ||
            options.FeedbackWindowMs is < 0 or > 600000 || options.FeedbackSamples is < 1 or > 4096 ||
            options.RetentionMs is < 0 or > 7 * 24 * 3600000)
            throw new ArgumentException("Invalid read prediction budgets");
        _vfs = vfs; _options = options; _modelPath = modelPath; _preload = preload; _siblingPreload = siblingPreload;
        _siblings = new SiblingStride(vfs);
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
    /// <summary>One thing read-ahead may fetch. A table nomination carries the context to be
    /// resolved when there is a slot, so it cannot act on evidence that has since been
    /// contradicted; a stride nomination already names its path and carries the position it was
    /// taken from, so the fetch can continue the walk it came from.</summary>
    private readonly record struct Nomination(Context? Context, SiblingStep? Sibling);

    public IDisposable Demand(string path)
    {
        // The stride reads the listing back from a layer, so it runs outside this lock: the
        // layers publish a read-ahead's quality back into the throttle, and taking their locks
        // while holding this one would close a cycle with that.
        if (path.Length <= 4096 && _siblings.Nominate(path) is { } sibling)
            Enqueue(new Nomination(null, sibling));
        lock (_gate)
        {
            _activeDemand++; _lastDemand = Environment.TickCount64;
            if (path.Length > 4096) return new DemandScope(this);
            if (_previous != null && !string.Equals(_previous, path, StringComparison.OrdinalIgnoreCase))
            {
                var observed = ContextOf(_previous);
                Observe(observed.Key, path);
                // Also record the last path alone: a longer key needs the pattern to repeat
                // before it has any evidence, and read-ahead should not go dark until then.
                if (observed.Paths.Length > 1) Observe(_previous, path);
            }
            PushHistory(_previous);
            _previous = path;
            // Nominate the context, not the successor it points at: what to fetch is resolved
            // when read-ahead runs, so a nomination cannot act on evidence that has since been
            // contradicted. Keep a small queue so a later access does not erase earlier ones.
            var context = ContextOf(path);
            if (HasEvidence(context)) Enqueue(new Nomination(context, null));
        }
        return new DemandScope(this);
    }

    /// <summary>Queues a nomination for the next read-ahead slot, unless the same thing is
    /// already waiting: a repeat of a nomination is not new evidence, and the queue is short
    /// enough that filling it with repeats would push out the ones the reader is about to want.
    /// </summary>
    private void Enqueue(Nomination nomination)
    {
        lock (_gate)
        {
            foreach (var queued in _candidates)
            {
                bool duplicate = nomination.Sibling is { } step
                    ? queued.Sibling is { } waiting && string.Equals(waiting.Path, step.Path, StringComparison.OrdinalIgnoreCase)
                    : queued.Context?.Key == nomination.Context!.Key;
                if (duplicate) return;
            }
            if (_candidates.Count >= MaxCandidates) _candidates.Dequeue();
            _candidates.Enqueue(nomination);
        }
    }

    /// <summary>The paths that precede a prediction, ending with the one just read. A single
    /// path is the order-1 key; a longer window is GHB's history-based key, which tells apart
    /// successors that conflate onto one last-address key.</summary>
    private sealed class Context(string[] paths)
    {
        public readonly string[] Paths = paths;
        public string Key { get; } = paths.Length == 1 ? paths[0] : string.Join((char)31, paths);
        public Context Follow(string next)
        {
            var shifted = new string[Paths.Length];
            Array.Copy(Paths, 1, shifted, 0, Paths.Length - 1);
            shifted[^1] = next;
            return new Context(shifted);
        }
        /// <summary>The key of the last <paramref name="length"/> paths, so a shorter history
        /// can answer when the longest one has nothing to say.</summary>
        public string Suffix(int length) => length >= Paths.Length
            ? Key
            : string.Join((char)31, Paths, Paths.Length - length, length);
    }

    private Context ContextOf(string path)
    {
        int take = Math.Min(_options.ContextDepth - 1, _history.Count);
        var paths = new string[take + 1];
        for (int i = 0; i < take; i++) paths[i] = _history[_history.Count - take + i];
        paths[take] = path;
        return new Context(paths);
    }

    private void PushHistory(string? path)
    {
        if (path == null || _options.ContextDepth <= 1) return;
        _history.Add(path);
        while (_history.Count > _options.ContextDepth - 1) _history.RemoveAt(0);
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

    /// <summary>The successor of a context if it holds enough of that context's observations
    /// to be worth the bandwidth - SPP's path confidence, applied per step.</summary>
    private string? ConfidentSuccessor(string key)
    {
        lock (_gate)
        {
            if (!_nodes.TryGetValue(key, out var successors) || successors.Count == 0) return null;
            int total = successors.Sum(t => t.Hits);
            if (total <= 0) return null;
            var top = successors.MaxBy(t => t.Hits)!;
            return (double)top.Hits / total >= _options.MinConfidence ? top.To : null;
        }
    }

    private bool HasEvidence(Context context)
    {
        lock (_gate)
            for (int length = context.Paths.Length; length >= 1; length--)
                if (_nodes.ContainsKey(context.Suffix(length))) return true;
        return false;
    }

    /// <summary>The longest history that still has something confident to say. A pure order-N
    /// key would answer nothing until the pattern repeated; falling back to a shorter history
    /// keeps the single-observation behaviour while gaining the longer one's specificity.</summary>
    private string? PredictedSuccessor(Context context)
    {
        for (int length = context.Paths.Length; length >= 1; length--)
            if (ConfidentSuccessor(context.Suffix(length)) is { } next) return next;
        return null;
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
        SiblingStep? covered = null;
        lock (_gate)
        {
            ForgetStale();
            _inFlight.RemoveAll(task => task.IsCompleted);
            // The owning cache lowers the throttle when recent guesses stopped paying off.
            // A stride is issued through the same budget, so a run of guesses nobody reads
            // slows the walk down exactly as it slows the table down.
            int budget = (int)Math.Round(_options.MaxInFlight * _throttle);
            if (_stop.IsCancellationRequested || _activeDemand > _options.MaxActiveDemandForPreload || _candidates.Count == 0 ||
                _inFlight.Count >= budget ||
                (_activeDemand == 0 && Environment.TickCount64 - _lastDemand < _options.IdleDelayMs)) return;
            var nominated = _candidates.Dequeue();
            string? candidate;
            if (nominated.Sibling is { } step)
            {
                candidate = step.Path;
                // The position is covered as soon as it is issued, so the reader's own demand
                // for the same sibling does not nominate it a second time.
                covered = step;
                SiblingPredictions++;
            }
            else if (PredictedSuccessor(nominated.Context!) is { } predicted) candidate = predicted;
            else return;
            Predictions++;
            bool sibling = nominated.Sibling != null;
            var fetch = sibling ? _siblingPreload ?? _preload : _preload;
            long epoch = _epoch;
            using (ExecutionContext.SuppressFlow()) _inFlight.Add(Task.Run(() =>
            {
                // This task is read-ahead from the first lookup to the last, and it runs with the
                // execution context suppressed, so the lanes it was queued from do not flow here:
                // the lane is named at the boundary that creates the work.
                using var lane = ReadPriority.Enter(ReadPriority.Speculative);
                try
                {
                    var context = nominated.Context;
                    var walk = nominated.Sibling;
                    string path = candidate!;
                    for (int step = 0; step < _options.PrefetchDepth; step++)
                    {
                        if (_stop.IsCancellationRequested || epoch != _epoch) return;
                        if (_activeDemand > _options.MaxActiveDemandForPreload) return;
                        if (!_vfs.TryLookup(path, out var entry) || (!entry.IsDir && entry.Size > _options.MaxFileBytes)) return;
                        if (entry.IsDir && fetch == null) return;
                        using (var trace = RequestDiagnostics.Begin("Predict.Preload", path, _vfs.Diagnostics))
                        {
                            if (fetch != null) fetch(path);
                            else { using var stream = _vfs.OpenRead(path, out _, speculative: true); }
                        }
                        lock (_gate) if (epoch == _epoch) { Completed++; if (sibling) SiblingCompleted++; }
                        if (_activeDemand > _options.MaxActiveDemandForPreload) return;
                        // A stride carries the walk it came from one position further; the table
                        // follows its own path, keeping going while the next step is confident.
                        if (walk is { } from)
                        {
                            if (!_siblings.TryNext(from, out var following)) return;
                            _siblings.Note(following);
                            path = following.Path; walk = following;
                            continue;
                        }
                        context = context!.Follow(path);
                        if (PredictedSuccessor(context) is not { } next) return;
                        path = next;
                    }
                }
                catch (IOException) { }
                catch (Exception ex) { using var trace = RequestDiagnostics.Begin("Predict.Failed", candidate!, _vfs.Diagnostics); trace?.Stage(ex.GetType().Name); }
            }));
        }
        // Taken outside the lock: the stride reads layers, and a layer publishes guess quality
        // back into this predictor, so it must never be entered while this lock is held.
        if (covered is { } taken) _siblings.Note(taken);
    }
    public void Reset()
    {
        lock (_gate)
        {
            // Do not reset live demand counts or abort an already-started I/O.
            // Queued candidates from the old model cannot begin after this reset.
            _epoch++;
            _nodes.Clear(); _previous = null; _history.Clear(); _candidates.Clear();
            Predictions = 0; Completed = 0; SiblingPredictions = 0; SiblingCompleted = 0;
            Directory.CreateDirectory(Path.GetDirectoryName(_modelPath)!);
            string temp = _modelPath + ".tmp";
            File.WriteAllText(temp, "[]");
            File.Move(temp, _modelPath, true);
        }
        // Outside the lock: the stride reads layers, and a layer publishes guess quality back
        // into this predictor (see Tick).
        _siblings.Reset();
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
