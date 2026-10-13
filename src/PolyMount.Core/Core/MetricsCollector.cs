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
    private readonly record struct Sample(long TicksUtc, string Layer, double CostMs, long Bytes, bool Cached, bool Write);

    private readonly int _capacity;
    private readonly ConcurrentQueue<Sample> _samples = new();
    private int _count;

    public MetricsCollector(int capacity = 5000) => _capacity = capacity;

    public void RecordRead(string layer, TimeSpan elapsed, long bytes, bool cached = false)
    {
        Record(layer, elapsed, bytes, cached, false);
    }
    public void RecordWrite(string layer, TimeSpan elapsed, long bytes) => Record(layer, elapsed, bytes, false, true);
    private void Record(string layer, TimeSpan elapsed, long bytes, bool cached, bool write)
    {
        _samples.Enqueue(new Sample(DateTime.UtcNow.Ticks, layer, elapsed.TotalMilliseconds, bytes, cached, write));
        if (Interlocked.Increment(ref _count) > _capacity)
            if (_samples.TryDequeue(out _)) Interlocked.Decrement(ref _count);
    }

    public MountMetrics Snapshot(string mountName, int windowSeconds)
    {
        if (windowSeconds <= 0) windowSeconds = 300;
        long cutoff = DateTime.UtcNow.AddSeconds(-windowSeconds).Ticks;

        var recent = _samples.ToArray().Where(s => s.TicksUtc >= cutoff).ToArray();
        var reads = recent.Where(s => !s.Write).ToArray();
        var result = new MountMetrics { Mount = mountName, WindowSeconds = windowSeconds, TotalReads = reads.Length, TotalWrites = recent.Length - reads.Length };
        if (recent.Length == 0) return result;

        result.AvgCostMs = reads.Length == 0 ? 0 : reads.Average(s => s.CostMs);

        long cacheHits = 0;
        foreach (var g in recent.GroupBy(s => s.Layer, StringComparer.OrdinalIgnoreCase))
        {
            var arr = g.ToArray();
            var layerReads = arr.Where(s => !s.Write).ToArray();
            cacheHits += arr.Count(s => s.Cached);
            result.Layers.Add(new LayerMetric
            {
                Name = g.Key,
                Hits = layerReads.Length,
                Writes = arr.Length - layerReads.Length,
                AccessSharePercent = 100.0 * arr.Length / recent.Length,
                SharePercent = reads.Length == 0 ? 0 : 100.0 * layerReads.Length / reads.Length,
                AvgCostMs = layerReads.Length == 0 ? 0 : layerReads.Average(s => s.CostMs),
                MaxCostMs = layerReads.Length == 0 ? 0 : layerReads.Max(s => s.CostMs),
                Bytes = layerReads.Sum(s => s.Bytes),
                WriteBytes = arr.Where(s => s.Write).Sum(s => s.Bytes)
            });
        }

        result.Layers = result.Layers.OrderByDescending(l => l.Hits).ToList();
        result.CacheHitPercent = reads.Length == 0 ? 0 : 100.0 * cacheHits / reads.Length;
        return result;
    }
}
