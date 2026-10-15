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
///
/// The logs are an accumulating family — a service that has been up for hours still has every
/// one of those hours on disk — so the counts describe the export's own window, not the files.
/// Without that, a fix and the records of the problem it fixed read as one and the same number.
/// </summary>
internal static class RequestLogDigest
{
    /// <summary>How many entries each ranked list keeps. The point is to rank, not to be complete.</summary>
    private const int TopOperations = 60;
    private const int TopPaths = 40;
    private const int TopSlowest = 40;
    private const int TopCallbacks = 40;
    private const int TopPending = 20;
    /// <summary>The floor under which a request is not worth ranking in a sample.</summary>
    private const int SlowEnoughMs = 100;
    /// <summary>How many entries are collected before the ranked lists are trimmed. A sampled log
    /// never came close to these; a recording, which carries every request, does.</summary>
    private const int KeptSlowest = 4000;
    private const int KeptPending = 500;
    /// <summary>How many distinct paths are ranked. Past this, a new path is counted in the
    /// totals but not ranked — a recording of a whole game load can touch a great many.</summary>
    private const int KeptPaths = 50000;

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

    /// <summary>Everything one pass over the logs produced, before it is ranked and rendered.</summary>
    private sealed class LogAggregate
    {
        public readonly Dictionary<string, OperationStats> Operations = new(StringComparer.Ordinal);
        public readonly Dictionary<(string Operation, string Path), PathStats> Paths = new();
        public readonly Dictionary<(string Pid, string Operation), CallbackStats> Callbacks = new();
        public readonly List<Slow> Slowest = new();
        public readonly List<Pending> PendingRequests = new();
        public readonly List<LogCoverage> Coverage = new();
        public long Begins, Ends, PendingCount, Counts, Dropped, Lines;
        /// <summary>Set when a ranked list had to drop entries, so a reader is told the list is
        /// the top of a larger set rather than the whole of it.</summary>
        public bool SlowestTrimmed, PendingTrimmed, PathsTrimmed;

        public bool Empty => Ends == 0 && Begins == 0 && PendingCount == 0 && Callbacks.Count == 0;
    }

    /// <summary>Reads each copied log from the bundle, so the digest describes exactly the files
    /// the reader has. A log that cannot be read is reported with its error and skipped rather
    /// than failing the export it is part of.</summary>
    public static RequestDigest Build(IReadOnlyList<LogFileCopy> logs, DateTime capturedUtc, int windowSeconds)
        => Build(logs, ToOffset(capturedUtc) - TimeSpan.FromSeconds(windowSeconds), ToOffset(capturedUtc), "window");

    /// <summary>The same reduction over a window that is stated rather than derived: a recording
    /// covers however long the reader was recording, which is not a constant of the tool.</summary>
    public static RequestDigest Build(IReadOnlyList<LogFileCopy> logs, DateTimeOffset from, DateTimeOffset to, string kind)
    {
        // A windowed export ranks only the requests worth ranking: it is a sample, so a list of
        // everything fast would bury the few that matter. A recording carries every request, so
        // the interesting thing is the ranking itself — which paths cost the most relative to
        // each other — and the floor would leave a fast workload with an empty list.
        long floor = kind == "recording" ? 0 : SlowEnoughMs;
        // A window the reader was paused through can predate everything the logs still
        // hold. Answering with nothing would be true and useless, so fall back to the whole span
        // and let the reader see that the two do not line up.
        var aggregate = Aggregate(logs, from, to, floor);
        bool windowed = true;
        if (aggregate.Empty)
        {
            aggregate = Aggregate(logs, null, null, floor);
            windowed = false;
        }
        return new RequestDigest(Render(aggregate, windowed ? from : null, to, windowed, kind), aggregate.Coverage);
    }

    private static DateTimeOffset ToOffset(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    private static LogAggregate Aggregate(IReadOnlyList<LogFileCopy> logs, DateTimeOffset? from, DateTimeOffset? to, long floor)
    {
        var aggregate = new LogAggregate();
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
                    bool stamped = DateTimeOffset.TryParse(line.AsSpan(0, firstSpace), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var at);
                    if (stamped)
                    {
                        first ??= at;
                        last = at;
                    }
                    // Coverage describes the whole file; the figures describe the window.
                    if (!stamped || (from != null && at < from) || (to != null && at > to)) continue;
                    var message = line.AsSpan(secondSpace + 1);
                    string atUtc = at.ToString("O", CultureInfo.InvariantCulture);
                    if (message.StartsWith("BEGIN "))
                    {
                        aggregate.Begins++; events++;
                        Stats(aggregate.Operations, Quoted(message, " op=")).Begins++;
                        continue;
                    }
                    if (message.StartsWith("END "))
                    {
                        aggregate.Ends++; events++;
                        string operation = Quoted(message, " op=");
                        string path = Quoted(message, " path='");
                        long elapsed = Number(message, " elapsedMs=");
                        var stats = Stats(aggregate.Operations, operation);
                        stats.Ends++; stats.Sum += elapsed;
                        if (elapsed > stats.Max) stats.Max = elapsed;
                        if (elapsed >= 100) stats.Over100++;
                        if (elapsed >= 1000) stats.Over1000++;
                        // Only the slow ones are worth ranking in a sample; a log of fast
                        // requests would otherwise fill the list with entries nobody needs to see.
                        if (elapsed >= floor)
                        {
                            var key = (operation, path);
                            if (!aggregate.Paths.TryGetValue(key, out var entry))
                            {
                                if (aggregate.Paths.Count >= KeptPaths) { aggregate.PathsTrimmed = true; continue; }
                                aggregate.Paths[key] = entry = new PathStats { Operation = operation };
                            }
                            entry.Ends++; entry.Sum += elapsed;
                            if (elapsed > entry.Max) entry.Max = elapsed;
                            aggregate.Slowest.Add(new Slow(atUtc, operation, path, elapsed, Quoted(message, " stage='")));
                            aggregate.SlowestTrimmed |= Trim(aggregate.Slowest, KeptSlowest, entry => entry.ElapsedMs);
                        }
                        continue;
                    }
                    if (message.StartsWith("PENDING "))
                    {
                        aggregate.PendingCount++; events++;
                        aggregate.PendingRequests.Add(new Pending(atUtc, Quoted(message, " op="), Quoted(message, " path='"),
                            Number(message, " elapsedMs="), Quoted(message, " stage='")));
                        aggregate.PendingTrimmed |= Trim(aggregate.PendingRequests, KeptPending, entry => entry.ElapsedMs);
                        continue;
                    }
                    if (message.StartsWith("COUNT "))
                    {
                        aggregate.Counts++; events++;
                        string pid = Quoted(message, " pid=");
                        string operation = Quoted(message, " op=");
                        long requests = Number(message, " requests=");
                        var key = (pid, operation);
                        if (!aggregate.Callbacks.TryGetValue(key, out var stats)) aggregate.Callbacks[key] = stats = new CallbackStats();
                        stats.Requests += requests;
                        if (requests > stats.Peak)
                        {
                            stats.Peak = requests;
                            stats.AtUtc = atUtc;
                        }
                        continue;
                    }
                    if (message.StartsWith("DROPPED ")) { aggregate.Dropped += Number(message, " count="); events++; }
                }
            }
            catch (Exception ex)
            {
                // A log that cannot be read is reported alongside the ones that could; the
                // export it belongs to is still worth having.
                System.Diagnostics.Debug.WriteLine($"digest '{log.Name}': {ex.Message}");
            }
            aggregate.Lines += lines;
            aggregate.Coverage.Add(new LogCoverage(log.Name, lines, events, first, last));
        }
        return aggregate;
    }

    /// <summary>Keeps only the largest few of a stream, so a recording — which carries every
    /// request rather than a sample of them — cannot grow the digest without bound. Trimming
    /// happens in batches: sorting a short list beats the bookkeeping a real heap would need
    /// here, and the lists are read once, at the end. Returns whether anything was dropped.</summary>
    private static bool Trim<T>(List<T> items, int keep, Func<T, long> rank)
    {
        if (items.Count <= keep + keep) return false;
        items.Sort((left, right) => rank(right).CompareTo(rank(left)));
        items.RemoveRange(keep, items.Count - keep);
        return true;
    }

    private static string Render(LogAggregate aggregate, DateTimeOffset? from, DateTimeOffset? to, bool windowed, string kind)
    {
        var operationRows = aggregate.Operations
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
        var pathRows = aggregate.Paths
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
        var slowRows = aggregate.Slowest
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
        var pendingRows = aggregate.PendingRequests
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
        var callbackRows = aggregate.Callbacks
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
            window = new
            {
                fromUtc = from?.ToString("O", CultureInfo.InvariantCulture),
                toUtc = to?.ToString("O", CultureInfo.InvariantCulture),
                // False means the window held nothing and the whole log span was used instead;
                // coverage[].firstUtc/lastUtc then say how far that reaches back.
                filtered = windowed,
                kind
            },
            note = kind == "recording"
                ? "A recording: no sampling filter was applied, so every request event is here and "
                    + "the operation counts are complete rather than estimated. A Layer probe that the "
                    + "sampled log would have discarded carries a negative id. COUNT lines still "
                    + "summarise every callback over the service's 5 second window."
                : "Logged requests only. SVN.* is recorded in full; a fast root request is sampled "
                    + "at about 1/128 (its nested calls come with it), and any request that takes at "
                    + "least 100 ms keeps its END line. COUNT lines summarise every callback, sampled "
                    + "or not, over the service's 5 second window.",
            totals = new
            {
                // The figures describe the window; this counts every line the copied
                // files hold, which is how far back the bundle could have looked.
                linesRead = aggregate.Lines,
                begins = aggregate.Begins,
                ends = aggregate.Ends,
                pending = aggregate.PendingCount,
                counts = aggregate.Counts,
                dropped = aggregate.Dropped,
                operations = aggregate.Operations.Count,
                paths = aggregate.Paths.Count,
                // The ranked lists are the top of a larger set when this is true.
                slowestTrimmed = aggregate.SlowestTrimmed,
                pendingTrimmed = aggregate.PendingTrimmed,
                pathsTrimmed = aggregate.PathsTrimmed
            },
            coverage = aggregate.Coverage,
            operations = operationRows,
            paths = pathRows,
            slowest = slowRows,
            pendingRequests = pendingRows,
            callbacks = callbackRows
        };
        return JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
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
