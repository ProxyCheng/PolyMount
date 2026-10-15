using System.Text.Json;

namespace PolyMount.Plugins.Svn;

/// <summary>
/// Speculative directory-listing read-ahead. Every directory enumeration is one backend
/// round trip and a walk enumerates its directories one after another, so a cold walk of n
/// directories costs n round trips end to end; measured against the L10 repository one
/// listing is about 360 ms, of which the layer itself accounts for a few. Fetching the next
/// level while the caller is still consuming the current one overlaps those round trips
/// instead of removing them.
/// </summary>
public sealed class SvnPrefetchOptions
{
    /// <summary>Directories fetched at once. Zero disables read-ahead.</summary>
    public int Concurrency { get; set; } = 2;

    /// <summary>Directories that may wait for a worker; enqueues beyond this are dropped.</summary>
    public int QueueDepth { get; set; } = 16;

    /// <summary>Child directories taken from one listing.</summary>
    public int Children { get; set; } = 8;

    /// <summary>
    /// The most of the source read-ahead may hold at once. One request is kept for it whatever
    /// the reader is doing, so this is what it may reach beyond that when the reader is quiet.
    /// <para>
    /// A walk is a burst of speculative requests against a source the reader is also using.
    /// Sharing one budget between them is what puts a speculative request in front of a demanded
    /// one — measured on L10, a listing costs about 360 ms of which the layer itself is a few,
    /// so a speculative request holding a connection or a process is 360 ms the reader's request
    /// is not being served. The reader's class keeps the rest of the source, so this bounds
    /// read-ahead's appetite without ever being the reason a reader waits.
    /// </para>
    /// </summary>
    public int SpeculativeConcurrency { get; set; } = 2;

    public static SvnPrefetchOptions From(IReadOnlyDictionary<string, JsonElement> parameters)
    {
        var options = new SvnPrefetchOptions
        {
            Concurrency = Number(parameters, "PrefetchConcurrency", 2),
            QueueDepth = Number(parameters, "PrefetchQueueDepth", 16),
            Children = Number(parameters, "PrefetchChildren", 8),
            SpeculativeConcurrency = Number(parameters, "SpeculativeConcurrency", 2)
        };
        if (options.Concurrency is < 0 or > 8) throw new ArgumentException("PrefetchConcurrency must be 0-8");
        if (options.QueueDepth is < 1 or > 1024) throw new ArgumentException("PrefetchQueueDepth must be 1-1024");
        if (options.Children is < 0 or > 256) throw new ArgumentException("PrefetchChildren must be 0-256");
        // The reader's class is guaranteed the rest of the source, so read-ahead may claim at
        // most half of it and the reader still keeps the larger half.
        if (options.SpeculativeConcurrency is < 1 or > 8) throw new ArgumentException("SpeculativeConcurrency must be 1-8");
        return options;
    }

    private static int Number(IReadOnlyDictionary<string, JsonElement> parameters, string key, int fallback) =>
        parameters.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32() : fallback;
}
