using PolyMount.Core.Ipc;

namespace PolyMount.Tray;

/// <summary>What the service's recording channel reported about itself.</summary>
internal sealed record ServiceChannelState(string SessionId, string Directory, long Bytes, long CapacityBytes,
    long DroppedEvents, int Restarts);

/// <summary>
/// A recording, frozen: everything the bundle needs, copied off the session so the writing happens
/// without holding it. What the page shows afterwards and what the bundle says come from the same
/// moment — the one the reader pressed save on.
/// </summary>
internal sealed class RecordingSubmission
{
    public RecordingSubmission(string mount, string workingDirectory, string logs, RecordingTail requests,
        RecordingTail serviceLog, ServiceChannelState channel, DateTime startedUtc, DateTime endedUtc,
        string state, string? note, IReadOnlyList<LayerTotalRecord> totals, IReadOnlyList<SecondRecord> seconds,
        IReadOnlyList<double> reads, IReadOnlyList<double> bytes, IReadOnlyList<double> cost,
        IReadOnlyList<double> writes, IReadOnlyList<double> overhead, long sampleRows)
    {
        Mount = mount;
        WorkingDirectory = workingDirectory;
        Logs = logs;
        RequestBytes = requests.Bytes;
        RequestLines = requests.Lines;
        ServiceLogBytes = serviceLog.Bytes;
        ServiceLogLines = serviceLog.Lines;
        Sources = requests.Sources.Concat(serviceLog.Sources).ToList();
        Gaps = requests.Gaps.Concat(serviceLog.Gaps).ToList();
        Channel = channel;
        StartedUtc = startedUtc;
        EndedUtc = endedUtc;
        State = state;
        Note = note;
        Totals = totals;
        Seconds = seconds;
        Reads = reads;
        Bytes = bytes;
        Cost = cost;
        Writes = writes;
        Overhead = overhead;
        SampleRows = sampleRows;
    }

    public string Mount { get; }
    public string WorkingDirectory { get; }
    /// <summary>The subtree of the working copy that becomes the bundle's logs folder.</summary>
    public string Logs { get; }
    /// <summary>What the local copy of the request channel holds: the request evidence.</summary>
    public long RequestBytes { get; }
    public long RequestLines { get; }
    public long ServiceLogBytes { get; }
    public long ServiceLogLines { get; }
    public IReadOnlyList<TailedSource> Sources { get; }
    public IReadOnlyList<TailGap> Gaps { get; }
    public ServiceChannelState Channel { get; }
    public DateTime StartedUtc { get; }
    public DateTime EndedUtc { get; }
    public string State { get; }
    public string? Note { get; }
    public IReadOnlyList<LayerTotalRecord> Totals { get; }
    public IReadOnlyList<SecondRecord> Seconds { get; }
    public IReadOnlyList<double> Reads { get; }
    public IReadOnlyList<double> Bytes { get; }
    public IReadOnlyList<double> Cost { get; }
    public IReadOnlyList<double> Writes { get; }
    /// <summary>Round-trip cost of the page's own polling, one entry per second.</summary>
    public IReadOnlyList<double> Overhead { get; }
    public long SampleRows { get; }
    public double DurationSeconds => Math.Max(0, (EndedUtc - StartedUtc).TotalSeconds);

    /// <summary>The frozen recording as a bundle. The charts describe the whole recording rather
    /// than the last minute: the page's charts are a live view, but the bundle is the record.</summary>
    public StatsSnapshot ToSnapshot(string language) => new()
    {
        Scope = Mount,
        Language = language,
        Kind = "recording",
        CapturedUtc = EndedUtc,
        // The window is the recording's own length, so every reader that computed a window from
        // this figure now computes the span of the session — with nothing new to know.
        WindowSeconds = (int)Math.Round(DurationSeconds),
        Charts = new List<ChartRecord>
        {
            new("stats.reads", "/s", ChartStatistics.Of(Reads)),
            new("stats.layerBytes", "B", ChartStatistics.Of(Bytes)),
            new("stats.latency", "ms", ChartStatistics.Of(Cost)),
            new("stats.bytes", "B", ChartStatistics.Of(Writes))
        },
        Seconds = Seconds.ToList(),
        SamplesFile = Path.Combine(WorkingDirectory, "samples.csv"),
        LogsDirectory = Logs,
        // The recording's own polling cost, one reading per second. It is the whole of what a
        // recording can say about the front end: the page's dispatch and render costs are the
        // performance window's subject, and it measures them over its own 60 second window.
        ResponseRequest = Overhead.ToList(),
        Recording = new RecordingRecord
        {
            SessionId = Channel.SessionId,
            Label = Mount,
            StartedUtc = StartedUtc.ToString("O"),
            EndedUtc = EndedUtc.ToString("O"),
            StoppedReason = Note ?? State,
            Seconds = DurationSeconds,
            CapturedBytes = RequestBytes,
            CapturedLines = RequestLines,
            SampleRows = SampleRows,
            SampleTicks = Seconds.Count,
            DroppedEvents = Channel.DroppedEvents,
            Truncated = State == nameof(RecordingState.Truncated),
            Complete = Gaps.Count == 0 && State != nameof(RecordingState.Truncated) && Channel.DroppedEvents == 0,
            ServiceDirectory = Channel.Directory,
            Restarts = Channel.Restarts,
            Sources = Sources.ToList(),
            Gaps = Gaps.ToList(),
            Layers = Totals.ToList()
        }
    };
}
