namespace PolyMount.Core;

public interface ILayer : IObjectLayer
{
    string Name { get; }
    string Generation { get; }
    bool Writable { get; }

    bool TryGetEntry(string rel, out VfsEntry entry);
    IReadOnlyList<VfsEntry> ListOwn(string rel);
    bool TryOpenRead(string rel, out VfsEntry entry, out Stream stream);

    bool IObjectLayer.TryReadObject(string path, out VirtualObject value)
    {
        value = null!;
        if (!TryGetEntry(path, out var header)) return false;
        value = new VirtualObject(header, header.IsDir ? ListOwn(path) : null);
        return true;
    }

    bool IObjectLayer.TryApply(IReadOnlyList<ObjectChange> changes) => false;
}

public interface ICacheLayer : ILayer
{
    void Store(string rel, string version, VfsEntry entry, Stream content);
    void Invalidate();
}

// Marker for data sources backed by the local filesystem. Such sources do not benefit from a
// read-through disk cache (the data is already local), so the mount chain omits the cache layer.
public interface ILocalSource
{
}
