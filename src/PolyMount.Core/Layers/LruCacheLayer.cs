using PolyMount.Core;

namespace PolyMount.Layers;

/// <summary>Shared object KV storage with evictable read-through data and durable user writes.</summary>
public sealed class LruCacheLayer : ICacheLayer, IObjectCache, IObjectManagement, ILayerStorageUsage
{
    private readonly ObjectDatabase _reads, _writes;
    private readonly Func<string> _version;
    public LruCacheLayer(string root, Func<string> version, long capacityBytes, bool compression = false)
    {
        _version = version; Capacity = capacityBytes;
        _reads = new(Path.Combine(root, "reads"), compression, recoverable: true);
        _writes = new(Path.Combine(root, "writes"), compression);
    }
    private string Key(string path) => Generation + "|" + path;
    public string Name => "cache";
    public string Generation => _version();
    public bool Writable => true;
    public bool HasDurableWrites => _writes.Records.Count > 0;
    public long Capacity { get; }
    public long TotalBytes => _reads.Records.Sum(r => r.StoredBytes);
    public int Count => _reads.Records.Count;
    public bool TryReadObject(string path, out VirtualObject value) => _writes.TryGet(path, out value) || _reads.TryGet(Key(path), out value);
    public bool TryApply(IReadOnlyList<ObjectChange> changes) { _writes.Apply(changes); return true; }
    public void StoreObject(string path, VirtualObject value, Stream? content = null)
    {
        _reads.Apply(new[] { new ObjectChange(Key(path), value, content) });
        while (TotalBytes > Capacity && _reads.Records.Count > 0)
        {
            var oldest = _reads.Records.MinBy(r => r.Access)!;
            _reads.Apply(new[] { new ObjectChange(oldest.Path, null) });
        }
    }
    public bool TryGetEntry(string path, out VfsEntry entry)
    { bool found = TryReadObject(path, out var value); entry = found ? value.Header : null!; return found; }
    public IReadOnlyList<VfsEntry> ListOwn(string path) => TryReadObject(path, out var value) ? value.Children ?? Array.Empty<VfsEntry>() : Array.Empty<VfsEntry>();
    public bool TryOpenRead(string path, out VfsEntry entry, out Stream stream)
    {
        bool found = _writes.TryOpen(path, out var value, out stream) || _reads.TryOpen(Key(path), out value, out stream);
        entry = found ? value.Header : null!; return found;
    }
    public void Store(string path, string version, VfsEntry entry, Stream content) => StoreObject(path, new(entry), content);
    public void Invalidate() => _reads.Clear();
    public void Purge() => _reads.Clear();
    public void InvalidateEntry(string path) => _reads.Apply(new[] { new ObjectChange(Key(path), null) });
    public void ReleaseObject(string path)
    {
        _writes.Apply(new[] { new ObjectChange(path, null) });
        InvalidateEntry(path);
    }
    public LayerStorageUsage GetStorageUsage()
    {
        var reads = _reads.GetStorageUsage();
        var writes = _writes.GetStorageUsage();
        return new(reads.DiskBytes + writes.DiskBytes, reads.ObjectBytes + writes.ObjectBytes,
            reads.FileObjects + writes.FileObjects, reads.DirectoryObjects + writes.DirectoryObjects,
            reads.ObjectBytes, Capacity, writes.ObjectBytes, writes.FileObjects + writes.DirectoryObjects);
    }
}
