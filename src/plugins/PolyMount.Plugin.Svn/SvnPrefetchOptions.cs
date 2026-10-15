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

    public static SvnPrefetchOptions From(IReadOnlyDictionary<string, JsonElement> parameters)
    {
        var options = new SvnPrefetchOptions
        {
            Concurrency = Number(parameters, "PrefetchConcurrency", 2),
            QueueDepth = Number(parameters, "PrefetchQueueDepth", 16),
            Children = Number(parameters, "PrefetchChildren", 8)
        };
        if (options.Concurrency is < 0 or > 8) throw new ArgumentException("PrefetchConcurrency must be 0-8");
        if (options.QueueDepth is < 1 or > 1024) throw new ArgumentException("PrefetchQueueDepth must be 1-1024");
        if (options.Children is < 0 or > 256) throw new ArgumentException("PrefetchChildren must be 0-256");
        return options;
    }

    private static int Number(IReadOnlyDictionary<string, JsonElement> parameters, string key, int fallback) =>
        parameters.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32() : fallback;
}
