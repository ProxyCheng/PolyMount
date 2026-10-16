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

    /// <summary>The named processes, busiest first, and one row for everything else.</summary>
    public static List<ProcessMetric> Top(IEnumerable<ProcessMetric> rows, int keep = Named)
    {
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
        }
        var ranked = merged.Values.OrderByDescending(row => row.Reads + row.Writes).ToList();
        if (ranked.Count <= keep + 1) return ranked;
        var kept = ranked.Take(keep).ToList();
        kept.Add(new ProcessMetric
        {
            Other = true,
            Reads = ranked.Skip(keep).Sum(row => row.Reads),
            Writes = ranked.Skip(keep).Sum(row => row.Writes),
            ReadBytes = ranked.Skip(keep).Sum(row => row.ReadBytes),
            WriteBytes = ranked.Skip(keep).Sum(row => row.WriteBytes)
        });
        return kept;
    }
}
