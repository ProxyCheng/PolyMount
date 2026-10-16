namespace PolyMount.Core.Ipc;

/// <summary>Ranks the processes that used an instance and folds the tail away.
///
/// A pie can draw a handful of slices and a legend can name them; a busy mount sees dozens of
/// processes, most of them one metadata lookup by something the reader never meant to look at. The
/// tail is not dropped — it becomes one slice, so the circle still adds up to everything that
/// happened — and the fold groups by <em>name</em> first, so the two readers of one application
/// count as that application rather than competing for the top.
///
/// Core does it rather than the window because both need it: an instance folds its own window
/// before answering, and the "all instances" view folds again over the merged list, where one
/// application may appear once per mount.</summary>
public static class ProcessFold
{
    /// <summary>How many processes a window names before the rest are folded together.</summary>
    public const int Named = 8;

    /// <summary>The named processes, busiest first, and one row for everything else.
    ///
    /// What "busiest" means is the caller's: a window that draws the requests a process made ranks
    /// them by those, and one that draws what the layers did for them ranks them by that — the two
    /// are different orders, and a fold that dropped a row the second order would have kept is a
    /// fold that lost the reading the reader was looking for.</summary>
    public static List<ProcessMetric> Top(IEnumerable<ProcessMetric> rows, int keep = Named, Func<ProcessMetric, long>? rank = null)
    {
        rank ??= row => row.Reads + row.Writes;
        var merged = new Dictionary<string, ProcessMetric>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            // A row with no name is a process that could not be read: it is keyed by its id so two
            // of them are not added together into one that never existed.
            string key = row.Name.Length > 0 ? row.Name : "pid " + row.Pid;
            if (!merged.TryGetValue(key, out var sum))
                merged[key] = sum = new ProcessMetric { Pid = row.Pid, Name = row.Name, Color = row.Color };
            sum.Reads += row.Reads;
            sum.Writes += row.Writes;
            sum.ReadBytes += row.ReadBytes;
            sum.WriteBytes += row.WriteBytes;
            Merge(sum, row);
        }
        var ranked = merged.Values.OrderByDescending(rank).ToList();
        if (ranked.Count <= keep + 1) return ranked;
        var kept = ranked.Take(keep).ToList();
        var other = new ProcessMetric
        {
            Other = true,
            Reads = ranked.Skip(keep).Sum(row => row.Reads),
            Writes = ranked.Skip(keep).Sum(row => row.Writes),
            ReadBytes = ranked.Skip(keep).Sum(row => row.ReadBytes),
            WriteBytes = ranked.Skip(keep).Sum(row => row.WriteBytes)
        };
        foreach (var row in ranked.Skip(keep)) Merge(other, row);
        kept.Add(other);
        return kept;
    }

    /// <summary>Adds one row's per-layer counts into another's, which is what keeps the fold
    /// honest for the one reading a fold could otherwise lose: the tail's layers still add up to
    /// the column totals, so a reader comparing the bucket with the layers sees the same numbers
    /// either way.</summary>
    private static void Merge(ProcessMetric into, ProcessMetric from)
    {
        foreach (var part in from.ByLayer)
        {
            var held = into.ByLayer.FirstOrDefault(
                row => string.Equals(row.Name, part.Name, StringComparison.OrdinalIgnoreCase));
            if (held == null) into.ByLayer.Add(new ProcessLayerCount { Name = part.Name, Reads = part.Reads, Writes = part.Writes });
            else { held.Reads += part.Reads; held.Writes += part.Writes; }
        }
    }
}
