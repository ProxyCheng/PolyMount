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
    /// <summary>Whether a directory the reader has started walking is read in full after a few
    /// of its files. This is the spatial counterpart of the successor table: a loader that
    /// consumes one asset directory often consumes the rest of it, and one directory position
    /// says more than one transition per file. Bounded by files, bytes and directory size,
    /// because a reader that only samples a large directory must not trigger it.</summary>
    public bool SpatialEnabled { get; set; }
    /// <summary>The path-successor table. Off leaves the stream history unused; a spatial-only
    /// or co-read-only composition does not need it.</summary>
    public bool SuccessorEnabled { get; set; } = true;
    /// <summary>The per-directory stride. Off stops the sibling walk from nominating.</summary>
    public bool SiblingEnabled { get; set; } = true;
    /// <summary>Distinct files from one directory that must be read before the rest is fetched.</summary>
    public int SpatialTriggerFiles { get; set; } = 2;
    /// <summary>Most files one spatial batch may name.</summary>
    public int SpatialMaxFiles { get; set; } = 32;
    /// <summary>Byte budget for one spatial batch, from the listing's own sizes.</summary>
    public long SpatialMaxBytes { get; set; } = 32L * 1024 * 1024;
    /// <summary>Directories larger than this are containers, not asset sets, and are not
    /// expanded in full.</summary>
    public int SpatialMaxChildren { get; set; } = 256;
    /// <summary>A directory at most this many entries large is expanded without asking for a
    /// common prefix; small asset directories (fonts, a few configs) are usually consumed whole.</summary>
    public int SpatialSmallDirectory { get; set; } = 16;
    /// <summary>For a larger directory, the files the caller has read must share at least this
    /// many leading characters; a world-level directory holds several worlds' files, and a
    /// GUID-sharded directory shares no meaningful prefix at all.</summary>
    public int SpatialMinPrefix { get; set; } = 3;
    /// <summary>How long before a directory may contribute another spatial batch.</summary>
    public int SpatialCooldownMs { get; set; } = 30000;
    /// <summary>Runtime histories kept at once (one per caller stream). The learned table is
    /// shared and keyed by paths, so evicting a stream costs its history, not the model.</summary>
    public int MaxStreams { get; set; } = 64;

    /// <summary>A copy, so one layer can enable different strategies on the same tuned profile.</summary>
    public ReadPredictionOptions Copy() => (ReadPredictionOptions)MemberwiseClone();
}

/// <summary>Which prediction produced a read-ahead entry. The layer counts hits and waste
/// apart per origin, so a stride or a spatial batch that stops paying off can be told from the
/// successor table without giving either a second confidence model.</summary>
public enum PreloadOrigin : byte
{
    Table = 0,
    Sibling = 1,
    Spatial = 2,
    Burst = 3
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
    { Enabled = true, TickIntervalMs = 50, IdleDelayMs = 0, MaxActiveDemandForPreload = 2, MaxInFlight = 1, MaxTransitions = 8192, SpatialEnabled = true };

    /// <summary>Preloads while a few reads are in flight, so a loader that pauses between
    /// resources still gets files ahead of asking for them.</summary>
    public static ReadPredictionOptions SteadyOptions() => new()
    { Enabled = true, TickIntervalMs = 25, IdleDelayMs = 0, MaxActiveDemandForPreload = 8, MaxInFlight = 2, MaxTransitions = 32768, SpatialEnabled = true };

    /// <summary>Keeps up with a continuously busy loader. Competes with it for the source,
    /// which helps when the source is latency-bound and hurts when it is bandwidth-bound.</summary>
    public static ReadPredictionOptions EagerOptions() => new()
    { Enabled = true, TickIntervalMs = 10, IdleDelayMs = 0, MaxActiveDemandForPreload = 32, MaxInFlight = 4, MaxTransitions = 131072, SpatialEnabled = true };

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
public sealed class ReadPrediction : IPredictionModel
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
    private readonly Action<string, long, PreloadOrigin>? _preload;
    /// <summary>Stride read-ahead for a directory the reader is walking, beside the successor
    /// table. A loader enumerating a directory in order is not producing successors to learn:
    /// it is traversing a listing, and one position in that listing says what a key per sibling
    /// would spend the whole table's budget saying.</summary>
    private readonly SiblingStride _siblings;
    private readonly Timer _timer;
    private readonly CancellationTokenSource _stop = new();
    /// <summary>Per-caller history. The learned table is shared and keyed by paths; only the
    /// history that forms a key is per stream, which is what keeps interleaved callers from
    /// writing each other's transitions into the model.</summary>
    private sealed class StreamState
    {
        public long Id;
        public string? Previous;
        public readonly List<string> History = new();
        public double Throttle;
        public double SpatialThrottle;
        public int InFlight;
        public long LastSeen;
    }
    private readonly Dictionary<long, StreamState> _streams = new();
    private double _defaultThrottle = 1;
    /// <summary>Per stream and directory: how many of its files this caller has read, which
    /// ones it has already asked for, and when a spatial batch was last queued. The listing is
    /// the one a lower cache already holds — this never enumerates a source.</summary>
    private sealed class SpatialWalk
    {
        public readonly HashSet<string> Seen = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Queued = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The caller enumerated this directory. A directory rest-prefetch is only
        /// worth making when the engine itself listed it; a GUID shard reached by direct probes
        /// is not a set of siblings anything will consume.</summary>
        public bool Listed;
        public long LastQueued;
        public long LastSeen;
    }
    private readonly Dictionary<(long Stream, string Parent), SpatialWalk> _spatialWalks = new();
    private readonly Queue<(long Stream, string Path)> _spatialCandidates = new();
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
    /// <summary>Preloads issued from a spatial batch (the rest of a directory the caller had
    /// started reading), and how many of them completed.</summary>
    public long SpatialPredictions { get; private set; }
    public long SpatialCompleted { get; private set; }
    /// <summary>Directories currently being walked, across streams.</summary>
    public int TrackedDirectories => _siblings.TrackedDirectories;
    /// <summary>Runtime streams with a history right now.</summary>
    public int ActiveStreams { get { lock (_gate) return _streams.Values.Distinct().Count(); } }
    /// <summary>How much of the configured read-ahead to actually issue, 0..1. Setting this
    /// applies the same throttle to every stream, which is what tests and a global shutdown
    /// want; the owning cache normally lowers one stream at a time through <see cref="SetThrottle"/>.</summary>
    public double Throttle
    {
        get { lock (_gate) return _defaultThrottle; }
        set
        {
            lock (_gate)
            {
                _defaultThrottle = Math.Clamp(value, 0, 1);
                foreach (var stream in _streams.Values.Distinct()) stream.Throttle = _defaultThrottle;
            }
        }
    }
    /// <summary>Sets one stream's read-ahead quality. A stream that keeps guessing wrong slows
    /// down without taking the others with it.</summary>
    public void SetThrottle(long streamId, double value)
    {
        lock (_gate)
            if (_streams.TryGetValue(streamId, out var stream)) stream.Throttle = Math.Clamp(value, 0, 1);
    }
    /// <summary>Separate quality for spatial batches: a directory expansion that wastes its
    /// bytes should not also throttle the successor table, which may be doing well.</summary>
    public void SetSpatialThrottle(long streamId, double value)
    {
        lock (_gate)
            if (_streams.TryGetValue(streamId, out var stream)) stream.SpatialThrottle = Math.Clamp(value, 0, 1);
    }

    /// <param name="preload">Fetches a path a nomination predicted, with the stream that
    /// nominated it and which predictor produced it, so the owner can keep the origins apart.</param>
    public ReadPrediction(VirtualFileSystem vfs, ReadPredictionOptions options, string modelPath,
        Action<string, long, PreloadOrigin>? preload = null)
    {
        if (options.MaxTransitions is < 1 or > 1048576 || options.MaxFileBytes is <= 0 or > 256L * 1024 * 1024 ||
            options.IdleDelayMs is < 0 or > 60000 || options.TickIntervalMs is < 10 or > 10000 ||
            options.MaxInFlight is < 1 or > 64 || options.MaxActiveDemandForPreload is < 0 or > 1024 ||
            options.MaxSuccessorsPerKey is < 1 or > 16 || options.MinConfidence is < 0 or > 1 ||
            options.ContextDepth is < 1 or > 4 || options.PrefetchDepth is < 1 or > 8 ||
            options.FeedbackWindowMs is < 0 or > 600000 || options.FeedbackSamples is < 1 or > 4096 ||
            options.RetentionMs is < 0 or > 7 * 24 * 3600000 ||
            options.SpatialTriggerFiles is < 1 or > 1024 || options.SpatialMaxFiles is < 1 or > 4096 ||
            options.SpatialMaxBytes is < 0 or > 256L * 1024 * 1024 ||
            options.SpatialMaxChildren is < 1 or > 65536 ||
            options.SpatialSmallDirectory is < 0 or > 65536 || options.SpatialMinPrefix is < 0 or > 64 ||
            options.SpatialCooldownMs is < 0 or > 600000 || options.MaxStreams is < 1 or > 256)
            throw new ArgumentException("Invalid read prediction budgets");
        _vfs = vfs; _options = options; _modelPath = modelPath; _preload = preload;
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
    private readonly record struct Nomination(long StreamId, Context? Context, SiblingStep? Sibling);

    public IDisposable Demand(string path) => Demand(path, RequestDiagnostics.AmbientStreamId);

    IDisposable? IPredictionModel.Observe(string path, long streamId, bool listing) =>
        Demand(path, streamId, listing);

    PredictionModelStats IPredictionModel.Stats => new(
        LearnedTransitions, Predictions, Completed,
        SiblingPredictions, SiblingCompleted,
        SpatialPredictions, SpatialCompleted,
        0, 0, ActiveStreams, TrackedDirectories);

    /// <summary>The successor/stride/spatial model has no burst work; the dial is accepted so
    /// the shared layer can call every model through one interface.</summary>
    public void SetBurstThrottle(long streamId, double value) { }


    /// <summary>
    /// One demand from one caller stream. <paramref name="streamId"/> selects which history
    /// forms the context; the successor table itself is shared and keyed by the paths, so a
    /// stream is a runtime hint and the learned model remains useful across processes and runs.
    /// <paramref name="listing"/> says the caller enumerated a directory rather than read a
    /// file; that is the signal a spatial batch is allowed to act on.
    /// </summary>
    public IDisposable Demand(string path, long streamId, bool listing = false)
    {
        StreamState stream;
        lock (_gate) stream = GetStream(streamId, path);
        // The stride reads the listing back from a layer, so it runs outside the metadata lock:
        // the layers publish a read-ahead's quality back into the throttle, and taking their
        // locks while holding this one would close a cycle with that.
        if (path.Length <= 4096)
        {
            if (listing)
            {
                if (_options.SpatialEnabled) NoteListing(path, stream.Id);
            }
            else
            {
                if (_options.SiblingEnabled && _siblings.Nominate(path, stream.Id) is { } sibling)
                    Enqueue(new Nomination(stream.Id, null, sibling));
                if (_options.SpatialEnabled) NominateSpatial(path, stream.Id);
            }
        }
        lock (_gate)
        {
            _activeDemand++; _lastDemand = Environment.TickCount64;
            if (path.Length > 4096) return new DemandScope(this);
            if (_options.SuccessorEnabled)
            {
                if (stream.Previous != null && !string.Equals(stream.Previous, path, StringComparison.OrdinalIgnoreCase))
                {
                    var observed = ContextOf(stream, stream.Previous);
                    Observe(observed.Key, path);
                    // Also record the last path alone: a longer key needs the pattern to repeat
                    // before it has any evidence, and read-ahead should not go dark until then.
                    if (observed.Paths.Length > 1) Observe(stream.Previous, path);
                }
                PushHistory(stream, stream.Previous);
                stream.Previous = path;
                // Nominate the context, not the successor it points at: what to fetch is resolved
                // when read-ahead runs, so a nomination cannot act on evidence that has since been
                // contradicted. Keep a small queue so a later access does not erase earlier ones.
                var context = ContextOf(stream, path);
                if (HasEvidence(context)) Enqueue(new Nomination(stream.Id, context, null));
            }
        }
        return new DemandScope(this);
    }

    /// <summary>The history a caller stream belongs to. An exact id is the common case. When a
    /// thread-pool thread changes between reads of the same loader, a stream of the same process
    /// whose next prediction is this path is the one that caller was using, so the new id is
    /// aliased to it instead of starting a cold history. Pid 0 (tests, callers with no boundary
    /// identity) never aliases: its thread ids are the only distinction there is.</summary>
    private StreamState GetStream(long streamId, string path)
    {
        long now = Environment.TickCount64;
        if (_streams.TryGetValue(streamId, out var state)) { state.LastSeen = now; return state; }
        int pid = RequestDiagnostics.ProcessOfStream(streamId);
        if (pid != 0)
        {
            foreach (var candidate in _streams.Values.Distinct())
            {
                if (RequestDiagnostics.ProcessOfStream(candidate.Id) != pid) continue;
                if (candidate.Previous == null) continue;
                if (!string.Equals(PredictedSuccessor(ContextOf(candidate, candidate.Previous)), path,
                        StringComparison.OrdinalIgnoreCase)) continue;
                _streams[streamId] = candidate;
                candidate.LastSeen = now;
                return candidate;
            }
        }
        if (_streams.Values.Distinct().Count() >= _options.MaxStreams)
        {
            var oldest = _streams.Values.Distinct().MinBy(s => s.LastSeen)!;
            foreach (var key in _streams.Where(p => ReferenceEquals(p.Value, oldest)).Select(p => p.Key).ToArray())
                _streams.Remove(key);
        }
        state = new StreamState { Id = streamId, LastSeen = now, Throttle = _defaultThrottle, SpatialThrottle = 1 };
        _streams[streamId] = state;
        return state;
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
                if (queued.StreamId != nomination.StreamId) continue;
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

    private Context ContextOf(StreamState stream, string path)
    {
        int take = Math.Min(_options.ContextDepth - 1, stream.History.Count);
        var paths = new string[take + 1];
        for (int i = 0; i < take; i++) paths[i] = stream.History[stream.History.Count - take + i];
        paths[take] = path;
        return new Context(paths);
    }

    private void PushHistory(StreamState stream, string? path)
    {
        if (path == null || _options.ContextDepth <= 1) return;
        stream.History.Add(path);
        while (stream.History.Count > _options.ContextDepth - 1) stream.History.RemoveAt(0);
    }

    /// <summary>
    /// Notes that the caller enumerated <paramref name="directory"/>. A directory rest-prefetch
    /// is only allowed after this: the engine itself decided the directory is a set, and the
    /// listing is in a cache already. Direct probes of GUID-sharded or packed layouts never
    /// produce this signal, which is exactly the case where expanding a directory would be noise.
    /// </summary>
    private void NoteListing(string directory, long streamId)
    {
        if (directory.Length == 0) return;
        lock (_gate)
        {
            var walk = SpatialWalkFor(streamId, directory);
            walk.Listed = true;
            walk.LastSeen = Environment.TickCount64;
        }
    }

    /// <summary>
    /// Nominates the rest of a directory after the caller has read a few of its files. The
    /// directory must have been enumerated by the caller and its listing must still be held by
    /// a cache: the trigger is what the reader did, never an extra enumeration. For a large
    /// directory the files already read must share a prefix (a world directory holds several
    /// worlds' levels; a GUID shard shares nothing), and a batch is bounded by files, by the
    /// listing's own sizes, by directory size, and by a cooldown.
    /// </summary>
    private void NominateSpatial(string path, long streamId)
    {
        string parent = VfsPath.Parent(path);
        if (parent.Length == 0) return;
        SpatialWalk walk;
        lock (_gate)
        {
            walk = SpatialWalkFor(streamId, parent);
            walk.LastSeen = Environment.TickCount64;
            if (!walk.Listed) return;
            if (walk.Seen.Count >= 4096) walk.Seen.Clear();
            if (!walk.Seen.Add(VfsPath.Name(path))) return;
            if (walk.Seen.Count < _options.SpatialTriggerFiles) return;
            // If the successor table already knows what comes next, it is the better predictor
            // for this read; a directory batch would only duplicate it. Spatial is for the
            // directory whose order the caller does not repeat, not for the one it walks.
            if (_streams.TryGetValue(streamId, out var stream) &&
                PredictedSuccessor(ContextOf(stream, path)) != null) return;
            if (walk.LastQueued != 0 && Environment.TickCount64 - walk.LastQueued < _options.SpatialCooldownMs) return;
        }
        // Outside the lock: a listing comes from a cache, and a cache publishes its own quality
        // back into the throttle.
        var listing = SiblingStride.FindHeldListing(_vfs, parent);
        if (listing?.Children == null || listing.Children.Count > _options.SpatialMaxChildren) return;
        bool small = listing.Children.Count <= _options.SpatialSmallDirectory;
        string prefix = small ? "" : CommonPrefix(walk.Seen);
        if (!small && prefix.Length < _options.SpatialMinPrefix) return;
        var files = listing.Children
            .Where(c => !c.IsDir && c.Size <= _options.MaxFileBytes &&
                        (small || c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        long bytes = 0;
        int queued = 0;
        lock (_gate)
        {
            // Another thread may have queued a batch while the listing was being read.
            if (walk.LastQueued != 0 && Environment.TickCount64 - walk.LastQueued < _options.SpatialCooldownMs) return;
            foreach (var file in files)
            {
                if (walk.Queued.Count >= _options.SpatialMaxFiles) break;
                if (walk.Seen.Contains(file.Name) || walk.Queued.Contains(file.Name)) continue;
                if (bytes + file.Size > _options.SpatialMaxBytes) break;
                bytes += file.Size;
                walk.Queued.Add(file.Name);
                if (_spatialCandidates.Count >= 512) _spatialCandidates.Dequeue();
                _spatialCandidates.Enqueue((streamId, VfsPath.Child(parent, file.Name)));
                queued++;
            }
            if (queued > 0) walk.LastQueued = Environment.TickCount64;
        }
    }

    private SpatialWalk SpatialWalkFor(long streamId, string parent)
    {
        var key = (streamId, parent);
        if (_spatialWalks.TryGetValue(key, out var walk)) return walk;
        if (_spatialWalks.Count >= 128)
            _spatialWalks.Remove(_spatialWalks.MinBy(p => p.Value.LastSeen).Key);
        walk = new SpatialWalk();
        _spatialWalks[key] = walk;
        return walk;
    }

    /// <summary>The common prefix of the names a caller has read, ignoring case. Empty when
    /// they share nothing, which is the signal not to expand a large directory.</summary>
    private static string CommonPrefix(IEnumerable<string> names)
    {
        string? prefix = null;
        foreach (var name in names)
        {
            if (prefix == null) { prefix = name; continue; }
            int length = 0, max = Math.Min(prefix.Length, name.Length);
            while (length < max && char.ToUpperInvariant(prefix[length]) == char.ToUpperInvariant(name[length])) length++;
            prefix = prefix[..length];
            if (prefix.Length == 0) break;
        }
        return prefix ?? "";
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
        List<SiblingStep>? covered = null;
        lock (_gate)
        {
            ForgetStale();
            _inFlight.RemoveAll(task => task.IsCompleted);
            if (_stop.IsCancellationRequested || _activeDemand > _options.MaxActiveDemandForPreload) return;
            if (_activeDemand == 0 && Environment.TickCount64 - _lastDemand < _options.IdleDelayMs) return;
            // Every stream gets its own budget, but the mount still has to stay out of the
            // source's way: the global bound is a multiple of the per-stream one, capped.
            int globalBudget = Math.Min(64, _options.MaxInFlight * Math.Max(1, _streams.Values.Distinct().Count()));
            if (_inFlight.Count >= globalBudget) return;

            // A tick is a chance to issue, not a limit of one. At eight streams the demands
            // arrive faster than a ten-millisecond timer, and issuing one nomination per tick
            // would leave the queue draining behind the reader: the model would be right and
            // the data would still not be there. Fill every stream's budget, then stop.
            int issued = 0;
            int attempts = _candidates.Count;
            while (issued < 64 && attempts-- > 0 && _candidates.Count > 0 && _inFlight.Count < globalBudget)
            {
                var next = _candidates.Peek();
                if (!_streams.TryGetValue(next.StreamId, out var stream)) { _candidates.Dequeue(); continue; }
                int budget = (int)Math.Round(_options.MaxInFlight * stream.Throttle);
                if (budget <= 0 || stream.InFlight >= budget)
                {
                    // This stream is at its budget; another stream behind it may not be.
                    _candidates.Dequeue();
                    _candidates.Enqueue(next);
                    continue;
                }
                _candidates.Dequeue();
                string candidate;
                PreloadOrigin origin;
                if (next.Sibling is { } step)
                {
                    candidate = step.Path;
                    // The position is covered as soon as it is issued, so the reader's own demand
                    // for the same sibling does not nominate it a second time.
                    (covered ??= new()).Add(step);
                    SiblingPredictions++;
                    origin = PreloadOrigin.Sibling;
                }
                else if (PredictedSuccessor(next.Context!) is { } predicted)
                {
                    candidate = predicted;
                    origin = PreloadOrigin.Table;
                }
                else continue;
                Issue(next.StreamId, stream, candidate, origin, next);
                issued++;
            }

            // A spatial batch is opportunistic: it gets capacity the table and the stride are
            // not using, never a slot one of them is waiting for.
            int spatialAttempts = _spatialCandidates.Count;
            while (issued < 64 && spatialAttempts-- > 0 && _spatialCandidates.Count > 0 && _inFlight.Count < globalBudget)
            {
                var next = _spatialCandidates.Peek();
                if (!_streams.TryGetValue(next.Stream, out var stream)) { _spatialCandidates.Dequeue(); continue; }
                int budget = (int)Math.Round(_options.MaxInFlight * stream.SpatialThrottle);
                if (budget <= 0 || stream.InFlight >= budget)
                {
                    _spatialCandidates.Dequeue();
                    _spatialCandidates.Enqueue(next);
                    continue;
                }
                _spatialCandidates.Dequeue();
                SpatialPredictions++;
                Issue(next.Stream, stream, next.Path, PreloadOrigin.Spatial, null);
                issued++;
            }
        }
        // Taken outside the lock: the stride reads layers, and a layer publishes guess quality
        // back into this predictor, so it must never be entered while this lock is held.
        if (covered != null) foreach (var step in covered) _siblings.Note(step);
    }

    /// <summary>Starts one read-ahead task. Caller holds <see cref="_gate"/>.</summary>
    private void Issue(long streamId, StreamState stream, string path, PreloadOrigin origin, Nomination? nomination)
    {
        Predictions++;
        bool sibling = origin == PreloadOrigin.Sibling;
        long epoch = _epoch;
        string path0 = path;
        stream.InFlight++;
        using (ExecutionContext.SuppressFlow())
            _inFlight.Add(Task.Run(() => PreloadTask(stream, streamId, path0, origin, nomination, epoch, sibling)));
    }

    private void PreloadTask(StreamState owner, long streamId, string path0, PreloadOrigin origin,
        Nomination? nomination, long epoch, bool sibling)
    {
        // This task is read-ahead from the first lookup to the last, and it runs with the
        // execution context suppressed, so the lanes it was queued from do not flow here:
        // the lane is named at the boundary that creates the work.
        using var lane = ReadPriority.Enter(ReadPriority.Speculative);
        try
        {
            var context = nomination?.Context;
            var walk = nomination?.Sibling;
            string path = path0;
            int depth = origin == PreloadOrigin.Spatial ? 1 : _options.PrefetchDepth;
            for (int step = 0; step < depth; step++)
            {
                if (_stop.IsCancellationRequested || epoch != _epoch) return;
                if (_activeDemand > _options.MaxActiveDemandForPreload) return;
                if (!_vfs.TryLookup(path, out var entry) || (!entry.IsDir && entry.Size > _options.MaxFileBytes)) return;
                if (entry.IsDir && _preload == null) return;
                using (var trace = RequestDiagnostics.Begin("Predict.Preload", path, _vfs.Diagnostics))
                {
                    if (_preload != null) _preload(path, streamId, origin);
                    else { using var read = _vfs.OpenRead(path, out _, speculative: true); }
                }
                lock (_gate)
                    if (epoch == _epoch)
                    {
                        Completed++;
                        if (sibling) SiblingCompleted++;
                        if (origin == PreloadOrigin.Spatial) SpatialCompleted++;
                    }
                if (_activeDemand > _options.MaxActiveDemandForPreload) return;
                if (origin == PreloadOrigin.Spatial) return;
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
        catch (Exception ex) { using var trace = RequestDiagnostics.Begin("Predict.Failed", path0, _vfs.Diagnostics); trace?.Stage(ex.GetType().Name); }
        finally { lock (_gate) owner.InFlight = Math.Max(0, owner.InFlight - 1); }
    }

    public void Reset()
    {
        lock (_gate)
        {
            // Do not reset live demand counts or abort an already-started I/O.
            // Queued candidates from the old model cannot begin after this reset.
            _epoch++;
            _nodes.Clear(); _streams.Clear(); _candidates.Clear(); _spatialWalks.Clear(); _spatialCandidates.Clear();
            Predictions = 0; Completed = 0; SiblingPredictions = 0; SiblingCompleted = 0;
            SpatialPredictions = 0; SpatialCompleted = 0;
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
