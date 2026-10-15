namespace PolyMount.Core;

/// <summary>A complete namespace object. Directory children are authoritative, not a delta.</summary>
public sealed record VirtualObject(VfsEntry Header, IReadOnlyList<VfsEntry>? Children = null)
{
    private ObjectContent? _payload;
    private VirtualObject(VirtualObject original)
    {
        Header = original.Header;
        Children = original.Children;
        // A record 'with' may replace Header/Children. Rebuild derived payload
        // instead of retaining an index from the original object.
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public ObjectContent Content
    {
        get
        {
            var current = Volatile.Read(ref _payload);
            if (current != null) return current;
            ObjectContent created = Header.IsDir
                ? new DirectoryObjectContent(Children ?? throw new InvalidDataException("Directory requires a complete listing"))
                : new FileObjectContent(Header.Size);
            return Interlocked.CompareExchange(ref _payload, created, null) ?? created;
        }
    }
    public VirtualObject Snapshot()
    {
        var content = Content.Snapshot();
        return new VirtualObject(new VfsEntry { Name = Header.Name, IsDir = Header.IsDir, Size = Header.Size, TimeUtc = Header.TimeUtc },
            content.Entries) { _payload = content };
    }
    public static VirtualObject WithContent(VfsEntry header, ObjectContent content)
    {
        if (header.IsDir != (content is DirectoryObjectContent)) throw new ArgumentException("Header and content kind differ");
        return new VirtualObject(header, content.Entries) { _payload = content };
    }
    public static VirtualObject Directory(string path, IEnumerable<VfsEntry> children)
    {
        var content = new DirectoryObjectContent(children);
        return new VirtualObject(new VfsEntry { Name = VfsPath.Name(path), IsDir = true, TimeUtc = DateTime.UtcNow },
            content.Entries) { _payload = content };
    }
}

/// <summary>One atomic namespace update. File contents are consumed during TryApply.</summary>
public sealed record ObjectChange(string Path, VirtualObject? Object, Stream? Content = null);

public interface IObjectLayer
{
    bool TryReadObject(string path, out VirtualObject value);
    // False means unsupported; operational failures must throw and must not fall through.
    bool TryApply(IReadOnlyList<ObjectChange> changes);
}

public interface IObjectCache
{
    void StoreObject(string path, VirtualObject value, Stream? content = null);

    /// <summary>
    /// Refreshes this cache's recency for a path that a higher layer served. Layers are
    /// consulted top down and the first hit wins, so without this an entry that a higher
    /// cache keeps answering never observes a read of its own and a recency-ordered
    /// eviction discards precisely the objects that are still in use.
    /// </summary>
    void TouchObject(string path);
}

/// <summary>Remove this layer's record without deleting the virtual object in lower layers.</summary>
public interface IObjectManagement
{
    void ReleaseObject(string path);
}

/// <summary>Where a store's object lookups spent their time. A directory listing probes every
/// layer above the provider once per child, so a lookup that costs milliseconds shows up as
/// seconds of listing latency; this splits that cost into the shared metadata gate, the
/// filesystem check that answers "is the store still there", and reading a record back. Counts
/// are cumulative since the store was opened, so a rate is <see cref="TotalUs"/> over
/// <see cref="Calls"/>. <see cref="ExistsChecks"/> is how many of those lookups actually ran the
/// check: it is meant to stay near one per second, not to track <see cref="Calls"/>.</summary>
public sealed record LookupProfile(long Calls, long TotalUs, long GateWaitUs, long ExistsUs, long ExistsChecks,
    long LoadUs, long MaxUs);

public sealed record LayerStorageUsage(long DiskBytes, long ObjectBytes, int FileObjects, int DirectoryObjects,
    long CacheBytes = 0, long CacheCapacity = 0, long DurableBytes = 0, int DurableObjects = 0, long MemoryBytes = 0,
    LookupProfile? Lookups = null);

public interface ILayerStorageUsage
{
    LayerStorageUsage GetStorageUsage();
}
