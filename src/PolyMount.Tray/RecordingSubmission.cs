using PolyMount.Core.Ipc;

namespace PolyMount.Tray;

/// <summary>What the service's recording counted, and how the session went.</summary>
internal sealed record ServiceChannelState(string SessionId, RecordingCounts? Counts, int Restarts);

/// <summary>
/// A recording, frozen: everything the bundle needs, copied off the session so the writing happens
/// without holding it. What the page shows afterwards and what the bundle says come from the same
/// moment — the one the reader pressed save on.
/// </summary>
internal sealed class RecordingSubmission
{
    public RecordingSubmission(string mount, string workingDirectory, string logs, RecordingTail requests,
        RecordingTail serviceLog, RecordingTail instanceLog, ServiceChannelState channel, DateTime startedUtc, DateTime endedUtc,
        string state, string? note, IReadOnlyList<LayerTotalRecord> totals, IReadOnlyList<SecondRecord> seconds,
        IReadOnlyList<double> reads, IReadOnlyList<double> bytes, IReadOnlyList<double> cost,
        IReadOnlyList<double> writes, IReadOnlyList<double> writeCounts, IReadOnlyList<double> writeCosts,
        IReadOnlyList<double> overhead, long sampleRows)
    {
        Mount = mount;
        WorkingDirectory = workingDirectory;
        Logs = logs;
        RequestBytes = requests.Bytes;
        RequestLines = requests.Lines;
        ServiceLogBytes = serviceLog.Bytes;
        ServiceLogLines = serviceLog.Lines;
        InstanceLogBytes = instanceLog.Bytes;
        InstanceLogLines = instanceLog.Lines;
        Sources = requests.Sources.Concat(instanceLog.Sources).Concat(serviceLog.Sources).ToList();
        Gaps = requests.Gaps.Concat(instanceLog.Gaps).Concat(serviceLog.Gaps).ToList();
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
        WriteCounts = writeCounts;
        WriteCosts = writeCosts;
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
    /// <summary>What the instance's own process log contributed: its story of the same window, as
    /// opposed to the service's story of it, which the next two are.</summary>
    public long InstanceLogBytes { get; }
    public long InstanceLogLines { get; }
    public IReadOnlyList<TailedSource> Sources { get; }
    public IReadOnlyList<TailGap> Gaps { get; }
    public ServiceChannelState Channel { get; }

    /// <summary>The exact counts: every request the instance served in the window, whatever the
    /// sampled detail chose to write a line about.</summary>
    public RecordingCounts? Counts => Channel.Counts;
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
    /// <summary>How many writes the layers applied per second, and what one of them cost: the two
    /// figures the page's write cards are drawn from, kept so the bundle describes the same six
    /// charts the page shows.</summary>
    public IReadOnlyList<double> WriteCounts { get; }
    public IReadOnlyList<double> WriteCosts { get; }
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
            new("stats.writes", "/s", ChartStatistics.Of(WriteCounts)),
            new("stats.readBytes", "B", ChartStatistics.Of(Bytes)),
            new("stats.writeBytes", "B", ChartStatistics.Of(Writes)),
            new("stats.readLatency", "ms", ChartStatistics.Of(Cost)),
            new("stats.writeLatency", "ms", ChartStatistics.Of(WriteCosts))
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
            Truncated = State == nameof(RecordingState.Truncated),
            // What completeness means here: the per-request detail is a sample by rule — which is
            // not a defect — and the counts are exact by construction, so the only thing that can
            // be missing is a stretch of the copy itself.
            Complete = Gaps.Count == 0 && State != nameof(RecordingState.Truncated),
            Counts = Channel.Counts,
            Restarts = Channel.Restarts,
            Sources = Sources.ToList(),
            Gaps = Gaps.ToList(),
            Layers = Totals.ToList()
        }
    };
}
