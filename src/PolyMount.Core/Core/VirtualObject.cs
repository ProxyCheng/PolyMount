namespace PolyMount.Core;

/// <summary>A complete namespace object. Directory children are authoritative, not a delta.</summary>
public sealed record VirtualObject(VfsEntry Header, IReadOnlyList<VfsEntry>? Children = null)
{
    public static VirtualObject Directory(string path, IEnumerable<VfsEntry> children) => new(
        new VfsEntry { Name = VfsPath.Name(path), IsDir = true, TimeUtc = DateTime.UtcNow }, children.ToArray());
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
}

/// <summary>Remove this layer's record without deleting the virtual object in lower layers.</summary>
public interface IObjectManagement
{
    void ReleaseObject(string path);
}

public sealed record LayerStorageUsage(long DiskBytes, long ObjectBytes, int FileObjects, int DirectoryObjects,
    long CacheBytes = 0, long CacheCapacity = 0, long DurableBytes = 0, int DurableObjects = 0, long MemoryBytes = 0);

public interface ILayerStorageUsage
{
    LayerStorageUsage GetStorageUsage();
}
