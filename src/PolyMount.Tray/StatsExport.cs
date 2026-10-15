using System.Globalization;
using System.Text;
using System.Text.Json;
using PolyMount.Core.Ipc;

namespace PolyMount.Tray;

/// <summary>A frozen performance window: the numbers on screen, the per-second records behind
/// them, and the front-end overhead they were measured with. Plain records, so the exporter
/// needs no access to the window itself.</summary>
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
    /// <summary>What each chart's header showed, in the order the charts are laid out.</summary>
    public List<ChartRecord> Charts { get; init; } = new();
    /// <summary>The per-layer analytics the window displayed, as reported by the service.</summary>
    public List<MountMetrics> Metrics { get; init; } = new();
    public List<SampleRecord> Samples { get; init; } = new();
    public List<double> ResponseRequest { get; init; } = new();
    public List<double> ResponseDispatch { get; init; } = new();
    public List<double> ResponseUi { get; init; } = new();
}

/// <summary>One chart's headline and summary figures.</summary>
internal readonly record struct ChartRecord(string Chart, string Unit, ChartStatistics Statistics);

/// <summary>One layer's contribution to one second of the window.</summary>
internal readonly record struct SampleRecord(DateTime Time, string Mount, string Layer,
    double Reads, double Bytes, double Cost, double WriteBytes, double Wasted, double WastedBytes,
    double Accelerated, double Fetched);

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
        var copies = CopyLogs(directory, logs);
        // The digest is built from the bundle's own copies, so it describes exactly the files
        // the reader has — including however far the logs had rolled by the time they were read.
        var digest = RequestLogDigest.Build(copies);
        File.WriteAllText(Path.Combine(directory, "samples.csv"), SamplesCsv(snapshot.Samples), Utf8);
        File.WriteAllText(Path.Combine(directory, "requests-summary.json"), digest.Json, Utf8);
        File.WriteAllText(Path.Combine(directory, "summary.json"), SummaryJson(snapshot, service, mounts, copies, digest), Utf8);
        return new(directory, 3 + copies.Count(copy => copy.Error == null), snapshot.Samples.Count, copies.Count);
    }

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

    private static string SummaryJson(StatsSnapshot snapshot, (bool Reachable, string? Error) service,
        List<MountStatus> mounts, List<LogFileCopy> logs, RequestDigest digest)
    {
        var coverage = digest.Coverage
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var document = new
        {
            export = new
            {
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
                rows = snapshot.Samples.Count,
                ticks = snapshot.Samples.Select(sample => sample.Time).Distinct().Count(),
                layers = snapshot.Samples.Select(sample => (sample.Mount, sample.Layer)).Distinct().Count(),
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
    private static object? ServiceProcess()
    {
        System.Diagnostics.Process[] candidates;
        try { candidates = System.Diagnostics.Process.GetProcessesByName("PolyMount.Service"); }
        catch { return null; }
        try
        {
            var process = candidates.OrderByDescending(candidate => candidate.StartTime).FirstOrDefault();
            if (process == null) return null;
            return new
            {
                id = process.Id,
                startTimeUtc = process.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                uptimeSeconds = (DateTime.Now - process.StartTime).TotalSeconds,
                cpuSeconds = process.TotalProcessorTime.TotalSeconds,
                workingSetBytes = process.WorkingSet64,
                privateBytes = process.PrivateMemorySize64,
                threads = process.Threads.Count,
                handles = process.HandleCount
            };
        }
        catch { return null; }
        finally { foreach (var candidate in candidates) candidate.Dispose(); }
    }

    private static double Average(List<double> values) => values.Count == 0 ? 0 : values.Average();

    /// <summary>One row per layer per second. The combined "all mounts" view is the sum of these
    /// rows, so it is not written twice.</summary>
    private static string SamplesCsv(List<SampleRecord> samples)
    {
        var csv = new StringBuilder("time_utc,mount,layer,reads,bytes,avg_cost_ms,write_bytes,wasted,wasted_bytes,accelerated_bytes,fetched_bytes\n");
        foreach (var sample in samples)
            csv.Append(sample.Time.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(Escape(sample.Mount)).Append(',').Append(Escape(sample.Layer)).Append(',')
                .Append(Number(sample.Reads)).Append(',').Append(Number(sample.Bytes)).Append(',')
                .Append(Number(sample.Cost)).Append(',').Append(Number(sample.WriteBytes)).Append(',')
                .Append(Number(sample.Wasted)).Append(',').Append(Number(sample.WastedBytes)).Append(',')
                .Append(Number(sample.Accelerated)).Append(',').Append(Number(sample.Fetched)).Append('\n');
        return csv.ToString();
    }

    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Escape(string field) => field.IndexOfAny(new[] { ',', '"', '\n' }) < 0
        ? field : '"' + field.Replace("\"", "\"\"") + '"';
}
