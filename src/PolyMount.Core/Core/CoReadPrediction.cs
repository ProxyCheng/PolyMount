using System.Collections.Concurrent;
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
    /// <summary>Distinct other paths a burst needs before its first path is even considered a
    /// trigger. A burst of one file beside one other is a pair, not a set: the measurement that
    /// motivated this rule found the first path of a concurrent burst is usually a payload, and
    /// a payload does not bring a batch.</summary>
    public int MinBurstMembers { get; set; } = 2;
    /// <summary>How much of the previous burst's set must reappear (Jaccard) before the repeat
    /// counts. A trigger whose set is different every time is the noisy case: it never
    /// accumulates the repeats that would let it preload.</summary>
    public double MinSetOverlap { get; set; } = 0.6;
    /// <summary>Repeated, consistent bursts a trigger needs before its members may be
    /// preloaded. Zero would act on a single observation — the behaviour this replaces.</summary>
    public int MinConfirmations { get; set; } = 1;
    /// <summary>Files one trigger may preload in a batch. The set is ranked by how often each
    /// member took part, so an over-budget set yields its best members, not all of them.</summary>
    public int MaxBatchFiles { get; set; } = 8;
    /// <summary>Bytes read-ahead may admit in one tick, across every queued trigger.</summary>
    public long MaxBatchBytes { get; set; } = 16L * 1024 * 1024;
    /// <summary>Unread preloads in a row that close a group. The layer reports back per
    /// preloaded entry, so a group that guesses what nobody reads stops asking while the other
    /// groups (and the other strategies) keep their own confidence.</summary>
    public int GroupWasteStreak { get; set; } = 16;
    public int MaxInFlight { get; set; } = 2;
    public long MaxFileBytes { get; set; } = 8L * 1024 * 1024;
    public int FeedbackWindowMs { get; set; } = 5000;
    public int FeedbackSamples { get; set; } = 32;
    public int RetentionMs { get; set; } = 3600000;
}

/// <summary>
/// Co-read / dependency-burst predictor. The successor table asks "which single file comes
/// next"; a parallel loader does not answer that. This model records the set of paths read
/// close together in time by one process and, when the first path of such a burst is read
/// again (usually a manifest or an index), asks the layer to preload the other members.
/// <para>
/// It is deliberately cross-thread: a loader commonly reads the manifest on one thread and the
/// payloads on several IO threads, so the burst is grouped by process, not by caller stream.
/// The layer around it owns the held cache, quota, throttle and write invalidation; this model
/// only learns sets and enqueues preloads.
/// </para>
/// <para>
/// A single burst is not evidence. A trigger becomes a preload source only after the same set
/// repeats (<see cref="CoReadOptions.MinSetOverlap"/>), which is what separates a manifest that
/// brings its payloads from a payload that merely happened to be read first in a burst of
/// concurrent loads. Each trigger also carries its own hit/waste count: the layer reports the
/// fate of every entry this model nominated, and a group whose preloads keep going unread is
/// closed without touching the others.
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

    /// <summary>What one trigger path has taught. <see cref="LastSet"/> is the previous burst's
    /// follower set (the overlap test), <see cref="Members"/> counts followers over consistent
    /// observations, and <see cref="Observations"/> is the confidence denominator.</summary>
    private sealed class Group
    {
        public readonly Dictionary<string, int> Members = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> LastSet = new(StringComparer.OrdinalIgnoreCase);
        public int Observations;
        public int Stable;
        public int Hits;
        public int Wasted;
        public int WasteStreak;
        public bool Disabled;
        public long LastSeen;
    }

    private sealed class GroupFile
    {
        public string Trigger { get; set; } = "";
        public Dictionary<string, int> Members { get; set; } = new();
        public List<string> LastSet { get; set; } = new();
        public int Observations { get; set; }
        public int Stable { get; set; }
        public int Hits { get; set; }
        public int Wasted { get; set; }
        public int WasteStreak { get; set; }
        public bool Disabled { get; set; }
        public long LastSeen { get; set; }
    }

    private sealed class ModelFile
    {
        public int Version { get; set; } = 2;
        public List<GroupFile> Groups { get; set; } = new();
    }

    private readonly VirtualFileSystem _vfs;
    private readonly CoReadOptions _options;
    private readonly string _modelPath;
    private readonly PreloadRequest? _preload;
    private readonly object _gate = new();
    private readonly Dictionary<int, Burst> _bursts = new();
    private readonly Dictionary<string, Group> _groups = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Outcome reports from the layer. They are queued rather than applied so the layer
    /// can report from inside its own lock without ever taking this model's lock there.</summary>
    private readonly ConcurrentQueue<(string Group, bool Paid)> _feedback = new();
    private readonly Dictionary<long, double> _throttle = new();
    private readonly Queue<(string Path, long Stream, string Group)> _queue = new();
    private readonly HashSet<(string Path, long Stream)> _queued = new();
    private readonly List<Task> _inFlight = new();
    private readonly Timer _timer;
    private readonly CancellationTokenSource _stop = new();
    private long _epoch;
    private long _memberCount;
    public long BurstPredictions { get; private set; }
    public long BurstCompleted { get; private set; }

    public CoReadPrediction(VirtualFileSystem vfs, CoReadOptions options, string modelPath,
        PreloadRequest? preload = null)
    {
        if (options.TickIntervalMs is < 10 or > 10000 || options.BurstGapMs is < 0 or > 60000 ||
            options.BurstWindowMs is < 1 or > 600000 || options.MaxPathsPerBurst is < 2 or > 4096 ||
            options.MaxLinks is < 1 or > 1_048_576 || options.MaxCoReadsPerPath is < 1 or > 64 ||
            options.MinCoReadCount is < 1 or > 10000 || options.MinConfidence is < 0 or > 1 ||
            options.MinBurstMembers is < 1 or > 4096 || options.MinSetOverlap is < 0 or > 1 ||
            options.MinConfirmations is < 0 or > 1000 || options.MaxBatchFiles is < 1 or > 4096 ||
            options.MaxBatchBytes is <= 0 or > 1024L * 1024 * 1024 ||
            options.GroupWasteStreak is < 1 or > 1_000_000 ||
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
            EnqueueMembers(path, streamId);
        }
        return null;
    }

    /// <summary>Associates a burst's first path with the set that followed it. One burst is a
    /// sample, not a rule: the set is only counted when it reproduces the previous one, so a
    /// path that just happened to be read first in a burst of concurrent loads never becomes a
    /// trigger.</summary>
    private void Close(Burst burst, long now)
    {
        if (burst.First is not { } trigger) return;
        if (burst.Paths.Count < Math.Max(2, _options.MinBurstMembers + 1)) return;
        var observed = new List<string>(Math.Min(burst.Paths.Count, _options.MaxPathsPerBurst));
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in burst.Paths)
            if (!string.Equals(path, trigger, StringComparison.OrdinalIgnoreCase) && distinct.Add(path))
                observed.Add(path);
        if (observed.Count < _options.MinBurstMembers) return;

        if (!_groups.TryGetValue(trigger, out var group))
        {
            while (_groups.Count >= _options.MaxLinks) EvictOldest();
            group = new Group();
            _groups[trigger] = group;
        }
        group.LastSeen = now;

        if (group.Observations > 0 && Overlap(observed, group.LastSet) >= _options.MinSetOverlap)
        {
            group.Observations++;
            foreach (var path in observed)
            {
                if (!group.Members.ContainsKey(path)) _memberCount++;
                group.Members[path] = group.Members.GetValueOrDefault(path) + 1;
            }
            group.LastSet.Clear();
            foreach (var path in observed) group.LastSet.Add(path);
            group.Stable = Math.Min(group.Stable + 1, 1_000_000);
            TrimMembers(group);
        }
        else
        {
            // First observation, or a set that did not reproduce. Either way this burst is the
            // only evidence there is: start the count here, so an unstable trigger stays below
            // the confirmation bar instead of accumulating unrelated bursts into confidence.
            _memberCount -= group.Members.Count;
            group.Members.Clear();
            foreach (var path in observed) group.Members[path] = 1;
            _memberCount += group.Members.Count;
            group.LastSet.Clear();
            foreach (var path in observed) group.LastSet.Add(path);
            group.Observations = 1;
            group.Stable = 0;
            // A contradicting set is new evidence: it re-arms a group that feedback closed,
            // because that verdict was about the old set.
            group.Disabled = false;
            group.Hits = 0;
            group.Wasted = 0;
            group.WasteStreak = 0;
        }
        while (_memberCount > _options.MaxLinks) EvictOldest();
    }

    /// <summary>Jaccard overlap of the new follower set with the previous one.</summary>
    private static double Overlap(List<string> observed, HashSet<string> previous)
    {
        if (previous.Count == 0 || observed.Count == 0) return 0;
        int shared = 0;
        var current = new HashSet<string>(observed, StringComparer.OrdinalIgnoreCase);
        foreach (var path in previous)
            if (current.Contains(path)) shared++;
        int union = current.Count + previous.Count - shared;
        return union == 0 ? 0 : (double)shared / union;
    }

    private void TrimMembers(Group group)
    {
        while (group.Members.Count > _options.MaxCoReadsPerPath)
        {
            var weakest = group.Members.MinBy(pair => pair.Value).Key;
            group.Members.Remove(weakest);
            _memberCount--;
        }
    }

    private void EvictOldest()
    {
        if (_groups.Count == 0) { _memberCount = 0; return; }
        var oldest = _groups.MinBy(pair => pair.Value.LastSeen).Key;
        if (!_groups.Remove(oldest, out var removed)) return;
        _memberCount -= removed.Members.Count;
    }

    /// <summary>The members a confirmed trigger may preload, best first. A member needs enough
    /// repeats of its own (<see cref="CoReadOptions.MinCoReadCount"/>) and a share of the
    /// trigger's observations (<see cref="CoReadOptions.MinConfidence"/>), so a set that is
    /// only half the same each time contributes its stable half.</summary>
    private void EnqueueMembers(string trigger, long stream)
    {
        if (!_groups.TryGetValue(trigger, out var group) || group.Disabled) return;
        if (group.Observations <= 0 || group.Stable < _options.MinConfirmations) return;
        int limit = Math.Min(_options.MaxCoReadsPerPath, _options.MaxBatchFiles);
        foreach (var member in group.Members
                     .Where(pair => pair.Value >= _options.MinCoReadCount &&
                                    (double)pair.Value / group.Observations >= _options.MinConfidence &&
                                    !string.Equals(pair.Key, trigger, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(pair => pair.Value)
                     .Take(limit))
            Enqueue(member.Key, stream, trigger);
    }

    private void Enqueue(string path, long stream, string group)
    {
        if (_queue.Count >= 256 || !_queued.Add((path, stream))) return;
        _queue.Enqueue((path, stream, group));
    }

    private void Tick()
    {
        lock (_gate)
        {
            ApplyFeedback();
            _inFlight.RemoveAll(task => task.IsCompleted);
            if (_stop.IsCancellationRequested || _queue.Count == 0) return;
            long budget = _options.MaxBatchBytes;
            while (_queue.Count > 0 && _inFlight.Count < _options.MaxInFlight * 4)
            {
                var (path, stream, group) = _queue.Peek();
                // A group feedback closed stops instructing at once; its queued members are
                // dropped rather than fetched for a rule that has been contradicted.
                if (!_groups.TryGetValue(group, out var state) || state.Disabled)
                {
                    _queue.Dequeue();
                    _queued.Remove((path, stream));
                    continue;
                }
                double throttle = _throttle.GetValueOrDefault(stream, 1);
                if (throttle <= 0)
                {
                    _queue.Dequeue();
                    _queued.Remove((path, stream));
                    continue;
                }
                if (_inFlight.Count >= Math.Max(1, (int)Math.Round(_options.MaxInFlight * throttle))) break;
                if (!_vfs.TryLookup(path, out var entry) || entry.IsDir || entry.Size > _options.MaxFileBytes)
                {
                    _queue.Dequeue();
                    _queued.Remove((path, stream));
                    continue;
                }
                if (entry.Size > budget) break;
                _queue.Dequeue();
                _queued.Remove((path, stream));
                budget -= entry.Size;
                long epoch = _epoch;
                using (ExecutionContext.SuppressFlow()) _inFlight.Add(Task.Run(() =>
                {
                    using var lane = ReadPriority.Enter(ReadPriority.Speculative);
                    try
                    {
                        if (epoch != _epoch) return;
                        using var trace = RequestDiagnostics.Begin("Predict.Burst", path, _vfs.Diagnostics);
                        bool fetched;
                        if (_preload != null) fetched = _preload(path, stream, PreloadOrigin.Burst, group);
                        else { using var read = _vfs.OpenRead(path, out _, speculative: true); fetched = true; }
                        // A member the layer already holds is not a preload: it was never
                        // fetched, so counting it would overstate what the model is doing.
                        if (!fetched) return;
                        lock (_gate) if (epoch == _epoch) { BurstPredictions++; BurstCompleted++; }
                    }
                    catch (IOException) { }
                    catch (Exception ex) { using var trace = RequestDiagnostics.Begin("Predict.Failed", path, _vfs.Diagnostics); trace?.Stage(ex.GetType().Name); }
                }));
            }
        }
    }

    /// <summary>Applies what the layer reported about this model's guesses. A run of entries
    /// nobody read closes the group they came from; one read entry resets the run, because the
    /// rule is still finding something.</summary>
    private void ApplyFeedback()
    {
        while (_feedback.TryDequeue(out var report))
        {
            if (!_groups.TryGetValue(report.Group, out var group)) continue;
            if (report.Paid)
            {
                group.Hits++;
                group.WasteStreak = 0;
            }
            else
            {
                group.Wasted++;
                if (++group.WasteStreak >= _options.GroupWasteStreak) group.Disabled = true;
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
                int confirmed = 0, closed = 0;
                foreach (var group in _groups.Values)
                {
                    if (group.Disabled) closed++;
                    else if (group.Observations > 0 && group.Stable >= _options.MinConfirmations) confirmed++;
                }
                return new PredictionModelStats(_memberCount, BurstPredictions, BurstCompleted,
                    0, 0, 0, 0, BurstPredictions, BurstCompleted, _bursts.Count, 0, confirmed, closed);
            }
        }
    }

    /// <summary>The layer reports the fate of one entry this model nominated: read before it was
    /// evicted, or evicted unread.</summary>
    public void ReportGroup(string group, bool paid) => _feedback.Enqueue((group, paid));

    public void SetThrottle(long streamId, double value) { lock (_gate) _throttle[streamId] = Math.Clamp(value, 0, 1); }
    public void SetSpatialThrottle(long streamId, double value) { }
    public void SetBurstThrottle(long streamId, double value) { lock (_gate) _throttle[streamId] = Math.Clamp(value, 0, 1); }

    public void Reset()
    {
        lock (_gate)
        {
            _epoch++;
            _bursts.Clear();
            _groups.Clear();
            _memberCount = 0;
            _feedback.Clear();
            _queue.Clear();
            _queued.Clear();
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
            // Version 1 held one link table per trigger, learned from single bursts; those were
            // the noisy associations this version replaces, so they are relearned rather than
            // migrated.
            if (file is not { Version: 2 } || file.Groups == null) return;
            long horizon = _options.RetentionMs <= 0 ? long.MinValue
                : DateTime.UtcNow.Ticks - TimeSpan.FromMilliseconds(_options.RetentionMs).Ticks;
            foreach (var item in file.Groups)
            {
                if (string.IsNullOrEmpty(item.Trigger) || item.LastSeen < horizon) continue;
                if (_groups.Count >= _options.MaxLinks) break;
                var group = new Group
                {
                    Observations = Math.Max(0, item.Observations),
                    Stable = Math.Max(0, item.Stable),
                    Hits = item.Hits, Wasted = item.Wasted, WasteStreak = item.WasteStreak,
                    Disabled = item.Disabled, LastSeen = item.LastSeen
                };
                if (item.Members != null)
                    foreach (var pair in item.Members)
                    {
                        if (pair.Value <= 0 || group.Members.Count >= _options.MaxCoReadsPerPath) continue;
                        group.Members[pair.Key] = pair.Value;
                    }
                if (item.LastSet != null)
                    foreach (var path in item.LastSet)
                        if (group.LastSet.Count < _options.MaxPathsPerBurst) group.LastSet.Add(path);
                if (group.Members.Count == 0) continue;
                _groups[item.Trigger] = group;
                _memberCount += group.Members.Count;
            }
            while (_memberCount > _options.MaxLinks) EvictOldest();
        }
        catch (IOException) { }
        catch (JsonException) { }
    }

    public void Dispose()
    {
        _timer.Dispose(); _stop.Cancel();
        Task[] running;
        lock (_gate)
        {
            // A burst still open at shutdown is evidence too; closing it here is what lets a
            // short run teach the next one.
            foreach (var burst in _bursts.Values.ToArray()) Close(burst, DateTime.UtcNow.Ticks);
            _bursts.Clear();
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
                foreach (var pair in _groups)
                {
                    file.Groups.Add(new GroupFile
                    {
                        Trigger = pair.Key,
                        Members = new Dictionary<string, int>(pair.Value.Members, StringComparer.OrdinalIgnoreCase),
                        LastSet = pair.Value.LastSet.ToList(),
                        Observations = pair.Value.Observations,
                        Stable = pair.Value.Stable,
                        Hits = pair.Value.Hits,
                        Wasted = pair.Value.Wasted,
                        WasteStreak = pair.Value.WasteStreak,
                        Disabled = pair.Value.Disabled,
                        LastSeen = pair.Value.LastSeen
                    });
                }
            }
            File.WriteAllText(_modelPath, JsonSerializer.Serialize(file));
        }
        catch (IOException) { }
        _stop.Dispose();
    }
}
