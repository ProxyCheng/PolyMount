using System.Text.Json;

namespace PolyMount.Core;

/// <summary>Pacing and bounds for the co-read/burst model.</summary>
public sealed class CoReadOptions
{
    public int TickIntervalMs { get; set; } = 50;
    /// <summary>A quiet gap that ends a burst.</summary>
    public int BurstGapMs { get; set; } = 50;
    /// <summary>The longest a burst may stay open.</summary>
    public int BurstWindowMs { get; set; } = 1000;
    public int MaxPathsPerBurst { get; set; } = 64;
    public int MaxLinks { get; set; } = 65536;
    public int MaxCoReadsPerPath { get; set; } = 8;
    public int MinCoReadCount { get; set; } = 2;
    public double MinConfidence { get; set; } = 0.5;
    public int MaxInFlight { get; set; } = 2;
    public long MaxFileBytes { get; set; } = 8L * 1024 * 1024;
    public int FeedbackWindowMs { get; set; } = 5000;
    public int FeedbackSamples { get; set; } = 32;
    public int RetentionMs { get; set; } = 3600000;
}

public sealed class CoReadLink
{
    public string To { get; set; } = "";
    public int Count { get; set; }
    public long LastSeen { get; set; }
}

/// <summary>
/// Co-read / dependency-burst predictor. The successor table asks "which single file comes
/// next"; a parallel loader does not answer that. This model records the paths that are read
/// close together in time by one process and, when the first path of such a burst is read
/// again (usually a manifest or an index), asks the layer to preload the other members.
/// <para>
/// It is deliberately cross-thread: a loader commonly reads the manifest on one thread and the
/// payloads on several IO threads, so the burst is grouped by process, not by caller stream.
/// The layer around it owns the held cache, quota, throttle and write invalidation; this model
/// only learns links and enqueues preloads.
/// </para>
/// </summary>
public sealed class CoReadPrediction : IPredictionModel
{
    private sealed class Burst
    {
        public long Start;
        public long Last;
        public string? First;
        public readonly List<string> Paths = new();
        public readonly HashSet<string> Seen = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ModelFile
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, int> Reads { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<CoReadLink>> Links { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly VirtualFileSystem _vfs;
    private readonly CoReadOptions _options;
    private readonly string _modelPath;
    private readonly Action<string, long, PreloadOrigin>? _preload;
    private readonly object _gate = new();
    private readonly Dictionary<int, Burst> _bursts = new();
    private readonly Dictionary<string, List<CoReadLink>> _links = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _reads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _pathLast = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, double> _throttle = new();
    private readonly Queue<(string Path, long Stream)> _queue = new();
    private readonly HashSet<(string Path, long Stream)> _queued = new();
    private readonly List<Task> _inFlight = new();
    private readonly Timer _timer;
    private readonly CancellationTokenSource _stop = new();
    private long _epoch;
    public long BurstPredictions { get; private set; }
    public long BurstCompleted { get; private set; }
    private int _linkCount;

    public CoReadPrediction(VirtualFileSystem vfs, CoReadOptions options, string modelPath,
        Action<string, long, PreloadOrigin>? preload = null)
    {
        if (options.TickIntervalMs is < 10 or > 10000 || options.BurstGapMs is < 0 or > 60000 ||
            options.BurstWindowMs is < 1 or > 600000 || options.MaxPathsPerBurst is < 2 or > 4096 ||
            options.MaxLinks is < 1 or > 1_048_576 || options.MaxCoReadsPerPath is < 1 or > 64 ||
            options.MinCoReadCount is < 1 or > 10000 || options.MinConfidence is < 0 or > 1 ||
            options.MaxInFlight is < 1 or > 64 || options.MaxFileBytes is <= 0 or > 256L * 1024 * 1024 ||
            options.FeedbackWindowMs is < 0 or > 600000 || options.FeedbackSamples is < 1 or > 4096 ||
            options.RetentionMs is < 0 or > 7 * 24 * 3600000)
            throw new ArgumentException("Invalid co-read prediction budgets");
        _vfs = vfs; _options = options; _modelPath = modelPath; _preload = preload;
        Load();
        using (ExecutionContext.SuppressFlow())
            _timer = new Timer(_ => Tick(), null, options.TickIntervalMs, options.TickIntervalMs);
    }

    public IDisposable? Observe(string path, long streamId, bool listing)
    {
        if (listing) return null;
        int pid = RequestDiagnostics.ProcessOfStream(streamId);
        lock (_gate)
        {
            long now = Environment.TickCount64;
            if (_bursts.TryGetValue(pid, out var burst) &&
                (now - burst.Last > _options.BurstGapMs || now - burst.Start > _options.BurstWindowMs))
            {
                Close(burst, DateTime.UtcNow.Ticks);
                burst = null;
            }
            if (burst == null)
            {
                burst = new Burst { Start = now, First = path };
                _bursts[pid] = burst;
            }
            if (burst.Paths.Count < _options.MaxPathsPerBurst && burst.Seen.Add(path)) burst.Paths.Add(path);
            burst.Last = now;
            if (CoReadsOf(path) is { } coReads)
                foreach (var to in coReads) Enqueue(to, streamId);
        }
        return null;
    }

    /// <summary>Associates the burst's first path (usually a manifest/index) with everything
    /// read alongside it. The first path is the trigger; links in that direction are what make
    /// the next startup preload the payloads when the manifest is read.</summary>
    private void Close(Burst burst, long now)
    {
        if (burst.First is not { } first || burst.Paths.Count < 2) return;
        _reads[first] = _reads.GetValueOrDefault(first) + 1;
        _pathLast[first] = now;
        foreach (var other in burst.Paths)
            if (!string.Equals(other, first, StringComparison.OrdinalIgnoreCase))
                AddLink(first, other, now);
    }

    private void AddLink(string from, string to, long now)
    {
        if (!_links.TryGetValue(from, out var links))
        {
            if (_links.Count >= _options.MaxLinks)
            {
                var oldest = _pathLast.MinBy(p => p.Value).Key;
                RemovePath(oldest);
            }
            links = new List<CoReadLink>();
            _links[from] = links;
        }
        var link = links.FirstOrDefault(l => string.Equals(l.To, to, StringComparison.OrdinalIgnoreCase));
        if (link == null)
        {
            links.Add(new CoReadLink { To = to, Count = 1, LastSeen = now });
            _linkCount++;
            while (links.Count > _options.MaxCoReadsPerPath) { links.Remove(links.MinBy(l => l.Count)!); _linkCount--; }
        }
        else
        {
            link.Count++;
            link.LastSeen = now;
        }
        _pathLast[from] = now;
        if (_linkCount > _options.MaxLinks * (long)Math.Max(1, _options.MaxCoReadsPerPath))
        {
            // A hard ceiling even when one path accumulates many short-lived successors.
            var oldest = _pathLast.MinBy(p => p.Value).Key;
            RemovePath(oldest);
        }
    }

    private void RemovePath(string path)
    {
        if (_links.Remove(path, out var removed)) _linkCount -= removed.Count;
        _pathLast.Remove(path);
    }

    /// <summary>The confident co-reads of a path, best first.</summary>
    private List<string>? CoReadsOf(string path)
    {
        if (!_links.TryGetValue(path, out var links) || !_reads.TryGetValue(path, out int reads) || reads <= 0)
            return null;
        var picked = links
            .Where(l => l.Count >= _options.MinCoReadCount && (double)l.Count / reads >= _options.MinConfidence)
            .OrderByDescending(l => l.Count)
            .Take(_options.MaxCoReadsPerPath)
            .Select(l => l.To)
            .ToList();
        return picked.Count > 0 ? picked : null;
    }

    private void Enqueue(string path, long stream)
    {
        if (_queue.Count >= 256 || !_queued.Add((path, stream))) return;
        _queue.Enqueue((path, stream));
    }

    private void Tick()
    {
        lock (_gate)
        {
            _inFlight.RemoveAll(task => task.IsCompleted);
            if (_stop.IsCancellationRequested || _queue.Count == 0) return;
            while (_queue.Count > 0 && _inFlight.Count < _options.MaxInFlight * 4)
            {
                var (path, stream) = _queue.Dequeue();
                _queued.Remove((path, stream));
                double throttle = _throttle.GetValueOrDefault(stream, 1);
                if (throttle <= 0) continue;
                if (_inFlight.Count >= Math.Max(1, (int)Math.Round(_options.MaxInFlight * throttle))) break;
                if (!_vfs.TryLookup(path, out var entry) || entry.IsDir || entry.Size > _options.MaxFileBytes) continue;
                BurstPredictions++;
                long epoch = _epoch;
                using (ExecutionContext.SuppressFlow()) _inFlight.Add(Task.Run(() =>
                {
                    using var lane = ReadPriority.Enter(ReadPriority.Speculative);
                    try
                    {
                        if (epoch != _epoch) return;
                        using var trace = RequestDiagnostics.Begin("Predict.Burst", path, _vfs.Diagnostics);
                        if (_preload != null) _preload(path, stream, PreloadOrigin.Burst);
                        else { using var read = _vfs.OpenRead(path, out _, speculative: true); }
                        lock (_gate) if (epoch == _epoch) { BurstCompleted++; }
                    }
                    catch (IOException) { }
                    catch (Exception ex) { using var trace = RequestDiagnostics.Begin("Predict.Failed", path, _vfs.Diagnostics); trace?.Stage(ex.GetType().Name); }
                }));
            }
        }
    }

    public PredictionModelStats Stats
    {
        get
        {
            lock (_gate)
                return new PredictionModelStats(_linkCount, BurstPredictions, BurstCompleted,
                    0, 0, 0, 0, BurstPredictions, BurstCompleted, _bursts.Count, 0);
        }
    }

    public void SetThrottle(long streamId, double value) { lock (_gate) _throttle[streamId] = Math.Clamp(value, 0, 1); }
    public void SetSpatialThrottle(long streamId, double value) { }
    public void SetBurstThrottle(long streamId, double value) { lock (_gate) _throttle[streamId] = Math.Clamp(value, 0, 1); }

    public void Reset()
    {
        lock (_gate)
        {
            _epoch++;
            _bursts.Clear();
            _links.Clear();
            _reads.Clear();
            _pathLast.Clear();
            _queue.Clear();
            _queued.Clear();
            _linkCount = 0;
            BurstPredictions = 0;
            BurstCompleted = 0;
            Directory.CreateDirectory(Path.GetDirectoryName(_modelPath)!);
            File.WriteAllText(_modelPath, JsonSerializer.Serialize(new ModelFile()));
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_modelPath) || new FileInfo(_modelPath).Length > 64L * 1024 * 1024) return;
            var file = JsonSerializer.Deserialize<ModelFile>(File.ReadAllText(_modelPath));
            if (file is not { Version: 1 } || file.Links == null) return;
            long horizon = _options.RetentionMs <= 0 ? long.MinValue
                : DateTime.UtcNow.Ticks - TimeSpan.FromMilliseconds(_options.RetentionMs).Ticks;
            foreach (var pair in file.Links)
            {
                var kept = pair.Value.Where(l => l.LastSeen >= horizon).Take(_options.MaxCoReadsPerPath).ToList();
                if (kept.Count == 0) continue;
                _links[pair.Key] = kept;
                _linkCount += kept.Count;
                _pathLast[pair.Key] = kept.Max(l => l.LastSeen);
                if (_reads.TryGetValue(pair.Key, out int reads) && reads > 0) continue;
                _reads[pair.Key] = 1;
            }
            foreach (var pair in file.Reads) _reads[pair.Key] = pair.Value;
        }
        catch (IOException) { }
        catch (JsonException) { }
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
            var file = new ModelFile();
            lock (_gate)
            {
                foreach (var pair in _links) file.Links[pair.Key] = pair.Value;
                foreach (var pair in _reads) file.Reads[pair.Key] = pair.Value;
            }
            File.WriteAllText(_modelPath, JsonSerializer.Serialize(file));
        }
        catch (IOException) { }
        _stop.Dispose();
    }
}
