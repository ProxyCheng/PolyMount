using System.Diagnostics.Tracing;
using PolyMount.Core.Ipc;

namespace PolyMount.Core;

/// <summary>
/// What the operating system's own tools see of a running instance.
///
/// This is the counter-tracing model rather than a bespoke one: a <see cref="EventSource"/> is
/// invisible until something listens, and what it exposes here are <see cref="PollingCounter"/>s —
/// the consumer pulls, so a mount with nobody watching pays nothing at all for this being here.
/// (`dotnet-counters monitor --counters PolyMount -p &lt;worker pid&gt;` is the consumer.)
///
/// The numbers themselves are not recomputed for the occasion: each counter asks the same
/// collector the performance window asks, and asking arms it — the same rule the window follows,
/// that being asked is what turns counting on. A tool that polls an instance therefore measures
/// that instance, whether or not a window is open.
///
/// The other half of the story is not here at all: per-layer CPU comes from the runtime's own
/// sampling profiler (`dotnet-trace collect --profile cpu-sampling`), which needs no cooperation
/// from this process and shows every layer's methods in its frames. See docs/profiling.md.
/// </summary>
[EventSource(Name = "PolyMount")]
public sealed class PolyMountEventSource : EventSource
{
    public static readonly PolyMountEventSource Log = new();

    private readonly object _gate = new();
    private readonly List<IDisposable> _registered = new();
    private int _instances;

    /// <summary>How many instances this process hosts, which for a worker is one and for the
    /// supervisor is however many workers it is running.</summary>
    public int Instances => Volatile.Read(ref _instances);

    /// <summary>Publishes one instance's numbers. Called when an instance is built; the counters
    /// live until it is disposed.</summary>
    internal void Register(MountInstance instance)
    {
        if (!IsEnabled()) return;
        var counters = new List<IDisposable>();
        void Counter(string name, string display, Func<double> value) =>
            counters.Add(new PollingCounter(name, this, value)
            {
                DisplayName = display,
                DisplayUnits = ""
            });

        var owner = instance;
        // Reading the instance the same way the window does - and arming it, because being asked is
        // what turns counting on. The window is a second, which is what these counters describe.
        MountMetrics Read(double window = 1) => owner.GetMetrics(window);

        Counter("reads", "Reads served", () => Read().TotalReads);
        Counter("writes", "Writes served", () => Read().TotalWrites);
        Counter("bytes", "Bytes resolved by the layers", () => Read().Layers.Sum(layer => (double)layer.Bytes));
        Counter("write-bytes", "Bytes written by the layers", () => Read().Layers.Sum(layer => (double)layer.WriteBytes));
        Counter("cache-hit-percent", "Reads answered by a cache layer", () => Read().CacheHitPercent);
        Counter("avg-cost-ms", "Average read-resolution time", () => Read().AvgCostMs);
        Counter("requests", "Requests taken at the file system boundary", () => Read().RequestCount);
        Counter("request-ms", "Time those requests took", () => Read().RequestMs);
        // The difference between the two: time in requests no layer answered for - the negative
        // lookups an application makes - and in the paths whose time is not recorded per layer.
        Counter("unattributed-ms", "Request time no layer accounts for",
            () => Read().RequestMs - Read().Layers.Sum(layer => layer.AvgCostMs * layer.Hits));
        foreach (var layer in owner.Vfs.Layers)
        {
            string name = layer.Name;
            Counter("layer." + name + ".hits", "Reads served by " + name,
                () => Read().Layers.FirstOrDefault(l => l.Name == name)?.Hits ?? 0);
            Counter("layer." + name + ".cost-ms", "Average resolution time in " + name,
                () => Read().Layers.FirstOrDefault(l => l.Name == name)?.AvgCostMs ?? 0);
        }

        lock (_gate) _registered.AddRange(counters);
        Interlocked.Increment(ref _instances);
    }

    /// <summary>Stops publishing. Called when the instance goes away, so a tool that keeps polling
    /// is not answered from a collector nothing writes to any more.</summary>
    internal void Unregister()
    {
        lock (_gate)
        {
            foreach (var counter in _registered) counter.Dispose();
            _registered.Clear();
        }
        Interlocked.Decrement(ref _instances);
    }
}
