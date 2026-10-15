using System.Globalization;
using System.Text.Json;

namespace PolyMount.Tray;

/// <summary>What one copied request log covered.</summary>
internal readonly record struct LogCoverage(string Name, long Lines, long Events,
    DateTimeOffset? FirstUtc, DateTimeOffset? LastUtc);

/// <summary>A digest of the copied request logs, and the JSON that carries it.</summary>
internal readonly record struct RequestDigest(string Json, IReadOnlyList<LogCoverage> Coverage);

/// <summary>
/// Reduces the copied request logs to the figures an investigation starts from: which
/// operations cost the window's time, which paths they were spent on, which single requests
/// ran longest, and how hard each WinFsp callback was driven. The raw logs stay in the bundle
/// for the cases the digest cannot anticipate; this is the part that should not have to be
/// re-derived with a text tool every time.
/// </summary>
internal static class RequestLogDigest
{
    /// <summary>How many entries each ranked list keeps. The point is to rank, not to be complete.</summary>
    private const int TopOperations = 60;
    private const int TopPaths = 40;
    private const int TopSlowest = 40;
    private const int TopCallbacks = 40;
    private const int TopPending = 20;

    private sealed class OperationStats
    {
        public long Begins, Ends, Sum, Max, Over100, Over1000;
    }

    private sealed class PathStats
    {
        public long Ends, Sum, Max;
        public string Operation = "";
    }

    private sealed class CallbackStats
    {
        public long Requests, Peak;
        public string AtUtc = "";
    }

    private readonly record struct Slow(string AtUtc, string Operation, string Path, long ElapsedMs, string Stage);

    private readonly record struct Pending(string AtUtc, string Operation, string Path, long ElapsedMs, string Stage);

    /// <summary>Reads each copied log from the bundle, so the digest describes exactly the
    /// files the reader has. A log that cannot be read is reported with its error and skipped
    /// rather than failing the export it is part of.</summary>
    public static RequestDigest Build(IReadOnlyList<LogFileCopy> logs)
    {
        var operations = new Dictionary<string, OperationStats>(StringComparer.Ordinal);
        var paths = new Dictionary<(string Operation, string Path), PathStats>();
        var callbacks = new Dictionary<(string Pid, string Operation), CallbackStats>();
        var slowest = new List<Slow>();
        var pending = new List<Pending>();
        var coverage = new List<LogCoverage>(logs.Count);
        long begins = 0, ends = 0, pendingCount = 0, counts = 0, dropped = 0;

        foreach (var log in logs)
        {
            long lines = 0, events = 0;
            DateTimeOffset? first = null, last = null;
            try
            {
                foreach (string line in File.ReadLines(log.Target))
                {
                    lines++;
                    int firstSpace = line.IndexOf(' ');
                    if (firstSpace <= 0) continue;
                    int secondSpace = line.IndexOf(' ', firstSpace + 1);
                    if (secondSpace < 0) continue;
                    if (DateTimeOffset.TryParse(line.AsSpan(0, firstSpace), CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out var at))
                    {
                        first ??= at;
                        last = at;
                    }
                    var message = line.AsSpan(secondSpace + 1);
                    string atUtc = at.ToString("O", CultureInfo.InvariantCulture);
                    if (message.StartsWith("BEGIN "))
                    {
                        begins++; events++;
                        Stats(operations, Quoted(message, " op=")).Begins++;
                        continue;
                    }
                    if (message.StartsWith("END "))
                    {
                        ends++; events++;
                        string operation = Quoted(message, " op=");
                        string path = Quoted(message, " path='");
                        long elapsed = Number(message, " elapsedMs=");
                        var stats = Stats(operations, operation);
                        stats.Ends++; stats.Sum += elapsed;
                        if (elapsed > stats.Max) stats.Max = elapsed;
                        if (elapsed >= 100) stats.Over100++;
                        if (elapsed >= 1000) stats.Over1000++;
                        // Only the slow ones are worth ranking; a log of fast requests would
                        // otherwise fill the list with entries nobody needs to see.
                        if (elapsed >= 100)
                        {
                            var key = (operation, path);
                            if (!paths.TryGetValue(key, out var entry))
                                paths[key] = entry = new PathStats { Operation = operation };
                            entry.Ends++; entry.Sum += elapsed;
                            if (elapsed > entry.Max) entry.Max = elapsed;
                            slowest.Add(new Slow(atUtc, operation, path, elapsed, Quoted(message, " stage='")));
                        }
                        continue;
                    }
                    if (message.StartsWith("PENDING "))
                    {
                        pendingCount++; events++;
                        pending.Add(new Pending(atUtc, Quoted(message, " op="), Quoted(message, " path='"),
                            Number(message, " elapsedMs="), Quoted(message, " stage='")));
                        continue;
                    }
                    if (message.StartsWith("COUNT "))
                    {
                        counts++; events++;
                        string pid = Quoted(message, " pid=");
                        string operation = Quoted(message, " op=");
                        long requests = Number(message, " requests=");
                        var key = (pid, operation);
                        if (!callbacks.TryGetValue(key, out var stats)) callbacks[key] = stats = new CallbackStats();
                        stats.Requests += requests;
                        if (requests > stats.Peak)
                        {
                            stats.Peak = requests;
                            stats.AtUtc = atUtc;
                        }
                        continue;
                    }
                    if (message.StartsWith("DROPPED ")) { dropped += Number(message, " count="); events++; }
                }
            }
            catch (Exception ex)
            {
                // A log that cannot be read is reported alongside the ones that could; the
                // export it belongs to is still worth having.
                System.Diagnostics.Debug.WriteLine($"digest '{log.Name}': {ex.Message}");
            }
            coverage.Add(new LogCoverage(log.Name, lines, events, first, last));
        }

        var operationRows = operations
            .OrderByDescending(pair => pair.Value.Sum)
            .Take(TopOperations)
            .Select(pair => new
            {
                operation = pair.Key,
                begins = pair.Value.Begins,
                ends = pair.Value.Ends,
                totalMs = pair.Value.Sum,
                avgMs = pair.Value.Ends == 0 ? 0 : (double)pair.Value.Sum / pair.Value.Ends,
                maxMs = pair.Value.Max,
                over100Ms = pair.Value.Over100,
                over1000Ms = pair.Value.Over1000
            });
        var pathRows = paths
            .OrderByDescending(pair => pair.Value.Sum)
            .Take(TopPaths)
            .Select(pair => new
            {
                path = pair.Key.Path,
                operation = pair.Value.Operation,
                ends = pair.Value.Ends,
                totalMs = pair.Value.Sum,
                maxMs = pair.Value.Max
            });
        var slowRows = slowest
            .OrderByDescending(entry => entry.ElapsedMs)
            .Take(TopSlowest)
            .Select(entry => new
            {
                atUtc = entry.AtUtc,
                operation = entry.Operation,
                path = entry.Path,
                elapsedMs = entry.ElapsedMs,
                stage = entry.Stage
            });
        var pendingRows = pending
            .OrderByDescending(entry => entry.ElapsedMs)
            .Take(TopPending)
            .Select(entry => new
            {
                atUtc = entry.AtUtc,
                operation = entry.Operation,
                path = entry.Path,
                elapsedMs = entry.ElapsedMs,
                stage = entry.Stage
            });
        var callbackRows = callbacks
            .OrderByDescending(pair => pair.Value.Peak)
            .Take(TopCallbacks)
            .Select(pair => new
            {
                pid = pair.Key.Pid,
                operation = pair.Key.Operation,
                requests = pair.Value.Requests,
                peakPerWindow = pair.Value.Peak,
                peakAtUtc = pair.Value.AtUtc
            });

        var document = new
        {
            note = "Logged requests only. SVN.* is recorded in full; a fast root request is sampled "
                + "at about 1/128 (its nested calls come with it), and any request that takes at "
                + "least 100 ms keeps its END line. COUNT lines summarise every callback, sampled "
                + "or not, over the service's 5 second window.",
            totals = new
            {
                lines = coverage.Sum(entry => entry.Lines),
                begins,
                ends,
                pending = pendingCount,
                counts,
                dropped,
                operations = operations.Count,
                paths = paths.Count
            },
            coverage,
            operations = operationRows,
            paths = pathRows,
            slowest = slowRows,
            pendingRequests = pendingRows,
            callbacks = callbackRows
        };
        return new RequestDigest(JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }), coverage);
    }

    private static OperationStats Stats(Dictionary<string, OperationStats> operations, string operation)
    {
        if (!operations.TryGetValue(operation, out var stats)) operations[operation] = stats = new OperationStats();
        return stats;
    }

    /// <summary>The value of a field, up to whichever of a space or a quote ends it.</summary>
    private static string Quoted(ReadOnlySpan<char> message, string marker)
    {
        int at = message.IndexOf(marker);
        if (at < 0) return "";
        var rest = message[(at + marker.Length)..];
        int end = rest.IndexOfAny(' ', '\'', '\r');
        return (end < 0 ? rest : rest[..end]).ToString();
    }

    private static long Number(ReadOnlySpan<char> message, string marker)
    {
        int at = message.IndexOf(marker);
        if (at < 0) return 0;
        var rest = message[(at + marker.Length)..];
        int end = rest.IndexOfAny(' ', '\'', '\r');
        if (end >= 0) rest = rest[..end];
        return long.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;
    }
}
