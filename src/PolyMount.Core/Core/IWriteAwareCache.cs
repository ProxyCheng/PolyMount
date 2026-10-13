namespace PolyMount.Core;

/// <summary>A recoverable object cache that can track successful lower-layer writes.</summary>
public interface IWriteAwareCache : IObjectCache
{
    void UpdateAfterWrite(IReadOnlyList<ObjectChange> changes);
}
