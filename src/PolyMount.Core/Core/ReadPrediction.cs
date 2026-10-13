using System.Text.Json;

namespace PolyMount.Core;

public sealed class ReadPredictionOptions
{
    public bool Enabled { get; set; }
    public int MaxTransitions { get; set; } = 4096;
    public long MaxFileBytes { get; set; } = 8L * 1024 * 1024;
    public int IdleDelayMs { get; set; } = 250;
    public int MaxActiveDemandForPreload { get; set; } = 1;
}

/// <summary>Bounded next-file predictor. One speculative load, only after demand becomes idle.</summary>
public sealed class ReadPrediction : IDisposable
{
    private sealed record Edge(string From, string To, int Hits);
    private readonly object _gate = new();
    private readonly Dictionary<string, Edge> _edges = new(StringComparer.OrdinalIgnoreCase);
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
    private Task? _loading;
    private long _epoch;
    public int LearnedTransitions { get { lock (_gate) return _edges.Count; } }
    public long Predictions { get; private set; }
    public long Completed { get; private set; }

    public ReadPrediction(VirtualFileSystem vfs, ReadPredictionOptions options, string modelPath, Action<string>? preload = null)
    {
        if (options.MaxTransitions is < 1 or > 65536 || options.MaxFileBytes is <= 0 or > 256L * 1024 * 1024 || options.IdleDelayMs < 100)
            throw new ArgumentException("Invalid read prediction budgets");
        _vfs = vfs; _options = options; _modelPath = modelPath; _preload = preload;
        try
        {
            if (File.Exists(modelPath) && new FileInfo(modelPath).Length <= 8 * 1024 * 1024)
                foreach (var edge in JsonSerializer.Deserialize<Edge[]>(File.ReadAllText(modelPath)) ?? [])
                    if (_edges.Count < options.MaxTransitions) _edges[edge.From] = edge;
        }
        catch (IOException) { }
        catch (JsonException) { }
        using (ExecutionContext.SuppressFlow()) _timer = new Timer(_ => Tick(), null, 100, 100);
    }
    public IDisposable Demand(string path)
    {
        lock (_gate)
        {
            _activeDemand++; _lastDemand = Environment.TickCount64;
            if (path.Length > 4096) return new DemandScope(this);
            if (_previous != null && !string.Equals(_previous, path, StringComparison.OrdinalIgnoreCase))
            {
                if (_edges.TryGetValue(_previous, out var edge) && edge.To.Equals(path, StringComparison.OrdinalIgnoreCase))
                    _edges[_previous] = edge with { Hits = Math.Min(10000, edge.Hits + 1) };
                else
                {
                    if (_edges.Count >= _options.MaxTransitions) _edges.Remove(_edges.MinBy(p => p.Value.Hits).Key);
                    _edges[_previous] = new(_previous, path, 1);
                }
            }
            _previous = path;
            // One previous run is enough to nominate a candidate. Keep a small
            // queue so a later unrelated access does not erase earlier predictions.
            if (_edges.TryGetValue(path, out var next) && next.Hits >= 1 && !_candidates.Contains(next.To))
            {
                if (_candidates.Count >= 16) _candidates.Dequeue();
                _candidates.Enqueue(next.To);
            }
        }
        return new DemandScope(this);
    }
    private sealed class DemandScope(ReadPrediction owner) : IDisposable
    {
        public void Dispose() { lock (owner._gate) { owner._activeDemand--; owner._lastDemand = Environment.TickCount64; } }
    }
    private void Tick()
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested || _activeDemand > _options.MaxActiveDemandForPreload || _candidates.Count == 0 ||
                _loading is { IsCompleted: false } ||
                (_activeDemand == 0 && Environment.TickCount64 - _lastDemand < _options.IdleDelayMs)) return;
            string candidate = _candidates.Dequeue(); Predictions++;
            long epoch = _epoch;
            using (ExecutionContext.SuppressFlow()) _loading = Task.Run(() =>
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
            });
        }
    }
    public void Reset()
    {
        lock (_gate)
        {
            // Do not reset live demand counts or abort an already-started I/O.
            // Queued candidates from the old model cannot begin after this reset.
            _epoch++;
            _edges.Clear(); _previous = null; _candidates.Clear();
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
        Task? loading;
        lock (_gate) loading = _loading;
        loading?.GetAwaiter().GetResult();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_modelPath)!);
            var temp = _modelPath + ".tmp";
            lock (_gate) File.WriteAllText(temp, JsonSerializer.Serialize(_edges.Values.ToArray()));
            File.Move(temp, _modelPath, true);
        }
        catch (IOException) { }
        _stop.Dispose();
    }
}
