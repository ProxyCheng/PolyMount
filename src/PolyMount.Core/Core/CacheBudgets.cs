namespace PolyMount.Core;

/// <summary>Per-instance recoverable metadata budgets, independent of disk capacity.</summary>
public static class CacheBudgets
{
    public const int MaxObjects = 32768;
    public const long MetadataBytes = 64L * 1024 * 1024;
    public static long Estimate(string path, VirtualObject value) => 512L + path.Length * 4L +
        value.Header.Name.Length * 2L + (value.Children?.Sum(c => 256L + c.Name.Length * 4L) ?? 0);
}
