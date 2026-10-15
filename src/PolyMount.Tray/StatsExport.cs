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
    public bool Frozen { get; init; }
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
        File.WriteAllText(Path.Combine(directory, "samples.csv"), SamplesCsv(snapshot.Samples), Utf8);
        File.WriteAllText(Path.Combine(directory, "summary.json"), SummaryJson(snapshot, service, mounts, copies), Utf8);
        return new(directory, 2 + copies.Count(c => c.Error == null), snapshot.Samples.Count, copies.Count);
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
    private static List<(string Name, long Bytes, string? Error)> CopyLogs(string directory, List<LogFile> logs)
    {
        var copies = new List<(string, long, string?)>();
        foreach (var log in logs)
        {
            string target = Path.Combine(directory, "logs", log.Name.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(log.Source, target, true);
                copies.Add((log.Name, new FileInfo(target).Length, null));
            }
            catch (Exception ex)
            {
                copies.Add((log.Name, 0, ex.Message));
            }
        }
        return copies;
    }

    private static string SummaryJson(StatsSnapshot snapshot, (bool Reachable, string? Error) service,
        List<MountStatus> mounts, List<(string Name, long Bytes, string? Error)> logs)
    {
        var document = new
        {
            export = new
            {
                createdLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
                createdUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                machine = Environment.MachineName,
                user = Environment.UserName,
                language = snapshot.Language,
                scope = snapshot.Scope.Length == 0 ? "all mounts" : snapshot.Scope,
                frozen = snapshot.Frozen,
                windowSeconds = snapshot.WindowSeconds,
                samples = snapshot.Samples.Count,
                logs = logs.Count
            },
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
            logs = logs.Select(log => new { name = log.Name, bytes = log.Bytes, error = log.Error })
        };
        return JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
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
