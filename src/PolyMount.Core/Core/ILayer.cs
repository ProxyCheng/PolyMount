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

/// <summary>
/// A layer that can open file content by the content version its source declared, rather than
/// only by the namespace generation. The version is opaque: the layer that resolved the entry
/// supplied it, and this layer only compares it. A source that cannot name one leaves
/// <see cref="VfsEntry.ContentVersion"/> null, the caller does not ask, and the chain keeps its
/// generation-keyed behaviour — nothing here knows which backend answered.
/// </summary>
public interface IContentVersionCache
{
    /// <param name="header">The entry the resolver just resolved. Its
    /// <see cref="VfsEntry.ContentVersion"/> names the bytes being asked for; an implementation
    /// must not answer with content of a different size under it.</param>
    bool TryOpenVersioned(string path, VfsEntry header, out VfsEntry entry, out Stream stream);
}

/// <summary>A layer that needs the chain beneath it once the whole chain is built, so it can
/// fetch through layers it does not know by name. The mount hands it a resolver over those
/// layers and nothing else; what it does with it is the layer's business.</summary>
public interface ILowerChainLayer
{
    void Attach(VirtualFileSystem lower);
}

/// <summary>A layer whose work is not a request: it runs in the background between a mount
/// and an unmount. The mount starts and stops it without knowing what it does, so a layer
/// can be added to a chain without the mount depending on that layer's definition.</summary>
public interface IMountAwareLayer
{
    void Attach(VirtualFileSystem lower, Func<TimeSpan> idleFor);
    void Start();
    void Stop();
    /// <summary>Progress for the instance status, or null when there is nothing to report.</summary>
    LayerProgress? Progress { get; }
}

/// <summary>What a background layer reports about the pass it is running.</summary>
public sealed record LayerProgress(long Completed, long Total, LocalizedText Detail);

/// <summary>A layer that owns its chain's directory listings.
///
/// Listings are the one thing capacity eviction cannot be allowed to take: a directory is the
/// ancestor chain of everything beneath it, so losing one costs a source round trip for the
/// directory and makes every path below it unreachable. The layer that keeps them says so here,
/// and the resolver stops handing a second copy of every listing to the caches below it — their
/// capacity is for content, and a copy protected by the namespace rule is capacity they can
/// never use.
/// </summary>
public interface INamespaceStore
{
}

// Marker for data sources backed by the local filesystem. Such sources do not benefit from a
// read-through disk cache (the data is already local), so the mount chain omits the cache layer.
public interface ILocalSource
{
}
