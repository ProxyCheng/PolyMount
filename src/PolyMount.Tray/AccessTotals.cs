using PolyMount.Core.Ipc;

namespace PolyMount.Tray;

/// <summary>The access section's window, built the way the charts' windows are: from this page's
/// own readings.
///
/// The instance can only answer about a minute of its own record — its rings are that deep — so the
/// page asks it for the interval since the last answer and keeps those answers. The table and the
/// two drawings are then a sum of the answers inside the range the reader chose, which is the same
/// rule the trend charts follow and the only way a fifteen-minute range can be answered at all.
/// Each answer's counts are amounts over its own interval rather than rates, so adding them is what
/// they are for.
///
/// The merge is where the dimensions of the section are kept together: a layer's totals are the
/// column totals of the caller rows, and the service's own work is the row that makes them add up.
/// Shares and averages are recomputed here because the instance computed them per answer, and an
/// average of averages is not the average of the window.</summary>
internal static class AccessTotals
{
    /// <summary>The answers whose windows end inside <paramref name="span"/> seconds before
    /// <paramref name="end"/>, added up per mount. A mount that only some of them name is still
    /// reported, with what the answers that did name it counted.</summary>
    public static List<MountMetrics> Sum(
        IReadOnlyList<(DateTime At, List<MountMetrics> Entries)> history, DateTime end, double span)
    {
        DateTime from = end.AddSeconds(-span);
        var order = new List<string>();
        var sums = new Dictionary<string, MountMetrics>(StringComparer.OrdinalIgnoreCase);
        var layers = new Dictionary<string, Dictionary<string, LayerMetric>>(StringComparer.OrdinalIgnoreCase);
        // The layers of a mount in the order its answers name them, which is the mount's own chain:
        // top of the stack first. The table, the circle and the four stacked trend cards are all read
        // in this order, so a layer keeps its band and its row whatever the traffic does. It was
        // ranked by hits once, which made a band move every time a busier layer overtook it and left
        // these cards in an order the mount's own provider list — what the held cards are drawn
        // from — did not share.
        var layerOrder = new Dictionary<string, List<LayerMetric>>(StringComparer.OrdinalIgnoreCase);
        var callers = new Dictionary<string, List<ProcessMetric>>(StringComparer.OrdinalIgnoreCase);
        var unattributed = new Dictionary<string, List<ProcessLayerCount>>(StringComparer.OrdinalIgnoreCase);
        var cached = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var costMs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        DateTime first = default, last = default;
        double firstWindow = 1;
        int covered = 0;

        foreach (var (at, entries) in history)
        {
            if (at < from) continue;
            if (covered++ == 0)
            {
                first = at;
                // The answer's own window, which is what the oldest row inside the range covers —
                // it started before that row's moment, and the span they add up to includes it.
                firstWindow = entries.Count > 0 ? entries[0].WindowSeconds : 1;
            }
            last = at;
            foreach (var entry in entries)
            {
                if (!sums.TryGetValue(entry.Mount, out var sum))
                {
                    order.Add(entry.Mount);
                    sums[entry.Mount] = sum = new MountMetrics { Mount = entry.Mount };
                    layers[entry.Mount] = new Dictionary<string, LayerMetric>(StringComparer.OrdinalIgnoreCase);
                    layerOrder[entry.Mount] = new List<LayerMetric>();
                    callers[entry.Mount] = new List<ProcessMetric>();
                    unattributed[entry.Mount] = new List<ProcessLayerCount>();
                    cached[entry.Mount] = 0;
                    costMs[entry.Mount] = 0;
                }
                sum.WindowSeconds = span;
                if (entry.PredictiveWasted != null) sum.PredictiveWasted = entry.PredictiveWasted;
                sum.TotalReads += entry.TotalReads;
                sum.TotalWrites += entry.TotalWrites;
                sum.RequestCount += entry.RequestCount;
                sum.RequestMs += entry.RequestMs;
                // The instance reports the two derived figures rather than the amounts behind them,
                // so they are recovered here: an average times the reads it was taken over, and a
                // percentage of them.
                cached[entry.Mount] += entry.TotalReads * entry.CacheHitPercent / 100;
                costMs[entry.Mount] += entry.TotalReads * entry.AvgCostMs;

                foreach (var layer in entry.Layers)
                {
                    if (!layers[entry.Mount].TryGetValue(layer.Name, out var into))
                    {
                        layers[entry.Mount][layer.Name] = into = new LayerMetric { Name = layer.Name };
                        layerOrder[entry.Mount].Add(into);
                    }
                    into.Hits += layer.Hits;
                    into.Writes += layer.Writes;
                    into.WriteBytes += layer.WriteBytes;
                    into.Bytes += layer.Bytes;
                    into.HeldUsed += layer.HeldUsed;
                    into.HeldWasted += layer.HeldWasted;
                    into.HeldCount += layer.HeldCount;
                    into.HeldWastedBytes += layer.HeldWastedBytes;
                    if (layer.MaxCostMs > into.MaxCostMs) into.MaxCostMs = layer.MaxCostMs;
                    // A running amount for now; turned into an average once every answer is in.
                    into.AvgCostMs += layer.AvgCostMs * layer.Hits;
                    if (!string.IsNullOrEmpty(layer.DisplayName)) into.DisplayName = layer.DisplayName;
                    if (!string.IsNullOrEmpty(layer.Color)) into.Color = layer.Color;
                    into.Predictive |= layer.Predictive;
                    into.Accelerating |= layer.Accelerating;
                    // Lifetime totals rather than window amounts: the newest answer is the reading.
                    into.AcceleratedBytes = layer.AcceleratedBytes;
                    into.FetchedBytes = layer.FetchedBytes;
                }
                // The caller rows are folded once, by the card, over every mount at once — folding
                // them here by this mount's own order could drop a row the card's order keeps.
                callers[entry.Mount].AddRange(entry.Processes);
                unattributed[entry.Mount].AddRange(entry.Unattributed);
            }
        }

        var result = new List<MountMetrics>(order.Count);
        foreach (string mount in order)
        {
            var sum = sums[mount];
            var rows = layerOrder[mount];
            long events = rows.Sum(layer => layer.Hits + layer.Writes);
            foreach (var layer in rows)
            {
                layer.AvgCostMs = layer.Hits > 0 ? layer.AvgCostMs / layer.Hits : 0;
                layer.SharePercent = sum.TotalReads == 0 ? 0 : 100.0 * layer.Hits / sum.TotalReads;
                layer.AccessSharePercent = events == 0 ? 0 : 100.0 * (layer.Hits + layer.Writes) / events;
                sum.Layers.Add(layer);
            }
            sum.CacheHitPercent = sum.TotalReads == 0 ? 0 : 100.0 * cached[mount] / sum.TotalReads;
            sum.AvgCostMs = sum.TotalReads == 0 ? 0 : costMs[mount] / sum.TotalReads;
            sum.Processes = callers[mount];
            sum.Unattributed = unattributed[mount];
            // What was watched, not what was asked for: the answers' own span, which is shorter
            // than the range until the page has watched a whole range.
            sum.MeasuredSeconds = covered == 0
                ? 0
                : Math.Min(span, Math.Max(0, (last - first).TotalSeconds) + Math.Max(0, firstWindow));
            result.Add(sum);
        }
        return result;
    }
}
