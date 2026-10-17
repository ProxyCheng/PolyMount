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
