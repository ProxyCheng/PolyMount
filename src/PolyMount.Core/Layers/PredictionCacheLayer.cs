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
public sealed class PredictionCacheLayer : CacheLayer, IDisposable
{
    private sealed class Entry(VirtualObject value, byte[]? content, long bytes)
    {
        public VirtualObject Value = value;
        public byte[]? Content = content;
        public long Bytes = bytes;
        public int Reads;
        public LinkedListNode<string>? Node;
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
    private long _bytes, _hits, _wasted, _fetchBytes;

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
    /// <summary>Kept entries evicted without ever being read — the guesses that did not pay off.</summary>
    public long Wasted { get { lock (_gate) return _wasted; } }
    public long LearnedTransitions => _predictor?.LearnedTransitions ?? 0;
    public long PreloadsStarted => _predictor?.Predictions ?? 0;
    public long PreloadsCompleted => _predictor?.Completed ?? 0;
    /// <summary>Size of the speculative fetch in flight, for the usage panel.</summary>
    public long FetchBytes => Interlocked.Read(ref _fetchBytes);

    public void Attach(VirtualFileSystem lower) =>
        _predictor = new ReadPrediction(lower, _options, _model, path => Preload(lower, path));

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
    private void Preload(VirtualFileSystem lower, string path)
    {
        VirtualObject value;
        try { value = lower.LoadObject(path, speculative: true); }
        catch (VfsNotFoundException) { return; }
        // An entry larger than this layer's whole budget is never worth keeping.
        if (!value.Header.IsDir && value.Header.Size > Math.Min(_capacity, _options.MaxFileBytes)) return;

        if (value.Header.IsDir) { StoreObject(path, value); return; }
        Interlocked.Exchange(ref _fetchBytes, value.Header.Size);
        try
        {
            using var source = value.Content.OpenRead();
            using var payload = new MemoryStream(value.Header.Size > int.MaxValue ? 0 : (int)value.Header.Size);
            source.CopyTo(payload);
            payload.Position = 0;
            StoreObject(path, value, payload);
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
            entry.Reads++;
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
            kept.Reads++;
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

    public void StoreObject(string path, VirtualObject value, Stream? content = null)
    {
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
            if (bytes > _capacity) return;
            if (_entries.TryGetValue(path, out var previous))
            {
                _bytes -= previous.Bytes;
                _lru.Remove(previous.Node!);
                _entries.Remove(path);
            }
            var entry = new Entry(value.Snapshot(), payload, bytes);
            entry.Node = _lru.AddFirst(path);
            _entries[path] = entry;
            _bytes += bytes;
            Trim();
        }
    }

    public void Store(string path, string version, VfsEntry entry, Stream content) => StoreObject(path, new(entry), content);

    /// <summary>Evicts least recently used entries. One that was never read is counted as wasted.</summary>
    private void Trim()
    {
        while (_bytes > _capacity && _lru.Last is { } tail)
        {
            var key = tail.Value;
            var entry = _entries[key];
            if (entry.Reads == 0) _wasted++;
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
