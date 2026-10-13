namespace PolyMount.Core;

public interface ILayer
{
    string Name { get; }
    string Generation { get; }
    bool Writable { get; }

    bool IsDeleted(string rel);
    bool TryGetEntry(string rel, out VfsEntry entry);
    IReadOnlyList<VfsEntry> ListOwn(string rel);
    bool TryOpenRead(string rel, out VfsEntry entry, out Stream stream);

    Stream Create(string rel, out VfsEntry entry);
    Stream OpenExistingWrite(string rel, out VfsEntry entry);
    void CreateDirectory(string rel);
    void Delete(string rel, bool shadowedBelow);
    void Rename(string rel, string dst, bool replace, bool shadowedBelow);
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
