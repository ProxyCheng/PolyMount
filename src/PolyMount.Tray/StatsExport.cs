using System.Globalization;
using System.Text;
using System.Text.Json;
using PolyMount.Core.Ipc;

namespace PolyMount.Tray;

/// <summary>A frozen performance window: the numbers on screen, the per-second records behind
/// them, and the front-end overhead they were measured with. Plain records, so the exporter
/// needs no access to the window itself.
///
/// A recording fills the same shape from a longer window, and adds the parts only a recording
/// has: how long it ran, what it captured and where, and a per-second account of the machine.
/// Everything the performance window wrote before is written exactly as it was.</summary>
internal sealed class StatsSnapshot
{
    /// <summary>The instance the window was showing, or empty for every mount.</summary>
    public string Scope { get; init; } = "";
    public string Language { get; init; } = "";
    /// <summary>Whether the window happened to be paused. The export is a snapshot either way:
    /// its numbers come from <see cref="CapturedUtc"/>, not from when the files were written.</summary>
    public bool Frozen { get; init; }
    /// <summary>When the state was copied off the window — the moment the export describes.
    /// The files are written later, and the window keeps sampling behind them.</summary>
    public DateTime CapturedUtc { get; init; }
    public int WindowSeconds { get; init; }
    /// <summary>"snapshot" for the performance window, "recording" for a captured session.</summary>
    public string Kind { get; init; } = "snapshot";
    /// <summary>What each chart's header showed, in the order the charts are laid out.</summary>
    public List<ChartRecord> Charts { get; init; } = new();
    /// <summary>The per-layer analytics the window displayed, as reported by the service.</summary>
    public List<MountMetrics> Metrics { get; init; } = new();
    public List<SampleRecord> Samples { get; init; } = new();
    public List<double> ResponseRequest { get; init; } = new();
    public List<double> ResponseDispatch { get; init; } = new();
    public List<double> ResponseUi { get; init; } = new();
    /// <summary>A session's own account of itself; null for the performance window.</summary>
    public RecordingRecord? Recording { get; init; }
    /// <summary>One row per second across the whole window, written as <c>seconds.csv</c> when
    /// there are any. Only a recording collects them: the performance window samples the service
    /// process once, at the moment it is captured.</summary>
    public List<SecondRecord> Seconds { get; init; } = new();
    /// <summary>The session's own <c>samples.csv</c>, written as the recording ran. Carried
    /// rather than rebuilt, so a long recording never has to hold every row in memory.</summary>
    public string? SamplesFile { get; init; }
    /// <summary>Logs the front-end collected itself, already laid out as the bundle wants them
    /// (a recording copies its log as it goes, because the service's own files roll away).
    /// Empty means: ask the service and copy what it points at.</summary>
    public string? LogsDirectory { get; init; }
}

/// <summary>What a recording was: how long it ran, what it captured, where, and every stretch it
/// knows is missing. A bundle that cannot say this cannot be trusted about the rest.</summary>
internal sealed class RecordingRecord
{
    public string SessionId { get; init; } = "";
    public string Label { get; init; } = "";
    public string StartedUtc { get; init; } = "";
    public string EndedUtc { get; init; } = "";
    /// <summary>Why the session ended: saved / capacity / stopped / tray-exit …</summary>
    public string StoppedReason { get; init; } = "";
    public double Seconds { get; init; }
    public long CapturedBytes { get; init; }
    public long CapturedLines { get; init; }
    public long SampleRows { get; init; }
    public long DroppedEvents { get; init; }
    /// <summary>The recording stopped at its own ceiling, so it is not the whole story.</summary>
    public bool Truncated { get; init; }
    /// <summary>Where the service wrote the session the front-end read from.</summary>
    public string ServiceDirectory { get; init; } = "";
    public string ServiceInstanceId { get; init; } = "";
    /// <summary>How many times the service vanished and the channel had to be attached again.</summary>
    public int Restarts { get; init; }
    public List<TailedSource> Sources { get; init; } = new();
    public List<TailGap> Gaps { get; init; } = new();
    /// <summary>Per layer over the whole recording, summed by the front-end from its own samples:
    /// the service can only report a window its collector still holds, and a recording outlives
    /// that. This is the block to read when the bundle says it is a recording.</summary>
    public List<LayerTotalRecord> Layers { get; init; } = new();
}

/// <summary>One layer over a whole recording.</summary>
internal sealed record LayerTotalRecord(string Mount, string Layer, string DisplayName, string Color,
    bool Accelerating, long Reads, long Bytes, long WriteBytes, double AvgCostMs, double MaxCostMs,
    double AcceleratedPercent, long AcceleratedBytes);

/// <summary>One second of a recording as the machine running it saw it.</summary>
internal readonly record struct SecondRecord(DateTime Time, double Reads, double Bytes, double Cost,
    double WriteBytes, double RequestMs, double DispatchMs, double UiMs, double CpuSeconds,
    long WorkingSetBytes, long PrivateBytes, int Threads, int Handles);

/// <summary>One chart's headline and summary figures.</summary>
internal readonly record struct ChartRecord(string Chart, string Unit, ChartStatistics Statistics);

/// <summary>One layer's contribution to one second of the window.</summary>
internal readonly record struct SampleRecord(DateTime Time, string Mount, string Layer,
    double Reads, double Bytes, double Cost, double WriteBytes, double Wasted, double WastedBytes,
    double Accelerated, double Fetched, double AvgCost = 0, double MaxCost = 0);

/// <summary>What an export produced, for the confirmation the window shows.</summary>
internal readonly record struct ExportOutcome(string Directory, int Files, int Samples, int Logs);

/// <summary>One service log copied into the bundle, and where it landed.</summary>
internal readonly record struct LogFileCopy(string Name, string Target, long Bytes, string? Error);

/// <summary>Writes a frozen window into a timestamped folder: a summary, the raw records and the
/// service's logs. JSON and CSV so the bundle needs no PolyMount to be read.</summary>
internal static class StatsExport
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PolyMount", "export.json");

    /// <summary>Where exports go until the reader picks somewhere else.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PolyMount Exports");

    public static ExportOutcome Write(string parent, StatsSnapshot snapshot)
    {
        string directory = Path.Combine(parent, "PolyMount-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        var (service, mounts, logs) = ServiceData();
        // A recording brings its own log copies: it read them while the service was writing, so
        // they hold the beginning of the window even if the service's files have rolled since.
        var copies = snapshot.LogsDirectory is { Length: > 0 } collected
            ? CollectDirectory(directory, collected)
            : CopyLogs(directory, logs);
        long rows = snapshot.SamplesFile is { Length: > 0 } written
            ? CarrySamples(directory, written)
            : WriteSamples(directory, snapshot.Samples);
        bool seconds = snapshot.Seconds.Count > 0;
        if (seconds) File.WriteAllText(Path.Combine(directory, "seconds.csv"), SecondsCsv(snapshot.Seconds), Utf8);
        // The digest is built from the bundle's own copies, so it describes exactly the files
        // the reader has — including however far the logs had rolled by the time they were read.
        var digest = RequestLogDigest.Build(copies, WindowStart(snapshot), ToOffset(snapshot.CapturedUtc), snapshot.Kind);
        File.WriteAllText(Path.Combine(directory, "requests-summary.json"), digest.Json, Utf8);
        File.WriteAllText(Path.Combine(directory, "summary.json"),
            SummaryJson(snapshot, service, mounts, copies, digest, rows), Utf8);
        return new(directory, 3 + (seconds ? 1 : 0) + copies.Count(copy => copy.Error == null),
            (int)rows, copies.Count);
    }

    /// <summary>When the window the bundle describes began: for a snapshot that is its length
    /// back from the moment it was captured, and for a recording the moment it started, which is
    /// what makes the digest cover the whole session.</summary>
    private static DateTimeOffset WindowStart(StatsSnapshot snapshot) =>
        snapshot.Recording is { StartedUtc.Length: > 0 } recording &&
        DateTimeOffset.TryParse(recording.StartedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started)
            ? started
            : ToOffset(snapshot.CapturedUtc) - TimeSpan.FromSeconds(snapshot.WindowSeconds);

    private static DateTimeOffset ToOffset(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    public static string LoadDirectory()
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            string? directory = json.RootElement.GetProperty("Directory").GetString();
            return !string.IsNullOrEmpty(directory) && Directory.Exists(directory) ? directory : DefaultDirectory;
        }
        catch { return DefaultDirectory; }
    }

    public static void SaveDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { Directory = directory }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The service's own view at export time. A stopped service is recorded rather than
    /// treated as a failure: the window still holds its records, and they are the point.</summary>
    private static ((bool Reachable, string? Error) Service, List<MountStatus> Mounts, List<LogFile> Logs) ServiceData()
    {
        try
        {
            return ((true, null), ControlClient.Status().Mounts, ControlClient.Logs().Logs);
        }
        catch (Exception ex)
        {
            return ((false, ex.Message), new List<MountStatus>(), new List<LogFile>());
        }
    }

    /// <summary>Copies each log the service pointed at into the bundle's logs folder. A log that
    /// has rolled away, or one this account may not read, is reported instead of aborting.</summary>
    private static List<LogFileCopy> CopyLogs(string directory, List<LogFile> logs)
    {
        var copies = new List<LogFileCopy>();
        foreach (var log in logs)
        {
            string target = Path.Combine(directory, "logs", log.Name.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(log.Source, target, true);
                copies.Add(new(log.Name, target, new FileInfo(target).Length, null));
            }
            catch (Exception ex)
            {
                copies.Add(new(log.Name, target, 0, ex.Message));
            }
        }
        return copies;
    }

    /// <summary>Moves the copies a recording made as it ran into the bundle. They are already in
    /// the layout the bundle wants, so this is a move rather than a second copy of something that
    /// can be hundreds of megabytes.</summary>
    private static List<LogFileCopy> CollectDirectory(string directory, string collected)
    {
        var copies = new List<LogFileCopy>();
        string root = Path.Combine(directory, "logs");
        var sources = Directory.Exists(collected)
            ? Directory.GetFiles(collected, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        foreach (string source in sources)
        {
            string name = Path.GetRelativePath(collected, source).Replace(Path.DirectorySeparatorChar, '/');
            string target = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(source, target, true);
                copies.Add(new(name, target, new FileInfo(target).Length, null));
            }
            catch (Exception ex)
            {
                copies.Add(new(name, target, 0, ex.Message));
            }
        }
        return copies;
    }

    private static string SummaryJson(StatsSnapshot snapshot, (bool Reachable, string? Error) service,
        List<MountStatus> mounts, List<LogFileCopy> logs, RequestDigest digest, long rows)
    {
        var coverage = digest.Coverage
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var document = new
        {
            export = new
            {
                // What this bundle is. A reader that only knows the snapshot keeps working: every
                // key it used before is still here and still means the same thing.
                kind = snapshot.Kind,
                capturedLocal = snapshot.CapturedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
                capturedUtc = snapshot.CapturedUtc.ToString("O", CultureInfo.InvariantCulture),
                writtenLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
                writtenUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                machine = Environment.MachineName,
                user = Environment.UserName,
                language = snapshot.Language,
                scope = snapshot.Scope.Length == 0 ? "all mounts" : snapshot.Scope,
                frozen = snapshot.Frozen,
                windowSeconds = snapshot.WindowSeconds,
                // Rows is what samples.csv holds: one row per layer per sampled second. Ticks
                // and layers are the two counts those rows are the product of, so a reader can
                // tell a short window from a missing layer without opening the CSV.
                rows,
                ticks = snapshot.Recording?.Seconds is { } seconds && seconds > 0
                    ? (long)Math.Round(seconds)
                    : snapshot.Samples.Select(sample => sample.Time).Distinct().Count(),
                layers = snapshot.Recording?.Layers.Count > 0
                    ? snapshot.Recording.Layers.Count
                    : snapshot.Samples.Select(sample => (sample.Mount, sample.Layer)).Distinct().Count(),
                logs = logs.Count
            },
            environment = new
            {
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                osVersion = Environment.OSVersion.VersionString,
                architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                processors = Environment.ProcessorCount,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                tray = EntryVersion(),
                winFsp = WinFspVersion()
            },
            serviceProcess = ServiceProcess(),
            service = new { reachable = service.Reachable, error = service.Error, mounts },
            layers = snapshot.Metrics,
            recording = snapshot.Recording,
            charts = snapshot.Charts.Select(chart => new
            {
                chart = chart.Chart,
                unit = chart.Unit,
                current = chart.Statistics.Current,
                average = chart.Statistics.Average,
                peak = chart.Statistics.Peak,
                p95 = chart.Statistics.P95,
                total = chart.Statistics.Total,
                samples = chart.Statistics.Samples
            }),
            response = new
            {
                samples = snapshot.ResponseRequest.Count,
                requestAvgMs = Average(snapshot.ResponseRequest),
                requestP95Ms = ChartStatistics.Percentile(snapshot.ResponseRequest.Order().ToArray(), 0.95),
                requestMaxMs = snapshot.ResponseRequest.DefaultIfEmpty(0).Max(),
                dispatchAvgMs = Average(snapshot.ResponseDispatch),
                uiAvgMs = Average(snapshot.ResponseUi)
            },
            logs = logs.Select(log => new
            {
                name = log.Name,
                bytes = log.Bytes,
                error = log.Error,
                // Which span of wall clock each log covers, so two overlapping segments of the
                // same instance can be told apart without opening them.
                lines = coverage.TryGetValue(log.Name, out var span) ? span.Lines : 0,
                firstUtc = coverage.TryGetValue(log.Name, out span) && span.FirstUtc != null
                    ? span.FirstUtc.Value.ToString("O", CultureInfo.InvariantCulture) : null,
                lastUtc = coverage.TryGetValue(log.Name, out span) && span.LastUtc != null
                    ? span.LastUtc.Value.ToString("O", CultureInfo.InvariantCulture) : null
            })
        };
        return JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    /// <summary>The tray build that produced the bundle, so a report can be tied to a revision.</summary>
    private static string EntryVersion()
    {
        try
        {
            var assembly = typeof(StatsExport).Assembly;
            return assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                is [System.Reflection.AssemblyInformationalVersionAttribute version, ..]
                ? version.InformationalVersion
                : assembly.GetName().Version?.ToString() ?? "unknown";
        }
        catch { return "unknown"; }
    }

    /// <summary>The WinFsp build the mount runs against. Read from the install location rather
    /// than from configuration, which the service owns and the tray only sees through status.</summary>
    private static string WinFspVersion()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinFsp", "bin", "winfsp-x64.dll");
            return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion ?? "unknown";
        }
        catch { return "unknown"; }
    }

    /// <summary>What the service process had spent when the window was captured — the one figure
    /// the window cannot show, since its charts describe PolyMount's own bookkeeping rather than
    /// whether the machine was busy. A stopped service is recorded as absent; the records the
    /// window holds are the point, and they outlive it.</summary>
    private static object? ServiceProcess() => ServiceProcessReading.Now() is { } sample ? new
    {
        id = sample.Id,
        startTimeUtc = sample.StartTimeUtc,
        uptimeSeconds = sample.UptimeSeconds,
        cpuSeconds = sample.CpuSeconds,
        workingSetBytes = sample.WorkingSetBytes,
        privateBytes = sample.PrivateBytes,
        threads = sample.Threads,
        handles = sample.Handles
    } : null;

    private static double Average(List<double> values) => values.Count == 0 ? 0 : values.Average();

    /// <summary>One row per layer per second. The combined "all mounts" view is the sum of these
    /// rows, so it is not written twice.</summary>
    private static long WriteSamples(string directory, List<SampleRecord> samples)
    {
        var csv = new StringBuilder(SamplesCsvHeader);
        foreach (var sample in samples) AppendSampleRow(csv, sample);
        File.WriteAllText(Path.Combine(directory, "samples.csv"), csv.ToString(), Utf8);
        return samples.Count;
    }

    /// <summary>Carries a CSV a recording wrote as it ran, and counts what it holds.</summary>
    private static long CarrySamples(string directory, string written)
    {
        File.Copy(written, Path.Combine(directory, "samples.csv"), true);
        long rows = 0;
        foreach (string line in File.ReadLines(written)) rows++;
        return Math.Max(0, rows - 1);
    }

    /// <summary>The columns of <c>samples.csv</c>, shared with the writer a recording appends to
    /// so the two cannot drift apart. <c>layer_avg_cost_ms</c> and <c>layer_max_cost_ms</c> are
    /// what that layer itself was measured at; <c>avg_cost_ms</c> is its share of the mount's
    /// average, which is what makes the layers stack to the whole.</summary>
    internal const string SamplesCsvHeader =
        "time_utc,mount,layer,reads,bytes,avg_cost_ms,write_bytes,wasted,wasted_bytes,accelerated_bytes,fetched_bytes,layer_avg_cost_ms,layer_max_cost_ms\n";

    internal static void AppendSampleRow(StringBuilder csv, SampleRecord sample) =>
        csv.Append(sample.Time.ToString("O", CultureInfo.InvariantCulture)).Append(',')
            .Append(Escape(sample.Mount)).Append(',').Append(Escape(sample.Layer)).Append(',')
            .Append(Number(sample.Reads)).Append(',').Append(Number(sample.Bytes)).Append(',')
            .Append(Number(sample.Cost)).Append(',').Append(Number(sample.WriteBytes)).Append(',')
            .Append(Number(sample.Wasted)).Append(',').Append(Number(sample.WastedBytes)).Append(',')
            .Append(Number(sample.Accelerated)).Append(',').Append(Number(sample.Fetched)).Append(',')
            .Append(Number(sample.AvgCost)).Append(',').Append(Number(sample.MaxCost)).Append('\n');

    /// <summary>One row per second of a recording: what the mount served and what the machine it
    /// ran on was spending, which is the correlation the per-layer rows cannot show.</summary>
    private static string SecondsCsv(List<SecondRecord> seconds)
    {
        var csv = new StringBuilder(
            "time_utc,reads,bytes,avg_cost_ms,write_bytes,request_ms,dispatch_ms,ui_ms,cpu_seconds,working_set_bytes,private_bytes,threads,handles\n");
        foreach (var second in seconds)
            csv.Append(second.Time.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(Number(second.Reads)).Append(',').Append(Number(second.Bytes)).Append(',')
                .Append(Number(second.Cost)).Append(',').Append(Number(second.WriteBytes)).Append(',')
                .Append(Number(second.RequestMs)).Append(',').Append(Number(second.DispatchMs)).Append(',')
                .Append(Number(second.UiMs)).Append(',').Append(Number(second.CpuSeconds)).Append(',')
                .Append(second.WorkingSetBytes.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(second.PrivateBytes.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(second.Threads.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(second.Handles.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return csv.ToString();
    }

    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Escape(string field) => field.IndexOfAny(new[] { ',', '"', '\n' }) < 0
        ? field : '"' + field.Replace("\"", "\"\"") + '"';
}

/// <summary>One reading of the service process, for the snapshot the bundle carries and for the
/// per-second series a recording collects. Absent when the service is not running.</summary>
internal sealed record ServiceProcessReading(int Id, string StartTimeUtc, double UptimeSeconds, double CpuSeconds,
    long WorkingSetBytes, long PrivateBytes, int Threads, int Handles)
{
    public static ServiceProcessReading? Now()
    {
        System.Diagnostics.Process[] candidates;
        try { candidates = System.Diagnostics.Process.GetProcessesByName("PolyMount.Service"); }
        catch { return null; }
        try
        {
            var process = candidates.OrderByDescending(candidate => candidate.StartTime).FirstOrDefault();
            if (process == null) return null;
            return new(process.Id, process.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                (DateTime.Now - process.StartTime).TotalSeconds, process.TotalProcessorTime.TotalSeconds,
                process.WorkingSet64, process.PrivateMemorySize64, process.Threads.Count, process.HandleCount);
        }
        catch { return null; }
        finally { foreach (var candidate in candidates) candidate.Dispose(); }
    }
}
