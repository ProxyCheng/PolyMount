namespace PolyMount.Plugins;

/// <summary>Which prediction produced a read-ahead entry. The layer counts hits and waste
/// apart per origin, so a stride or a spatial batch that stops paying off can be told from the
/// successor table without giving either a second confidence model.</summary>
public enum PreloadOrigin : byte
{
    Table = 0,
    Sibling = 1,
    Spatial = 2,
    Burst = 3
}
