using System.Diagnostics;
using System.Globalization;
using System.Text;
using PolyMount.Core.Ipc;

namespace PolyMount.Tray;

/// <summary>Where a recording stands. One that ended keeps what it collected: the page can still
/// save it, and a service that comes back does not undo what was captured without it.</summary>
internal enum RecordingState
{
    Recording,
    /// <summary>The service could not be reached. The copy stays, and polling continues.</summary>
    Unreachable,
    /// <summary>Ended by the service: at its ceiling, or detached by something else.</summary>
    Stopped,
    /// <summary>The local copy reached its own ceiling; it is no longer the whole story.</summary>
    Truncated
}

/// <summary>What one poll produced, read by the page on the UI thread. The page reaches into the
/// session for nothing a drawing needs.</summary>
internal sealed record RecordingPulse(RecordingState State, string? Note, DateTime Time, double RequestMs,
    LayerSampler.Sample? Sample, IReadOnlyList<MountMetrics> Metrics, ServiceProcessReading? Process,
    long RequestBytes, long RequestLines, long ServiceBytes, long ServiceLines, long SampleRows,
    long SecondRows, long DroppedEvents, long CapacityBytes, int Restarts, IReadOnlyList<TailGap> Gaps,
    IReadOnlyList<LayerTotalRecord> Totals, string? Error);

/// <summary>
/// One recording session on the front end: it attaches the service's recording channel, copies
/// what that channel writes as it is written, samples the mount once a second, and hands the whole
/// of it to the exporter when the reader saves.
///
/// Everything that talks to the service happens here rather than in the page, so a recording keeps
/// running while its tab is not the one being looked at — which is most of the point of it.
/// </summary>
internal sealed class RecordingSession
{
    /// <summary>How often the session asks the service how it is doing. In between, the copy is
    /// driven by the files themselves, which is fresher and costs nothing.</summary>
    private const int StatusSeconds = 10;

    /// <summary>Where sessions keep their working copy until they are saved. One abandoned — a
    /// tray that was killed — leaves it behind, and the page clears out old ones.</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PolyMount", "recordings");

    /// <summary>One layer over the whole recording, accumulated as samples arrive so the summary
    /// never has to hold every row.</summary>
    private sealed class LayerAccumulator
    {
        public string DisplayName = "", Color = "";
        public bool Accelerating;
        public long Reads, Bytes, WriteBytes, Accelerated, Fetched;
        public double CostWeighted, MaxCost;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, LayerSampler.Counters> _wasteSteps = new();
    private readonly Dictionary<string, LayerAccumulator> _totals = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>One number per second for the whole recording. Four doubles a second is nothing,
    /// and a summary over the whole window has to be computed from something.</summary>
    private readonly List<double> _readsSeries = new(), _bytesSeries = new(), _costSeries = new(), _writeSeries = new();
    private readonly List<SecondRecord> _seconds = new();
    private readonly List<double> _overhead = new();
    private List<MountMetrics> _lastMetrics = new();
    private LayerSampler.Sample? _lastSample;
    private DateTime _lastStatusUtc;
    private string _serviceDirectory = "", _serviceLogPattern = "", _serviceSessionId = "";
    private long _serviceBytes, _serviceCapacityBytes, _droppedEvents;
    private int _restarts;
    private string? _note, _error;

    private RecordingSession(string mount, string workingDirectory)
    {
        Mount = mount;
        WorkingDirectory = workingDirectory;
        StartedUtc = DateTime.UtcNow;
        Requests = new RecordingTail(Path.Combine(workingDirectory, Sanitize(mount)));
        ServiceLog = new RecordingTail(Path.Combine(workingDirectory, "service")) { Prefix = "polymount-service" };
        Directory.CreateDirectory(Requests.Directory);
        Directory.CreateDirectory(ServiceLog.Directory);
        File.WriteAllText(Path.Combine(workingDirectory, "samples.csv"), StatsExport.SamplesCsvHeader, new UTF8Encoding(false));
    }

    public string Mount { get; }
    public DateTime StartedUtc { get; }
    public string WorkingDirectory { get; }
    public RecordingTail Requests { get; }
    public RecordingTail ServiceLog { get; }
    public RecordingState State { get; private set; } = RecordingState.Recording;
    public string? Note { get { lock (_gate) return _note; } }
    public string? ServiceSessionId { get { lock (_gate) return _serviceSessionId; } }
    public long SampleRows { get; private set; }

    /// <summary>Attaches the service's recording channel and starts a session. An instance that is
    /// already recording — a tray that was restarted, or another window — hands back the session
    /// it is running, which is then adopted: the service keeps every segment it wrote, so copying
    /// them from the start recovers the whole span rather than only the part after the restart.</summary>
    public static RecordingSession Start(string mount)
    {
        var response = ControlClient.RecordingStart(mount, "tray");
        if (!response.Ok) throw new InvalidOperationException(response.Error ?? "the recording could not be started");
        if (response.Recording is not { Active: true } info) throw new InvalidOperationException("the service started no recording");
        var session = new RecordingSession(mount, NewDirectory(mount));
        session.Adopt(info);
        // The service's own log is the context around the request channel, which is the evidence.
        try
        {
            var service = ControlClient.Logs().Logs.FirstOrDefault(log => log.Name.StartsWith("service/", StringComparison.Ordinal));
            if (service != null) session._serviceLogPattern = service.Source;
        }
        catch (Exception ex) { session._error = ex.Message; }
        return session;
    }

    /// <summary>One second of the recording: copy what is new, ask the mount what it served, and
    /// note what the machine running it was spending.</summary>
    public RecordingPulse Poll(DateTime utcNow)
    {
        var process = ServiceProcessReading.Now();
        double requestMs = 0;
        try
        {
            if (State == RecordingState.Recording && (utcNow - _lastStatusUtc).TotalSeconds >= StatusSeconds)
                RefreshStatus(utcNow);
            Drain(utcNow);
            var watch = Stopwatch.StartNew();
            var response = ControlClient.Metrics(1, Mount);
            watch.Stop();
            requestMs = watch.Elapsed.TotalMilliseconds;
            if (!response.Ok) throw new InvalidOperationException(response.Error);
            Accept(utcNow, response.Metrics, process, requestMs);
        }
        catch (Exception ex)
        {
            // A service that is down is recorded rather than treated as the end: the reader may be
            // waiting for it to come back, and the copy taken so far is still worth having.
            lock (_gate)
            {
                if (State == RecordingState.Recording)
                {
                    State = RecordingState.Unreachable;
                    _note = ex.Message;
                }
            }
            return Pulse(utcNow, requestMs, null, _lastMetrics, process, ex.Message);
        }
        return Pulse(utcNow, requestMs, _lastSample, _lastMetrics, process, null);
    }

    /// <summary>Asks the service how the recording is doing, and attaches it again if the instance
    /// was rebuilt or the service restarted. Both are recorded as a break rather than smoothed
    /// over: a bundle that claims to be continuous when it is not is worse than none.</summary>
    private void RefreshStatus(DateTime utcNow)
    {
        _lastStatusUtc = utcNow;
        ControlResponse response;
        try { response = ControlClient.RecordingStatus(Mount); }
        catch (Exception ex) { lock (_gate) _error = ex.Message; return; }
        if (!response.Ok || response.Recording is not { } info)
        {
            lock (_gate) _error = response.Error ?? "the service reported no recording";
            return;
        }
        if (info.Active && info.SessionId == ServiceSessionId)
        {
            Remember(info);
            return;
        }
        if (info.Active)
        {
            // A session started elsewhere; adopt it, keeping what is already copied. The two
            // sessions are different files, so the copy simply continues into new parts.
            Adopt(info);
            return;
        }
        // The session is gone: the instance was reloaded, the service restarted, or the channel
        // reached its ceiling. Nothing is discarded, a new session is attached, and the break is
        // written down.
        Requests.NoteBreak(utcNow, "the recording was attached again: " + (info.StoppedReason ?? "stopped"));
        lock (_gate) _restarts++;
        try
        {
            var restarted = ControlClient.RecordingStart(Mount, "tray");
            if (restarted.Recording is { Active: true } again) { Adopt(again); return; }
            lock (_gate)
            {
                State = RecordingState.Stopped;
                _note = info.StoppedReason ?? "stopped";
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                State = RecordingState.Unreachable;
                _note = ex.Message;
            }
        }
    }

    private void Adopt(RecordingSessionInfo info)
    {
        lock (_gate)
        {
            _serviceSessionId = info.SessionId;
            _serviceDirectory = info.Directory;
            if (State is RecordingState.Unreachable or RecordingState.Stopped) State = RecordingState.Recording;
        }
        Remember(info);
    }

    private void Remember(RecordingSessionInfo info)
    {
        lock (_gate)
        {
            _serviceBytes = info.Bytes;
            _serviceCapacityBytes = info.CapacityBytes;
            _droppedEvents = info.DroppedEvents;
        }
    }

    /// <summary>Copies whatever the service has written since the last poll. The sources are the
    /// paths the service itself named, matched as a family because Serilog rolls them.</summary>
    private void Drain(DateTime utcNow)
    {
        string directory, pattern;
        lock (_gate) { directory = _serviceDirectory; pattern = _serviceLogPattern; }
        if (directory.Length > 0) Requests.Drain(Family(directory, "events"), utcNow);
        if (pattern.Length > 0)
            ServiceLog.Drain(Family(Path.GetDirectoryName(pattern) ?? "", Path.GetFileNameWithoutExtension(pattern)), utcNow);
        if (Requests.Truncated)
            lock (_gate)
            {
                State = RecordingState.Truncated;
                _note = "the local copy reached its ceiling";
            }
    }

    /// <summary>Every segment of a rolled log, oldest first.</summary>
    private static string[] Family(string directory, string prefix)
    {
        try { return Directory.GetFiles(directory, prefix + "*.log").Order(StringComparer.Ordinal).ToArray(); }
        catch (DirectoryNotFoundException) { return Array.Empty<string>(); }
        catch (IOException) { return Array.Empty<string>(); }
    }

    private void Accept(DateTime utcNow, List<MountMetrics> metrics, ServiceProcessReading? process, double requestMs)
    {
        var sample = LayerSampler.Capture(utcNow, metrics, LayerSampler.Steps(utcNow, metrics, _wasteSteps, 3));
        double reads = sample.Layers.Values.Sum(layer => layer.Reads);
        double bytes = sample.Layers.Values.Sum(layer => layer.Bytes);
        double cost = sample.Layers.Values.Sum(layer => layer.Cost);
        double writes = sample.Layers.Values.Sum(layer => layer.WriteBytes);
        var csv = new StringBuilder();
        foreach (var (mount, layer) in Rows(metrics))
            StatsExport.AppendSampleRow(csv, new SampleRecord(utcNow, mount.Mount, layer.Name, layer.Hits, layer.Bytes,
                reads == 0 ? 0 : layer.AvgCostMs * layer.Hits / reads, layer.WriteBytes, 0, 0, 0, 0,
                layer.AvgCostMs, layer.MaxCostMs));
        // Appended as the recording runs: a long one never has to hold its own rows in memory.
        File.AppendAllText(Path.Combine(WorkingDirectory, "samples.csv"), csv.ToString(), new UTF8Encoding(false));
        SampleRows += metrics.Sum(m => m.Layers.Count);

        lock (_gate)
        {
            foreach (var (mount, layer) in Rows(metrics))
            {
                if (!_totals.TryGetValue(layer.Name, out var total)) _totals[layer.Name] = total = new LayerAccumulator();
                total.DisplayName = layer.DisplayName;
                total.Color = layer.Color;
                total.Accelerating |= layer.Accelerating;
                total.Reads += layer.Hits;
                total.Bytes += layer.Bytes;
                total.WriteBytes += layer.WriteBytes;
                total.CostWeighted += layer.AvgCostMs * layer.Hits;
                total.MaxCost = Math.Max(total.MaxCost, layer.MaxCostMs);
                total.Accelerated += layer.AcceleratedBytes;
                total.Fetched += layer.FetchedBytes;
            }
            _readsSeries.Add(reads);
            _bytesSeries.Add(bytes);
            _costSeries.Add(cost);
            _writeSeries.Add(writes);
            _overhead.Add(requestMs);
            _seconds.Add(new SecondRecord(utcNow, reads, bytes, cost, writes, requestMs, 0, 0,
                process?.CpuSeconds ?? 0, process?.WorkingSetBytes ?? 0, process?.PrivateBytes ?? 0,
                process?.Threads ?? 0, process?.Handles ?? 0));
            _lastSample = sample;
            _lastMetrics = metrics;
        }
    }

    private static IEnumerable<(MountMetrics Mount, LayerMetric Layer)> Rows(IEnumerable<MountMetrics> metrics) =>
        metrics.SelectMany(m => m.Layers.Select(l => (Mount: m, Layer: l)));

    private RecordingPulse Pulse(DateTime utcNow, double requestMs, LayerSampler.Sample? sample,
        IReadOnlyList<MountMetrics> metrics, ServiceProcessReading? process, string? error)
    {
        lock (_gate)
            return new RecordingPulse(State, _note, utcNow, requestMs, sample, metrics, process,
                Requests.Bytes, Requests.Lines, _serviceBytes, ServiceLog.Lines, SampleRows, _seconds.Count,
                _droppedEvents, _serviceCapacityBytes, _restarts, Requests.Gaps.ToArray(), Totals(), error);
    }

    private List<LayerTotalRecord> Totals() => _totals
        .OrderByDescending(pair => pair.Value.Reads)
        .Select(pair => new LayerTotalRecord(Mount, pair.Key, pair.Value.DisplayName, pair.Value.Color,
            pair.Value.Accelerating, pair.Value.Reads, pair.Value.Bytes, pair.Value.WriteBytes,
            pair.Value.Reads == 0 ? 0 : pair.Value.CostWeighted / pair.Value.Reads, pair.Value.MaxCost,
            pair.Value.Accelerated + pair.Value.Fetched == 0
                ? 0 : 100.0 * pair.Value.Accelerated / (pair.Value.Accelerated + pair.Value.Fetched),
            pair.Value.Accelerated))
        .ToList();

    /// <summary>Takes the recording down and freezes everything the bundle needs. Runs on the
    /// writer's thread; nothing else touches the session while it does.</summary>
    public RecordingSubmission Freeze(DateTime utcNow)
    {
        Drain(utcNow);
        RecordingSessionInfo? info = null;
        try { info = ControlClient.RecordingStop(Mount, "saved").Recording; }
        catch (Exception ex) { lock (_gate) _error = ex.Message; }
        // One last copy after the channel is detached: whatever it wrote between the last poll and
        // the stop belongs to the recording too.
        Drain(utcNow);
        if (info != null) Remember(info);
        lock (_gate)
        {
            State = RecordingState.Stopped;
            _note = "saved";
            return new RecordingSubmission(Mount, WorkingDirectory, Requests, ServiceLog,
                new ServiceChannelState(_serviceSessionId, _serviceDirectory, _serviceBytes,
                    _serviceCapacityBytes, _droppedEvents, _restarts),
                StartedUtc, utcNow, State.ToString(), _note,
                Totals(), _seconds.ToList(), _readsSeries.ToList(), _bytesSeries.ToList(),
                _costSeries.ToList(), _writeSeries.ToList(), _overhead.ToList(), SampleRows);
        }
    }

    /// <summary>Ends the recording without keeping it.</summary>
    public void Discard()
    {
        try { ControlClient.RecordingStop(Mount, "discarded"); }
        catch (Exception) { /* the service may already be gone; the copy is ours to delete */ }
        lock (_gate) State = RecordingState.Stopped;
        TryDelete(WorkingDirectory);
    }

    /// <summary>Detaches the service's channel and keeps the copy. Used when the tray is going
    /// away: the service must not be left writing a session no page will ever save.</summary>
    public void Suspend(string reason = "tray-exit")
    {
        if (State is RecordingState.Stopped or RecordingState.Truncated) return;
        try { ControlClient.RecordingStop(Mount, reason); }
        catch (Exception) { }
        lock (_gate) State = RecordingState.Stopped;
    }

    /// <summary>Clears working copies a tray never saved. A week is long enough to come back to a
    /// recording and short enough not to accumulate.</summary>
    public static void PruneWorkingCopies(TimeSpan age)
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (string directory in Directory.GetDirectories(Root))
                if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow - age) TryDelete(directory);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string NewDirectory(string mount)
    {
        string name = Sanitize(mount) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string directory = Path.Combine(Root, name);
        for (int suffix = 1; Directory.Exists(directory); suffix++)
            directory = Path.Combine(Root, name + "-" + suffix.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string Sanitize(string name)
    {
        var clean = new StringBuilder(name.Length);
        foreach (char c in name) clean.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0 ? c : '_');
        return clean.Length == 0 ? "mount" : clean.ToString();
    }
}

/// <summary>
/// The recordings this tray is holding. A recording is a session between the service and the page,
/// so closing the tray has to take the service's side down: otherwise the channel stays attached
/// and keeps writing a session that no page will ever collect.
/// </summary>
internal static class RecordingSessions
{
    private static readonly List<RecordingSession> Sessions = new();

    public static void Track(RecordingSession? session)
    {
        if (session == null) return;
        lock (Sessions) Sessions.Add(session);
    }

    public static void Forget(RecordingSession? session)
    {
        if (session == null) return;
        lock (Sessions) Sessions.Remove(session);
    }

    /// <summary>How many are running, for the status the page shows about the tray itself.</summary>
    public static int Count { get { lock (Sessions) return Sessions.Count; } }

    public static void SuspendAll()
    {
        RecordingSession[] sessions;
        lock (Sessions) sessions = Sessions.ToArray();
        Sessions.Clear();
        foreach (var session in sessions) session.Suspend();
    }
}
