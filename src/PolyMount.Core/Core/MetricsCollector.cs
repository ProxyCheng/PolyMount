using System.Collections.Concurrent;
using PolyMount.Core.Ipc;

namespace PolyMount.Core;

/// <summary>
/// Thread-safe, bounded, rolling-window collector of read events used to compute per-layer
/// hit rates and access cost. Each read records which layer served it and how long resolution
/// took (including probing the layers above the hit).
/// </summary>
public sealed class MetricsCollector
{
    private readonly record struct Sample(long TicksUtc, string Layer, double CostMs, long Bytes, bool Cached);

    private readonly int _capacity;
    private readonly ConcurrentQueue<Sample> _samples = new();
    private int _count;

    public MetricsCollector(int capacity = 5000) => _capacity = capacity;

    public void RecordRead(string layer, TimeSpan elapsed, long bytes, bool cached = false)
    {
        _samples.Enqueue(new Sample(DateTime.UtcNow.Ticks, layer, elapsed.TotalMilliseconds, bytes, cached));
        if (Interlocked.Increment(ref _count) > _capacity)
            if (_samples.TryDequeue(out _)) Interlocked.Decrement(ref _count);
    }

    public MountMetrics Snapshot(string mountName, int windowSeconds)
    {
        if (windowSeconds <= 0) windowSeconds = 300;
        long cutoff = DateTime.UtcNow.AddSeconds(-windowSeconds).Ticks;

        var recent = _samples.ToArray().Where(s => s.TicksUtc >= cutoff).ToArray();
        var result = new MountMetrics { Mount = mountName, WindowSeconds = windowSeconds, TotalReads = recent.Length };
        if (recent.Length == 0) return result;

        result.AvgCostMs = recent.Average(s => s.CostMs);

        long cacheHits = 0;
        foreach (var g in recent.GroupBy(s => s.Layer, StringComparer.OrdinalIgnoreCase))
        {
            var arr = g.ToArray();
            cacheHits += arr.Count(s => s.Cached);
            result.Layers.Add(new LayerMetric
            {
                Name = g.Key,
                Hits = arr.Length,
                SharePercent = 100.0 * arr.Length / recent.Length,
                AvgCostMs = arr.Average(s => s.CostMs),
                MaxCostMs = arr.Max(s => s.CostMs),
                Bytes = arr.Sum(s => s.Bytes)
            });
        }

        result.Layers = result.Layers.OrderByDescending(l => l.Hits).ToList();
        result.CacheHitPercent = 100.0 * cacheHits / recent.Length;
        return result;
    }
}
