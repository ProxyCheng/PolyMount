namespace PolyMount.Core;

/// <summary>Stable instance identity for multiple providers of the same type.</summary>
internal class NamedLayer(string name, ILayer inner) : ILayer
{
    internal ILayer Inner => inner;
    public string Name => name;
    public string Generation => inner.Generation;
    public bool Writable => inner.Writable;
    public bool TryReadObject(string path, out VirtualObject value) => ((IObjectLayer)inner).TryReadObject(path, out value);
    public bool TryApply(IReadOnlyList<ObjectChange> changes) => ((IObjectLayer)inner).TryApply(changes);
    public bool TryGetEntry(string rel, out VfsEntry entry) => inner.TryGetEntry(rel, out entry);
    public IReadOnlyList<VfsEntry> ListOwn(string rel) => inner.ListOwn(rel);
    public bool TryOpenRead(string rel, out VfsEntry entry, out Stream stream) => inner.TryOpenRead(rel, out entry, out stream);
}

internal sealed class NamedCacheLayer(string name, ICacheLayer inner) : NamedLayer(name, inner), ICacheLayer, IObjectCache
{
    public void Store(string rel, string version, VfsEntry entry, Stream content) => inner.Store(rel, version, entry, content);
    public void Invalidate() => inner.Invalidate();
    public void StoreObject(string path, VirtualObject value, Stream? content = null)
    {
        if (inner is IObjectCache cache) cache.StoreObject(path, value, content);
        else if (!value.Header.IsDir && content != null) inner.Store(path, inner.Generation, value.Header, content);
    }

    public void TouchObject(string path)
    {
        if (inner is IObjectCache cache) cache.TouchObject(path);
    }
}
