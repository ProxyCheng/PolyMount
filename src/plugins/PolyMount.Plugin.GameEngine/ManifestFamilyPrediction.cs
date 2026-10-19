using System.Collections.Concurrent;
using System.Text.Json;
using PolyMount.Core;

namespace PolyMount.Plugins.GameEngine;

/// <summary>
/// A format-aware prediction strategy for game-engine content repositories, running inside the
/// shared prediction layer.
///
/// The layout it knows is the one many engines share: assets are grouped into packages, each
/// package is a directory holding a manifest index, and packages that belong together sit beside
/// each other under a family name (<c>char3_10071.local</c>, <c>char3_10072.local</c>, …).
/// Start-up opens those manifests family by family and in parallel; measured against a real
/// editor start-up that is what a cold window is mostly made of (thousands of manifest opens,
/// each one a source round trip), while the resources inside the packages are read rarely in
/// the same window.
///
/// So the one thing this strategy knows that a generic predictor does not is where the next
/// manifests are: when one is read, the rest of its family is already named in the listing of
/// the directory above. The strategy lists that directory once (it is one request, cached),
/// takes the names that follow the trigger inside the same family, and asks the layer to fetch
/// their manifests ahead of the reader. Everything after that — the held cache, the per-stream
/// quota, the speculative read lane, the hit/waste accounting — is the shared layer's, and the
/// family is the group feedback closes when it keeps guessing wrong.
/// </summary>
public sealed class ManifestFamilyPrediction : IPredictionModel
{
    private sealed class Family
    {
        public int Issued;
        public int Hits;
        public int Wasted;
        public int WasteStreak;
        public bool Disabled;
        public long LastBatch;
        public long LastSeen;
    }

    private sealed record Job(string Repository, string PackageDirectory, string Family, long Stream);

    private sealed class FamilyFile
    {
        public int Hits { get; set; }
        public int Wasted { get; set; }
        public int WasteStreak { get; set; }
        public bool Disabled { get; set; }
        public long LastSeen { get; set; }
    }

    private sealed class ModelFile
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, FamilyFile> Families { get; set; } = new();
    }

    private readonly VirtualFileSystem _lower;
    private readonly PreloadRequest _preload;
    private readonly ManifestFamilyOptions _options;
    private readonly string _modelPath;
    private readonly object _gate = new();
    private readonly object _listingGate = new();
    private readonly Dictionary<string, Family> _families = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (VfsEntry[] Children, long At)> _listings = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<(string Family, bool Paid)> _feedback = new();
    private readonly Dictionary<long, double> _throttle = new();
    private readonly HashSet<int> _processes = new();
    private readonly Queue<Job> _queue = new();
    private readonly List<Task> _inFlight = new();
    private readonly Timer _timer;
    private readonly CancellationTokenSource _stop = new();
    private long _epoch;
    private long _issued, _completed;

    public ManifestFamilyPrediction(PredictionStrategyContext context)
    {
        _lower = context.Lower;
        _preload = context.Preload;
        _options = ManifestFamilyOptions.From(context.Parameters);
        _modelPath = context.ModelPath;
        Load();
        using (ExecutionContext.SuppressFlow())
            _timer = new Timer(_ => Tick(), null, _options.TickIntervalMs, _options.TickIntervalMs);
    }

    /// <summary>How a package name is grouped with its siblings: everything up to and including
    /// the configured separator, after the package suffix is removed. That is what content
    /// repository directory names use to keep a family together (<c>char3_</c>,
    /// <c>000_BLEACH_</c>), and it is the unit one lookup may prefetch inside.</summary>
    internal static string FamilyOf(string package, string suffix, string separator)
    {
        if (suffix.Length > 0 && package.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            package = package[..^suffix.Length];
        if (separator.Length == 0) return package;
        int cut = package.LastIndexOf(separator, StringComparison.Ordinal);
        return cut < 0 ? package : package[..(cut + separator.Length)];
    }

    internal string FamilyOf(string package) => FamilyOf(package, _options.PackageSuffix, _options.FamilySeparator);

    /// <summary>One family's counters, as a command reports them: what this strategy issued for
    /// the family, how much of it was read, and whether unread guesses closed it.</summary>
    public sealed record FamilySnapshot(string Family, int Issued, int Hits, int Wasted, bool Disabled);

    /// <summary>What the strategy has learned, one row per family, busiest first. It is the data
    /// behind the plugin's own control operation; nothing in Core interprets it.</summary>
    public IReadOnlyList<FamilySnapshot> Snapshot()
    {
        lock (_gate)
        {
            ApplyFeedback();
            return _families
                .Select(pair => new FamilySnapshot(pair.Key, pair.Value.Issued, pair.Value.Hits, pair.Value.Wasted, pair.Value.Disabled))
                .OrderByDescending(family => family.Hits)
                .ThenBy(family => family.Family, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public IDisposable? Observe(string path, long streamId, bool listing)
    {
        if (listing) return null;
        if (!VfsPath.Name(path).Equals(_options.ManifestName, StringComparison.OrdinalIgnoreCase)) return null;
        string packageDirectory = VfsPath.Parent(path);
        string repository = VfsPath.Parent(packageDirectory);
        if (packageDirectory.Length == 0 || repository.Length == 0) return null;
        string family = FamilyOf(VfsPath.Name(packageDirectory));
        int process = RequestDiagnostics.ProcessOfStream(streamId);
        lock (_gate)
        {
            if (_processes.Count < 256 || _processes.Contains(process)) _processes.Add(process);
            if (!_families.TryGetValue(family, out var state)) _families[family] = state = new Family();
            state.LastSeen = Environment.TickCount64;
            if (state.Disabled) return null;
            long now = Environment.TickCount64;
            if (now - state.LastBatch < _options.FamilyCooldownMs) return null;
            state.LastBatch = now;
            if (_queue.Count < 64) _queue.Enqueue(new Job(repository, packageDirectory, family, streamId));
        }
        return null;
    }

    private void Tick()
    {
        Job? job = null;
        lock (_gate)
        {
            ApplyFeedback();
            _inFlight.RemoveAll(task => task.IsCompleted);
            if (_stop.IsCancellationRequested || _queue.Count == 0) return;
            if (_inFlight.Count >= _options.MaxInFlight) return;
            job = _queue.Dequeue();
            if (!_families.TryGetValue(job.Family, out var state) || state.Disabled) return;
            if (_throttle.GetValueOrDefault(job.Stream, 1) <= 0) return;
        }
        long epoch = _epoch;
        var work = Task.Run(() => Run(job, epoch));
        lock (_gate) _inFlight.Add(work);
    }

    /// <summary>One lookup: list the repository (cached), take the manifests that follow the
    /// trigger inside its family, and ask the layer to fetch them.</summary>
    private void Run(Job job, long epoch)
    {
        using var lane = ReadPriority.Enter(ReadPriority.Speculative);
        try
        {
            if (epoch != _epoch) return;
            var children = Listing(job.Repository);
            if (children == null) return;
            foreach (string path in Targets(children, job))
            {
                if (_stop.IsCancellationRequested || epoch != _epoch) return;
                lock (_gate)
                {
                    if (!_families.TryGetValue(job.Family, out var state) || state.Disabled) return;
                }
                using var trace = RequestDiagnostics.Begin("Predict.Manifest", path, _lower.Diagnostics);
                if (!_preload(path, job.Stream, PreloadOrigin.Burst, job.Family)) continue;
                lock (_gate)
                {
                    if (epoch != _epoch) return;
                    _issued++;
                    _completed++;
                    if (_families.TryGetValue(job.Family, out var state)) state.Issued++;
                }
            }
        }
        catch (VfsNotFoundException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            using var trace = RequestDiagnostics.Begin("Predict.Failed", job.Repository, _lower.Diagnostics);
            trace?.Stage(ex.GetType().Name);
        }
    }

    /// <summary>The names a lookup may prefetch: the trigger's family, in name order, taking the
    /// entries that follow it and wrapping around once. A family listing is one request and is
    /// reused until it goes stale, so a burst of manifest opens costs one listing, not one per
    /// open.</summary>
    private IEnumerable<string> Targets(VfsEntry[] children, Job job)
    {
        string current = VfsPath.Name(job.PackageDirectory);
        var names = children.Where(entry => entry.IsDir)
            .Select(entry => entry.Name)
            .Where(name => name.Length > 0)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0) yield break;
        int start = names.FindIndex(name => string.Equals(name, current, StringComparison.OrdinalIgnoreCase));
        if (_options.Stride)
        {
            // Start-ups that walk the whole repository follow the listing's order. The family
            // below guesses by name; that misses whenever the engine's order is not family
            // order, so this mode simply stays a few names ahead of the trigger.
            int taken = 0;
            for (int step = 1; step <= names.Count && taken < _options.StrideLookahead; step++)
            {
                int at = start < 0 ? step - 1 : (start + step) % names.Count;
                string name = names[at];
                if (string.Equals(name, current, StringComparison.OrdinalIgnoreCase)) continue;
                taken++;
                yield return VfsPath.Child(VfsPath.Child(job.Repository, name), _options.ManifestName);
            }
            yield break;
        }
        int takenFamily = 0;
        for (int step = 1; step <= names.Count && takenFamily < _options.MaxManifests; step++)
        {
            int at = start < 0 ? step - 1 : (start + step) % names.Count;
            string name = names[at];
            if (string.Equals(name, current, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(FamilyOf(name), job.Family, StringComparison.OrdinalIgnoreCase)) continue;
            takenFamily++;
            yield return VfsPath.Child(VfsPath.Child(job.Repository, name), _options.ManifestName);
        }
    }

    private VfsEntry[]? Listing(string repository)
    {
        lock (_gate)
        {
            if (_listings.TryGetValue(repository, out var cached) &&
                Environment.TickCount64 - cached.At < _options.ListingTtlMs) return cached.Children;
        }
        // One repository listing at a time: a burst of manifest opens starts several lookups at
        // once, and they all need the same listing.
        lock (_listingGate)
        {
            lock (_gate)
            {
                if (_listings.TryGetValue(repository, out var cached) &&
                    Environment.TickCount64 - cached.At < _options.ListingTtlMs) return cached.Children;
            }
            VfsEntry[] children;
            try { children = _lower.List(repository, recordAccess: false, speculative: true).ToArray(); }
            catch (VfsNotFoundException) { return null; }
            catch (IOException) { return null; }
            lock (_gate)
            {
                if (_listings.Count >= 64) _listings.Remove(_listings.Keys.First());
                _listings[repository] = (children, Environment.TickCount64);
            }
            return children;
        }
    }

    /// <summary>Applies what the layer reported. A run of manifests nobody opened closes the
    /// family they came from; one that was opened resets the run.</summary>
    private void ApplyFeedback()
    {
        while (_feedback.TryDequeue(out var report))
        {
            if (!_families.TryGetValue(report.Family, out var state)) continue;
            if (report.Paid)
            {
                state.Hits++;
                state.WasteStreak = 0;
            }
            else
            {
                state.Wasted++;
                if (++state.WasteStreak >= _options.GroupWasteStreak) state.Disabled = true;
            }
        }
    }

    public PredictionModelStats Stats
    {
        get
        {
            lock (_gate)
            {
                ApplyFeedback();
                int groups = 0, closed = 0;
                foreach (var family in _families.Values)
                {
                    if (family.Disabled) closed++;
                    else groups++;
                }
                return new PredictionModelStats(_families.Count, _issued, _completed,
                    0, 0, 0, 0, _issued, _completed, _processes.Count, 0, groups, closed);
            }
        }
    }

    public void ReportGroup(string group, bool paid) => _feedback.Enqueue((group, paid));
    public void SetThrottle(long streamId, double value) { lock (_gate) _throttle[streamId] = Math.Clamp(value, 0, 1); }
    public void SetSpatialThrottle(long streamId, double value) { }
    public void SetBurstThrottle(long streamId, double value) { lock (_gate) _throttle[streamId] = Math.Clamp(value, 0, 1); }

    public void Reset()
    {
        lock (_gate)
        {
            _epoch++;
            _families.Clear();
            _listings.Clear();
            _feedback.Clear();
            _queue.Clear();
            _processes.Clear();
            _issued = 0;
            _completed = 0;
            Directory.CreateDirectory(Path.GetDirectoryName(_modelPath)!);
            File.WriteAllText(_modelPath, JsonSerializer.Serialize(new ModelFile()));
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_modelPath) || new FileInfo(_modelPath).Length > 16L * 1024 * 1024) return;
            var file = JsonSerializer.Deserialize<ModelFile>(File.ReadAllText(_modelPath));
            if (file is not { Version: 1 } || file.Families == null) return;
            long horizon = _options.RetentionMs <= 0 ? long.MinValue
                : DateTime.UtcNow.Ticks - TimeSpan.FromMilliseconds(_options.RetentionMs).Ticks;
            foreach (var pair in file.Families)
            {
                if (pair.Value.LastSeen < horizon || _families.Count >= 65536) continue;
                _families[pair.Key] = new Family
                {
                    Hits = pair.Value.Hits, Wasted = pair.Value.Wasted,
                    WasteStreak = pair.Value.WasteStreak, Disabled = pair.Value.Disabled,
                    LastSeen = pair.Value.LastSeen
                };
            }
        }
        catch (IOException) { }
        catch (JsonException) { }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _stop.Cancel();
        Task[] running;
        lock (_gate)
        {
            ApplyFeedback();
            running = _inFlight.ToArray();
        }
        try { Task.WaitAll(running); } catch (AggregateException) { }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_modelPath)!);
            var file = new ModelFile();
            lock (_gate)
            {
                foreach (var pair in _families)
                    file.Families[pair.Key] = new FamilyFile
                    {
                        Hits = pair.Value.Hits, Wasted = pair.Value.Wasted,
                        WasteStreak = pair.Value.WasteStreak, Disabled = pair.Value.Disabled,
                        LastSeen = pair.Value.LastSeen
                    };
            }
            File.WriteAllText(_modelPath, JsonSerializer.Serialize(file));
        }
        catch (IOException) { }
        _stop.Dispose();
    }
}
