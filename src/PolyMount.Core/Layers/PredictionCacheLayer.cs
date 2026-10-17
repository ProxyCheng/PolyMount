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
public sealed class PredictionCacheLayer : CacheLayer, ILowerChainLayer, IDisposable
{
    private sealed class Entry(VirtualObject value, byte[]? content, long bytes, bool sibling)
    {
        public VirtualObject Value = value;
        public byte[]? Content = content;
        public long Bytes = bytes;
        /// <summary>Fetched because the reader was walking a directory in order, rather than
        /// because the successor table predicted it. The two are counted apart so a stride's
        /// hits and waste are visible without giving it a confidence of its own.</summary>
        public bool Sibling = sibling;
        public int Reads;
        public LinkedListNode<string>? Node;
        public long StoredAt;
        /// <summary>Counted against the guess already; an entry is judged once.</summary>
        public bool Scored;
    }

    private readonly long _capacity;
    private readonly Func<string> _generation;
    private readonly ReadPredictionOptions _options;
    private readonly string _model;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private ReadPrediction? _predictor;
    private string? _generationSnapshot;
    private long _bytes, _hits, _used, _wasted, _wastedBytes, _fetchBytes, _siblingUsed, _siblingWasted;
    // Recent guess quality, judged per entry as soon as it is resolved rather than waiting
    // for an eviction that a generous budget may never reach.
    private int _paidOff, _wastedGuesses;
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
    /// <summary>Directories the stride is currently walking.</summary>
    public int TrackedDirectories => _predictor?.TrackedDirectories ?? 0;
    /// <summary>Size of the speculative fetch in flight, for the usage panel.</summary>
    public long FetchBytes => Interlocked.Read(ref _fetchBytes);

    public void Attach(VirtualFileSystem lower) =>
        _predictor = new ReadPrediction(lower, _options, _model, path => Preload(lower, path, sibling: false),
            path => Preload(lower, path, sibling: true));

    internal IDisposable? Observe(string path) => _predictor?.Demand(path);

    /// <summary>Held copies belong to the generation they were fetched at; a source revision
    /// change makes every one of them stale.</summary>
    private void CheckGeneration()
    {
        string generation = Generation;
        if (_generationSnapshot == generation) return;
        _generationSnapshot = generation;
        _entries.Clear();
        _lru.Clear();
        _bytes = 0;
    }

    /// <summary>Fetches a predicted path through the layers below and keeps it here.</summary>
    private void Preload(VirtualFileSystem lower, string path, bool sibling)
    {
        VirtualObject value;
        try { value = lower.LoadObject(path, speculative: true); }
        catch (VfsNotFoundException) { return; }
        // An entry larger than this layer's whole budget is never worth keeping.
        if (!value.Header.IsDir && value.Header.Size > Math.Min(_capacity, _options.MaxFileBytes)) return;

        if (value.Header.IsDir) { Store(path, value, null, sibling); return; }
        Interlocked.Exchange(ref _fetchBytes, value.Header.Size);
        try
        {
            using var source = value.Content.OpenRead();
            using var payload = new MemoryStream(value.Header.Size > int.MaxValue ? 0 : (int)value.Header.Size);
            source.CopyTo(payload);
            payload.Position = 0;
            Store(path, value, payload, sibling);
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
            if (entry.Reads++ == 0) { _used++; _paidOff++; if (entry.Sibling) _siblingUsed++; PublishQuality(); }
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
            if (kept.Reads++ == 0) { _used++; _paidOff++; if (kept.Sibling) _siblingUsed++; PublishQuality(); }
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

    public void StoreObject(string path, VirtualObject value, Stream? content = null) => Store(path, value, content, sibling: false);

    /// <summary>Keeps a value, recording whether read-ahead chose it by walking a directory or
    /// by predicting a successor; only the origin differs, so everything else is shared.</summary>
    private void Store(string path, VirtualObject value, Stream? content, bool sibling)
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
            if (_entries.TryGetValue(path, out var previous))
            {
                _bytes -= previous.Bytes;
                _lru.Remove(previous.Node!);
                _entries.Remove(path);
            }
            var entry = new Entry(value.Snapshot(), payload, bytes, sibling) { StoredAt = Environment.TickCount64 };
            entry.Node = _lru.AddFirst(path);
            _entries[path] = entry;
            _bytes += bytes;
            Trim();
        }
    }

    public void Store(string path, string version, VfsEntry entry, Stream content) => StoreObject(path, new(entry), content);

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
        foreach (var entry in _entries.Values)
        {
            if (entry.Reads > 0 || entry.Scored || entry.StoredAt > horizon) continue;
            entry.Scored = true;
            _wastedGuesses++;
        }
        PublishQuality();
    }

    /// <summary>Hands the recent hit rate to the predictor as a throttle once a window has
    /// filled. Reading ahead spends source bandwidth, so a run of guesses nobody reads should
    /// slow it down without changing what the model believes.</summary>
    private void PublishQuality()
    {
        int resolved = _paidOff + _wastedGuesses;
        if (resolved < _options.FeedbackSamples) return;
        _predictor?.Throttle = (double)_paidOff / resolved;
        _paidOff = 0;
        _wastedGuesses = 0;
    }

    /// <summary>Evicts least recently used entries. One that was never read is counted as wasted.</summary>
    private void Trim()
    {
        while (_bytes > _capacity && _lru.Last is { } tail)
        {
            var key = tail.Value;
            var entry = _entries[key];
            if (entry.Reads == 0) { _wasted++; _wastedBytes += entry.Bytes; if (entry.Sibling) _siblingWasted++; if (!entry.Scored) { entry.Scored = true; _wastedGuesses++; PublishQuality(); } }
            _bytes -= entry.Bytes;
            _entries.Remove(key);
            _lru.RemoveLast();
        }
    }

    /// <summary>Drops held data. Nothing here is durable, so this is the whole of the state.</summary>
    public override void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _lru.Clear();
            _bytes = 0;
        }
    }

    public void Invalidate() => Clear();

    /// <summary>Drops held data and forgets the learned order.</summary>
    public void ResetLearning()
    {
        Clear();
        if (_predictor != null) _predictor.Reset();
        else if (File.Exists(_model)) File.Delete(_model);
    }

    public void Dispose() => _predictor?.Dispose();
}
