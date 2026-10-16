using PolyMount.Core.Ipc;

namespace PolyMount.Tray;

/// <summary>
/// The per-interval layer math both the performance window and a recording run on. They describe
/// the same counters, so they have to describe them the same way: a rule like "the first
/// observation of a lifetime total is not a rate" cannot be copied into a second page without
/// the two eventually disagreeing about the same number.
///
/// An observation is an <em>amount</em> over the interval it was measured across, and it carries
/// that interval. The window refreshes at a rate the reader chooses, so an amount is only a rate
/// next to the seconds it was counted over — the recording, which observes once a second, is the
/// case where the two happen to be the same number.
///
/// State stays with the window that owns it — the history, the last counters seen and the
/// widgets are the page's own; only the functions live here.
/// </summary>
internal static class LayerSampler
{
    /// <summary>One layer's contribution to one observation, as amounts over
    /// <see cref="Sample.Seconds"/>. <see cref="Cost"/> is its share of the mount's average read
    /// cost over that interval, not that layer's own average, so the layers stack to the total;
    /// <see cref="AvgCost"/> and <see cref="MaxCost"/> are what that layer itself was measured at,
    /// which is what a whole-recording average has to be built from. Costs are durations and stay
    /// as they are when an amount is turned into a rate.</summary>
    internal sealed record LayerSample(double Reads, double Cost, double Bytes, double WriteBytes,
        double Wasted = 0, double WastedBytes = 0, double Accelerated = 0, double Fetched = 0,
        double AvgCost = 0, double MaxCost = 0);

    /// <summary><paramref name="Seconds"/> is how long the window this observation was asked for
    /// covered: exactly one second for a recording, and whatever elapsed since the previous
    /// observation for a page that refreshes faster or slower than that.</summary>
    internal sealed record Sample(DateTime Time, double Seconds, Dictionary<string, LayerSample> Layers);

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

    /// <summary><paramref name="seconds"/> is the window the caller asked the service for; it
    /// defaults to the one-second window a recording polls with, which is the case where an
    /// amount and a rate are the same number.</summary>
    internal static Sample Capture(DateTime time, IEnumerable<MountMetrics> mounts,
        IReadOnlyDictionary<string, WasteStep> steps, double seconds = 1)
    {
        var rows = mounts.SelectMany(m => m.Layers.Select(l => (Key: Key(m, l), Layer: l))).ToArray();
        double reads = rows.Sum(r => r.Layer.Hits);
        return new(time, seconds, rows.ToDictionary(r => r.Key, r =>
        {
            var step = steps.TryGetValue(r.Key, out var found) ? found : WasteStep.None;
            return new LayerSample(r.Layer.Hits,
                reads == 0 ? 0 : r.Layer.AvgCostMs * r.Layer.Hits / reads, r.Layer.Bytes, r.Layer.WriteBytes,
                step.Count, step.Bytes, step.Served, step.Fetched, r.Layer.AvgCostMs, r.Layer.MaxCostMs);
        }));
    }

    /// <summary>What one observation contributes to one of the window's charts, in the chart's own
    /// unit: the counts and the bytes become rates over the interval the observation covered, and a
    /// cost stays the duration it is. <paramref name="metric"/> is the chart's index — reads,
    /// bytes, latency, written bytes. The waste series divides its own counters the same way.</summary>
    internal static double ChartValue(LayerSample layer, int metric, double seconds)
    {
        double rate = seconds > 0 ? seconds : 1;
        return metric switch
        {
            0 => layer.Reads / rate,
            1 => layer.Bytes / rate,
            2 => layer.Cost,
            _ => layer.WriteBytes / rate
        };
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

    /// <summary>What an accelerating layer kept out of the streams underneath over the window,
    /// as a rate: the share of read bytes it served from its own pages, and how many per second
    /// those were. A block cache answers no reads of its own, so this is the only account of its
    /// work. Each observation is divided by its own interval, so the figure does not depend on how
    /// often the page refreshed.</summary>
    internal static (double Percent, double Bytes) Accelerated(IEnumerable<Sample> samples, string key)
    {
        double served = 0, fetched = 0;
        foreach (var sample in samples)
        {
            if (!sample.Layers.TryGetValue(key, out var layer)) continue;
            double seconds = sample.Seconds > 0 ? sample.Seconds : 1;
            served += layer.Accelerated / seconds;
            fetched += layer.Fetched / seconds;
        }
        double total = served + fetched;
        return (total == 0 ? 0 : 100.0 * served / total, served);
    }
}
