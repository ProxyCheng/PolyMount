namespace PolyMount.Plugins;

/// <summary>Per-instance recoverable metadata budgets, independent of disk capacity.</summary>
public static class CacheBudgets
{
    // Directory listings dominate this budget and their cost grows with the child count,
    // so the ceiling has to clear the whole working tree rather than a sample of it.
    // Losing a single directory is not one miss: Visible() validates every ancestor, so
    // evicting a top-level folder re-fetches every path beneath it. A package tree of
    // ~20k directories charges roughly 155 MB here, against a 4 GB content cache, which
    // is why the earlier 64 MB / 32,768 limits thrashed on real repositories.
    public const int MaxObjects = 262144;
    public const long MetadataBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Upper bound on what a cached object costs in memory. Deliberately pessimistic:
    /// it is enforced as a bound, so it runs around 2-3x the footprint a directory
    /// snapshot actually occupies.
    /// </summary>
    public static long Estimate(string path, VirtualObject value) => 512L + path.Length * 4L +
        value.Header.Name.Length * 2L + value.Content.EstimatedMemoryBytes;
}
