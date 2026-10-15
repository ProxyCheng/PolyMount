using PolyMount.Core.Ipc;

namespace PolyMount.Tray;

/// <summary>
/// The per-second layer math both the performance window and a recording run on. They describe
/// the same counters, so they have to describe them the same way: a rule like "the first
/// observation of a lifetime total is not a rate" cannot be copied into a second page without
/// the two eventually disagreeing about the same number.
///
/// State stays with the window that owns it — the history, the last counters seen and the
/// widgets are the page's own; only the functions live here.
/// </summary>
internal static class LayerSampler
{
    /// <summary>One layer's contribution to one second. <see cref="Cost"/> is its share of the
    /// mount's average read cost that second, not that layer's own average, so the layers stack
    /// to the total; <see cref="AvgCost"/> and <see cref="MaxCost"/> are what that layer itself
    /// was measured at, which is what a whole-recording average has to be built from.</summary>
    internal sealed record LayerSample(double Reads, double Cost, double Bytes, double WriteBytes,
        double Wasted = 0, double WastedBytes = 0, double Accelerated = 0, double Fetched = 0,
        double AvgCost = 0, double MaxCost = 0);

    internal sealed record Sample(DateTime Time, Dictionary<string, LayerSample> Layers);

    /// <summary>What one tick added to the counters a layer reports as lifetime totals. A rate
    /// only means something next to the interval it was measured over.</summary>
    internal readonly record struct WasteStep(double Count, double Bytes, double Served, double Fetched)
    {
        public static readonly WasteStep None = new(0, 0, 0, 0);
    }

    /// <summary>What was last seen of a layer's lifetime counters, and when: a step spanning more
    /// than the sampling gap is a pause in observation rather than a rate.</summary>
    internal readonly record struct Counters(long Count, long Bytes, long Served, long Fetched, DateTime Time);

    /// <summary>One layer's key is "mount/layer".</summary>
    internal static string Key(MountMetrics mount, LayerMetric layer) => mount.Mount + "/" + layer.Name;

    /// <summary>Takes the step for this tick of every counter a layer reports as a lifetime
    /// total, one entry per layer. A layer can evict a whole budget's worth in a second, so this
    /// is a rate only when it was measured over a normal interval: the first observation of a
    /// layer, and the first one after the window was hidden or the instance switched, would
    /// otherwise bill a lifetime total of hundreds of megabytes to a single second, against a
    /// chart scaled in kilobytes.</summary>
    internal static Dictionary<string, WasteStep> Steps(DateTime time, IEnumerable<MountMetrics> mounts,
        IDictionary<string, Counters> previous, double gapSeconds)
    {
        var steps = new Dictionary<string, WasteStep>();
        foreach (var (key, layer) in mounts.SelectMany(m => m.Layers.Select(l => (Key: Key(m, l), Layer: l))))
        {
            bool measured = previous.TryGetValue(key, out var seen) && (time - seen.Time).TotalSeconds <= gapSeconds;
            previous[key] = new Counters(layer.HeldWasted, layer.HeldWastedBytes, layer.AcceleratedBytes, layer.FetchedBytes, time);
            // A counter that went backwards was rebuilt, not credited: clamp rather than dip.
            steps[key] = measured
                ? new WasteStep(Math.Max(0, layer.HeldWasted - seen.Count), Math.Max(0, layer.HeldWastedBytes - seen.Bytes),
                    Math.Max(0, layer.AcceleratedBytes - seen.Served), Math.Max(0, layer.FetchedBytes - seen.Fetched))
                : WasteStep.None;
        }
        return steps;
    }

    internal static Sample Capture(DateTime time, IEnumerable<MountMetrics> mounts, IReadOnlyDictionary<string, WasteStep> steps)
    {
        var rows = mounts.SelectMany(m => m.Layers.Select(l => (Key: Key(m, l), Layer: l))).ToArray();
        double reads = rows.Sum(r => r.Layer.Hits);
        return new(time, rows.ToDictionary(r => r.Key, r =>
        {
            var step = steps.TryGetValue(r.Key, out var found) ? found : WasteStep.None;
            return new LayerSample(r.Layer.Hits,
                reads == 0 ? 0 : r.Layer.AvgCostMs * r.Layer.Hits / reads, r.Layer.Bytes, r.Layer.WriteBytes,
                step.Count, step.Bytes, step.Served, step.Fetched, r.Layer.AvgCostMs, r.Layer.MaxCostMs);
        }));
    }

    /// <summary>Appends one observation, dropping whatever fell out of the horizon. A horizon of
    /// zero keeps the whole recording, which is what the recording page is for.</summary>
    internal static List<Sample> Add(IDictionary<string, List<Sample>> history, string name, Sample sample, int horizonSeconds)
    {
        if (!history.TryGetValue(name, out var samples)) history[name] = samples = new();
        samples.Add(sample);
        if (horizonSeconds > 0) samples.RemoveAll(s => s.Time < sample.Time.AddSeconds(-horizonSeconds));
        return samples;
    }

    /// <summary>What an accelerating layer kept out of the streams underneath over the window:
    /// the share of read bytes it served from its own pages, and how many those were. A block
    /// cache answers no reads of its own, so this is the only account of its work.</summary>
    internal static (double Percent, double Bytes) Accelerated(IEnumerable<Sample> samples, string key)
    {
        double served = 0, fetched = 0;
        foreach (var sample in samples)
        {
            if (!sample.Layers.TryGetValue(key, out var layer)) continue;
            served += layer.Accelerated;
            fetched += layer.Fetched;
        }
        double total = served + fetched;
        return (total == 0 ? 0 : 100.0 * served / total, served);
    }
}
