using PolyMount.Core;

namespace PolyMount.Layers;

/// <summary>Sequence-learning preload driver. Read caches below it own all cache hits.</summary>
public sealed class PredictionCacheLayer : CacheLayer, IDisposable
{
    private readonly Func<string> _generation;
    private readonly long _capacity;
    private readonly ReadPredictionOptions _options;
    private readonly string _model;
    private ReadPrediction? _predictor;
    private long _bytes;
    public PredictionCacheLayer(long capacity, Func<string> generation, ReadPredictionOptions options, string model)
    {
        if (capacity < 1 || capacity > 256L * 1048576) throw new ArgumentException("Prediction temporary capacity must be 1–256 MiB");
        _capacity = capacity; _generation = generation; _options = options; _model = model;
    }
    public override string Name => "prediction-cache";
    public override string Generation => _generation();
    public override bool Writable => false;
    public long MemoryBytes => Interlocked.Read(ref _bytes);
    public void Attach(VirtualFileSystem lower)
    {
        // Without a lower read cache, speculative downloads have nowhere useful
        // to go. Remain a pass-through layer and do not issue background reads.
        if (!lower.Layers.OfType<ICacheLayer>().Any()) return;
        _predictor = new ReadPrediction(lower, _options, _model, path =>
        {
            if (lower.Layers.OfType<ICacheLayer>().Any(cache => cache.TryGetEntry(path, out _))) return;
            if (!lower.TryLookup(path, out var header)) return;
            if (!header.IsDir && header.Size > Math.Min(_capacity, _options.MaxFileBytes)) return;
            Interlocked.Exchange(ref _bytes, header.Size);
            try
            {
                // OpenRead performs ordinary read-through fill in lower caches.
                // They retain their own safe content references before this stream
                // is closed; the predictor neither duplicates bytes nor serves hits.
                lower.LoadObject(path, speculative: true);
            }
            finally { Interlocked.Exchange(ref _bytes, 0); }
        });
    }
    internal IDisposable? Observe(string path) => _predictor?.Demand(path);
    public override bool TryGetEntry(string path, out VfsEntry entry) { entry = null!; return false; }
    public override IReadOnlyList<VfsEntry> ListOwn(string path) => Array.Empty<VfsEntry>();
    public override bool TryOpenRead(string path, out VfsEntry entry, out Stream stream)
    { entry = null!; stream = null!; return false; }
    // Content invalidation belongs to the owning lower cache. Its VFS generation
    // and visibility validation prevent stale speculative data from being served.
    public override void Clear() => ResetLearning();
    public void ResetLearning()
    {
        if (_predictor != null) _predictor.Reset();
        else if (File.Exists(_model)) File.Delete(_model);
    }
    public void Dispose() => _predictor?.Dispose();
}
