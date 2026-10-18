namespace PolyMount.Core;

/// <summary>A recoverable object cache that can track successful lower-layer writes.</summary>
public interface IWriteAwareCache : IObjectCache
{
    void UpdateAfterWrite(IReadOnlyList<ObjectChange> changes);
}

/// <summary>A layer that holds read-ahead data but never accepts a write. When a lower layer
/// accepts one, the changed paths (and, for a deletion, their subtree) are stale in this layer;
/// it drops only those, rather than clearing everything it holds.</summary>
public interface IWriteInvalidation
{
    void InvalidateAfterWrite(IReadOnlyList<ObjectChange> changes);
}

/// <summary>A layer that can say whether it holds a path without that counting as a use of it.
/// Inspection (the layer view, Explorer's origin badges every few seconds) must not raise a
/// read-ahead layer's hit rate or keep an entry warm that nobody is reading.</summary>
public interface IQuietProbe
{
    bool TryPeekEntry(string path, out VfsEntry entry);
}
