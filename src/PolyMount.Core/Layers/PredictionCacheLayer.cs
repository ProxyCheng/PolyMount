using PolyMount.Core;

namespace PolyMount.Layers;

/// <summary>
/// Speculative read-ahead. Reads passing through are recorded to learn access order, and when
/// a successor is predicted while there is spare read-ahead budget it is fetched through the
/// layers below and kept in this layer's own memory cache — so a correct guess promotes the
/// data to RAM ahead of the reader.
/// <para>
/// The layer never inspects the layers below beyond fetching through them: whether they could
/// have served the read anyway is their business. What matters is whether a kept entry was read
/// before it was evicted. A kept entry later read is a hit; one evicted while still unread was
/// wasted. Both are counters only — neither feeds back into the learned model.
/// </para>
/// </summary>
public sealed class PredictionCacheLayer : CacheLayer, ILowerChainLayer, IWriteInvalidation, IDisposable
{
    private sealed class Entry(VirtualObject value, byte[]? content, long bytes, PreloadOrigin origin, long stream)
    {
        public VirtualObject Value = value;
        public byte[]? Content = content;
        public long Bytes = bytes;
        /// <summary>Which predictor chose this entry: the successor table, the directory stride,
        /// or a spatial batch. Hits and waste are counted per origin so one that stops paying
        /// off can be throttled without speaking for the others.</summary>
        public PreloadOrigin Origin = origin;
        /// <summary>The caller stream that nominated this entry; capacity and feedback are
        /// kept per stream so one loader cannot evict or silence another's guesses.</summary>
        public long Stream = stream;
        public int Reads;
        public LinkedListNode<string>? Node;
        public long StoredAt;
        /// <summary>Counted against the guess already; an entry is judged once.</summary>
        public bool Scored;
    }

    private sealed class Quality
    {
        public int Paid, Wasted, SpatialPaid, SpatialWasted;
    }

    private readonly long _capacity;
    private readonly Func<string> _generation;
    private readonly ReadPredictionOptions _options;
    private readonly string _model;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    /// <summary>Bytes held per caller stream, so a batch of guesses for one loader cannot push
    /// another loader's entries out. The map is the set of streams with entries right now.</summary>
    private readonly Dictionary<long, long> _streamBytes = new();
    private readonly Dictionary<long, Quality> _quality = new();
    private ReadPrediction? _predictor;
    private string? _generationSnapshot;
    private long _bytes, _hits, _used, _wasted, _wastedBytes, _fetchBytes, _siblingUsed, _siblingWasted;
    private long _spatialUsed, _spatialWasted;
    private long _lastScored;

    public PredictionCacheLayer(long capacity, Func<string> generation, ReadPredictionOptions options, string model)
    {
        if (capacity < 1 || capacity > 256L * 1048576) throw new ArgumentException("Prediction temporary capacity must be 1–256 MiB");
        _capacity = capacity; _generation = generation; _options = options; _model = model;
    }

    public override string Name => "prediction-cache";
    public override string Generation => _generation();
    public override bool Writable => false;

    public long Capacity => _capacity;
    public long MemoryBytes { get { lock (_gate) return _bytes; } }
    public int HeldObjects { get { lock (_gate) return _entries.Count; } }
    /// <summary>Reads served from this layer's own copy.</summary>
    public long CacheHits { get { lock (_gate) return _hits; } }
    /// <summary>Held entries that were read at least once — a guess that paid off.</summary>
    public long Used { get { lock (_gate) return _used; } }
    /// <summary>Kept entries evicted without ever being read — the guesses that did not pay off.</summary>
    public long Wasted { get { lock (_gate) return _wasted; } }
    /// <summary>Size of everything wasted, so the read-ahead cost is visible in bytes too.</summary>
    public long WastedBytes { get { lock (_gate) return _wastedBytes; } }
    public long LearnedTransitions => _predictor?.LearnedTransitions ?? 0;
    public long PreloadsStarted => _predictor?.Predictions ?? 0;
    public long PreloadsCompleted => _predictor?.Completed ?? 0;
    /// <summary>Preloads the stride issued for a directory walk, and their share of the held
    /// entries read back and evicted unread. Subsets of the totals above and below, so what the
    /// stride contributes can be separated from what the successor table contributes.</summary>
    public long SiblingPreloads => _predictor?.SiblingPredictions ?? 0;
    public long SiblingCompleted => _predictor?.SiblingCompleted ?? 0;
    public long SiblingHits { get { lock (_gate) return _siblingUsed; } }
    public long SiblingWasted { get { lock (_gate) return _siblingWasted; } }
    /// <summary>Preloads a spatial batch issued (the rest of a directory the caller had started
    /// reading), and their share of hits and waste.</summary>
    public long SpatialPreloads => _predictor?.SpatialPredictions ?? 0;
    public long SpatialCompleted => _predictor?.SpatialCompleted ?? 0;
    public long SpatialHits { get { lock (_gate) return _spatialUsed; } }
    public long SpatialWasted { get { lock (_gate) return _spatialWasted; } }
    /// <summary>Directories the stride is currently walking.</summary>
    public int TrackedDirectories => _predictor?.TrackedDirectories ?? 0;
    /// <summary>Runtime caller streams with a history right now.</summary>
    public int ActiveStreams => _predictor?.ActiveStreams ?? 0;
    /// <summary>Entries dropped because a lower layer accepted a write to their path. They are
    /// not prediction misses: the content they held was correct until the write changed it.</summary>
    public long Invalidated { get { lock (_gate) return _invalidated; } }
    private long _invalidated;
    /// <summary>Size of the speculative fetch in flight, for the usage panel.</summary>
    public long FetchBytes => Interlocked.Read(ref _fetchBytes);

    public void Attach(VirtualFileSystem lower) =>
        _predictor = new ReadPrediction(lower, _options, _model,
            (path, stream, origin) => Preload(lower, path, stream, origin));

    internal IDisposable? Observe(string path, long streamId, bool listing = false) =>
        _predictor?.Demand(path, streamId, listing);

    /// <summary>Held copies belong to the generation they were fetched at; a source revision
    /// change makes every one of them stale.</summary>
    private void CheckGeneration()
    {
        string generation = Generation;
        if (_generationSnapshot == generation) return;
        _generationSnapshot = generation;
        _entries.Clear();
        _lru.Clear();
        _streamBytes.Clear();
        _quality.Clear();
        _bytes = 0;
    }

    /// <summary>Fetches a predicted path through the layers below and keeps it here.</summary>
    private void Preload(VirtualFileSystem lower, string path, long stream, PreloadOrigin origin)
    {
        VirtualObject value;
        try { value = lower.LoadObject(path, speculative: true); }
        catch (VfsNotFoundException) { return; }
        // An entry larger than this layer's whole budget is never worth keeping.
        if (!value.Header.IsDir && value.Header.Size > Math.Min(_capacity, _options.MaxFileBytes)) return;

        if (value.Header.IsDir) { Store(path, value, null, origin, stream); return; }
        Interlocked.Exchange(ref _fetchBytes, value.Header.Size);
        try
        {
            using var source = value.Content.OpenRead();
            using var payload = new MemoryStream(value.Header.Size > int.MaxValue ? 0 : (int)value.Header.Size);
            source.CopyTo(payload);
            payload.Position = 0;
            Store(path, value, payload, origin, stream);
        }
        catch (IOException) { }
        finally { Interlocked.Exchange(ref _fetchBytes, 0); }
    }

    public override bool TryGetEntry(string path, out VfsEntry entry)
    {
        bool found = TryReadObject(path, out var value);
        entry = found ? value.Header : null!;
        return found;
    }

    public override IReadOnlyList<VfsEntry> ListOwn(string path) =>
        TryReadObject(path, out var value) ? value.Children ?? Array.Empty<VfsEntry>() : Array.Empty<VfsEntry>();

    public override bool TryReadObject(string path, out VirtualObject value)
    {
        lock (_gate)
        {
            CheckGeneration();
            if (!_entries.TryGetValue(path, out var entry)) { value = null!; return false; }
            if (entry.Reads++ == 0) { _used++; if (entry.Origin == PreloadOrigin.Sibling) _siblingUsed++; else if (entry.Origin == PreloadOrigin.Spatial) _spatialUsed++; RecordPaid(entry); PublishQuality(entry.Stream); }
            _hits++;
            _lru.Remove(entry.Node!);
            _lru.AddFirst(entry.Node!);
            value = entry.Value;
            return true;
        }
    }

    public override bool TryOpenRead(string path, out VfsEntry entry, out Stream stream)
    {
        lock (_gate)
        {
            CheckGeneration();
            entry = null!; stream = null!;
            if (!_entries.TryGetValue(path, out var kept) || kept.Content == null) return false;
            if (kept.Reads++ == 0) { _used++; if (kept.Origin == PreloadOrigin.Sibling) _siblingUsed++; else if (kept.Origin == PreloadOrigin.Spatial) _spatialUsed++; RecordPaid(kept); PublishQuality(kept.Stream); }
            _hits++;
            _lru.Remove(kept.Node!);
            _lru.AddFirst(kept.Node!);
            entry = kept.Value.Header;
            stream = new MemoryStream(kept.Content, writable: false);
            return true;
        }
    }

    public void TouchObject(string path)
    {
        lock (_gate)
        {
            CheckGeneration();
            if (!_entries.TryGetValue(path, out var entry)) return;
            _lru.Remove(entry.Node!);
            _lru.AddFirst(entry.Node!);
        }
    }

    public void StoreObject(string path, VirtualObject value, Stream? content = null) =>
        Store(path, value, content, PreloadOrigin.Table, stream: 0);

    /// <summary>Keeps a value, recording which predictor chose it and which caller stream it
    /// belongs to; only the origin and owner differ, so everything else is shared.</summary>
    private void Store(string path, VirtualObject value, Stream? content, PreloadOrigin origin, long stream)
    {
        // A record with neither children nor content is header-only: the resolver telling
        // this layer that a path exists, not a guess it chose to read ahead. Keeping it would
        // count against a guess nobody made (HeldObjects, and the eviction that calls it
        // wasted), and TryReadObject is read as a complete listing, which a header is not.
        if (value.Children == null && content == null) return;
        byte[]? payload = null;
        if (content != null)
        {
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            payload = buffer.ToArray();
        }
        long bytes = CacheBudgets.Estimate(Generation + "|" + path, value) + (payload?.LongLength ?? 0);
        lock (_gate)
        {
            CheckGeneration();
            ScoreStale();
            if (bytes > _capacity) return;
            if (_entries.TryGetValue(path, out var previous)) RemoveEntry(path, previous);
            var entry = new Entry(value.Snapshot(), payload, bytes, origin, stream) { StoredAt = Environment.TickCount64 };
            entry.Node = _lru.AddFirst(path);
            _entries[path] = entry;
            _bytes += bytes;
            _streamBytes[stream] = _streamBytes.GetValueOrDefault(stream) + bytes;
            Trim();
            TrimStream(stream);
        }
    }

    public void Store(string path, string version, VfsEntry entry, Stream content) =>
        StoreObject(path, new(entry), content);

    /// <summary>Counts entries that have sat unread past the feedback window against the
    /// guess, and republishes the throttle. Runs on read-ahead admission, so it needs no timer
    /// and costs at most one pass per window.</summary>
    private void ScoreStale()
    {
        if (_options.FeedbackWindowMs <= 0) return;
        long now = Environment.TickCount64;
        if (now - _lastScored < _options.FeedbackWindowMs) return;
        _lastScored = now;
        long horizon = now - _options.FeedbackWindowMs;
        List<long>? touched = null;
        foreach (var entry in _entries.Values)
        {
            if (entry.Reads > 0 || entry.Scored || entry.StoredAt > horizon) continue;
            entry.Scored = true;
            var quality = QualityOf(entry.Stream);
            if (entry.Origin == PreloadOrigin.Spatial) quality.SpatialWasted++;
            else quality.Wasted++;
            (touched ??= new()).Add(entry.Stream);
        }
        if (touched == null) return;
        foreach (var stream in touched.Distinct()) PublishQuality(stream);
    }

    /// <summary>Hands one stream's recent hit rate to the predictor as a throttle once a window
    /// has filled. Reading ahead spends source bandwidth, so a run of guesses nobody reads
    /// should slow that stream down without changing what the model believes or silencing the
    /// other streams. Spatial batches have their own dial: an expansion that wastes its bytes
    /// should not throttle a successor table that is doing well.</summary>
    private void PublishQuality(long stream)
    {
        if (!_quality.TryGetValue(stream, out var quality)) return;
        int resolved = quality.Paid + quality.Wasted;
        if (resolved >= _options.FeedbackSamples)
        {
            _predictor?.SetThrottle(stream, (double)quality.Paid / resolved);
            quality.Paid = 0;
            quality.Wasted = 0;
        }
        int spatial = quality.SpatialPaid + quality.SpatialWasted;
        if (spatial >= _options.FeedbackSamples)
        {
            _predictor?.SetSpatialThrottle(stream, (double)quality.SpatialPaid / spatial);
            quality.SpatialPaid = 0;
            quality.SpatialWasted = 0;
        }
    }

    private Quality QualityOf(long stream)
    {
        if (!_quality.TryGetValue(stream, out var quality))
        {
            if (_quality.Count >= 256) _quality.Remove(_quality.Keys.First());
            _quality[stream] = quality = new();
        }
        return quality;
    }

    private void RecordPaid(Entry entry)
    {
        var quality = QualityOf(entry.Stream);
        if (entry.Origin == PreloadOrigin.Spatial) quality.SpatialPaid++;
        else quality.Paid++;
    }

    /// <summary>Removes an entry from both the index and the per-stream accounting. The caller
    /// has already decided whether it counts as a hit or as waste.</summary>
    private void RemoveEntry(string key, Entry entry)
    {
        _bytes -= entry.Bytes;
        if (_streamBytes.TryGetValue(entry.Stream, out var held))
        {
            held -= entry.Bytes;
            if (held <= 0) _streamBytes.Remove(entry.Stream);
            else _streamBytes[entry.Stream] = held;
        }
        _entries.Remove(key);
        _lru.Remove(entry.Node!);
    }

    /// <summary>Evicts least recently used entries. One that was never read is counted as wasted.</summary>
    private void Trim()
    {
        while (_bytes > _capacity && _lru.Last is { } tail)
        {
            var key = tail.Value;
            Evict(key, _entries[key]);
        }
    }

    /// <summary>Keeps one stream from filling the whole buffer. The share is the capacity
    /// divided by the number of streams currently holding entries; the newest entry is kept
    /// even if it alone exceeds the share, because a single large guess is still a guess.</summary>
    private void TrimStream(long stream)
    {
        int active = _streamBytes.Count;
        if (active <= 1) return;
        long cap = Math.Max(1, _capacity / active);
        if (!_streamBytes.TryGetValue(stream, out var held) || held <= cap) return;
        var keys = _lru.Where(key => _entries[key].Stream == stream).Reverse().ToList();
        for (int i = 0; i + 1 < keys.Count; i++)
        {
            if (!_streamBytes.TryGetValue(stream, out held) || held <= cap) break;
            Evict(keys[i], _entries[keys[i]]);
        }
    }

    /// <summary>Removes one entry, counting it as waste when it was never read. <see cref="ScoreStale"/>
    /// may already have judged it; an entry is counted once.</summary>
    private void Evict(string key, Entry entry)
    {
        if (entry.Reads == 0 && !entry.Scored)
        {
            entry.Scored = true;
            _wasted++;
            _wastedBytes += entry.Bytes;
            var quality = QualityOf(entry.Stream);
            if (entry.Origin == PreloadOrigin.Sibling) _siblingWasted++;
            else if (entry.Origin == PreloadOrigin.Spatial) _spatialWasted++;
            if (entry.Origin == PreloadOrigin.Spatial) quality.SpatialWasted++;
            else quality.Wasted++;
            PublishQuality(entry.Stream);
        }
        RemoveEntry(key, entry);
    }

    /// <summary>Drops held data. Nothing here is durable, so this is the whole of the state.</summary>
    public override void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _lru.Clear();
            _streamBytes.Clear();
            _quality.Clear();
            _bytes = 0;
        }
    }

    public void Invalidate() => Clear();

    /// <summary>
    /// Drops the entries a successful lower-layer write made stale. The write itself never
    /// reaches this layer: it is not writable, so the resolver passes it to the first writable
    /// layer below. Only after that layer returned success is this called, with the same change
    /// list. A file write also carries its parent's complete listing, so the held directory
    /// entry goes with it; a deletion takes the path's descendants as well.
    /// </summary>
    public void InvalidateAfterWrite(IReadOnlyList<ObjectChange> changes)
    {
        lock (_gate)
        {
            CheckGeneration();
            long removed = 0;
            foreach (var change in changes)
            {
                string path = change.Path;
                bool subtree = change.Object == null || !change.Object.Header.IsDir;
                foreach (var key in _entries.Keys.Where(key =>
                             key.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                             (subtree && key.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))).ToArray())
                {
                    RemoveEntry(key, _entries[key]);
                    removed++;
                }
            }
            _invalidated += removed;
        }
    }

    /// <summary>Drops held data and forgets the learned order.</summary>
    public void ResetLearning()
    {
        Clear();
        if (_predictor != null) _predictor.Reset();
        else if (File.Exists(_model)) File.Delete(_model);
    }

    public void Dispose() => _predictor?.Dispose();
}
