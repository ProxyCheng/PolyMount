using PolyMount.Core;

namespace PolyMount.Layers;

/// <summary>
/// Read-through cache for file content and the directory namespace. Namespace records
/// are never evicted from the store; content is bounded by capacity.
/// </summary>
public sealed class LruCacheLayer : DurableCacheLayer, IWriteAwareCache, IObjectManagement, ILayerStorageUsage, IContentVersionCache
{
    private sealed class ContentItem(VirtualObject value, long bytes, ObjectDatabase.Record? record = null)
        : Item(value, bytes, record)
    {
        public CacheContent? Content;
        public CacheContent? OpenContent;
        public LinkedListNode<Item>? OpenNode;
    }

    private const int MaxOpenFiles = 2048;
    private const long MaxOpenBytes = 64L * 1024 * 1024;

    private readonly ObjectDatabase _writes;
    private readonly bool _writable;
    private int _openCount;
    private readonly LinkedList<Item> _openLru = new();
    private long _openBytes;

    public LruCacheLayer(string root, Func<string> version, long capacityBytes, bool compression = false,
        long metadataBudget = CacheBudgets.MetadataBytes, bool writable = true)
        : base(new ObjectDatabase(Path.Combine(root, "reads"), compression, recoverable: true,
                preferredPrefix: version() + "|", metadataBudget: metadataBudget),
            Path.Combine(root, "reads", "objects-v2"), version, capacityBytes, metadataBudget,
            _ => true)
    {
        _writes = new(Path.Combine(root, "writes"), compression);
        _writable = writable;
    }

    public override string Name => "cache";
    /// <summary>Whether this cache is also the chain's writable layer. A read-through cache
    /// placed above an overlay is not: the overlay owns writes, and accepting one here would
    /// keep it from ever reaching the overlay.</summary>
    public override bool Writable => _writable;
    public bool HasDurableWrites => _writable && _writes.Count > 0;
    public int RetainedReadHandles { get { lock (Gate) return _openCount; } }

    private protected override Item NewItem(VirtualObject value, long bytes, ObjectDatabase.Record? record = null) =>
        new ContentItem(value, bytes, record);

    private protected override bool Prepare(Item item, VirtualObject source, Stream? content)
    {
        var payload = source.Content;
        var captured = payload.RetainBytes(content);
        if (payload is FileObjectContent && captured == null) return false;
        ((ContentItem)item).Content = captured;
        return true;
    }

    private protected override void ReleaseItem(Item item)
    {
        var entry = (ContentItem)item;
        entry.Content?.Dispose();
        entry.Content = null;
        ReleaseOpen(entry);
    }

    private protected override void SettleItem(Item item)
    {
        var entry = (ContentItem)item;
        entry.Content?.Dispose();
        entry.Content = null;
    }

    private protected override Stream? AcquireContent(Item item) => ((ContentItem)item).Content?.Acquire();

    private protected override void OnTouch(Item item)
    {
        var entry = (ContentItem)item;
        if (entry.OpenNode == null) return;
        _openLru.Remove(entry.OpenNode);
        _openLru.AddFirst(entry.OpenNode);
    }

    /// <summary>A record is usable only while its blob is still on disk.</summary>
    private protected override bool IsBacked(Item item) =>
        item.Record?.Blob == null || File.Exists(Path.Combine(StoreRoot, item.Record.Blob));

    /// <summary>
    /// Content is evicted to keep the store within its capacity, but namespace records
    /// are never deleted: the whole ancestor chain has to stay resolvable, and losing one
    /// directory costs a source fetch for every path beneath it. Dropping them from the
    /// index alone is free because TryReadObject reads them back from the store.
    /// </summary>
    private protected override string[] ObsoleteKeys(IReadOnlyCollection<string> evicted) =>
        evicted.Where(key => !IsResidentStoreKey(key)).ToArray();

    private void ReleaseOpen(ContentItem content)
    {
        if (content.OpenContent == null) return;
        _openCount--;
        _openBytes -= content.OpenContent.Length;
        _openLru.Remove(content.OpenNode!);
        content.OpenNode = null;
        content.OpenContent.Dispose();
        content.OpenContent = null;
    }

    public override bool TryApply(IReadOnlyList<ObjectChange> changes) { if (!_writable) return false; _writes.Apply(changes); return true; }

    /// <summary>A lower layer accepted a write, so this cache's copy of the changed paths is
    /// stale. Only those paths are dropped; the rest of the read cache survives a save.</summary>
    public void UpdateAfterWrite(IReadOnlyList<ObjectChange> changes) =>
        InvalidateEntries(changes.Select(change => change.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

    public override bool TryOpenRead(string path, out VfsEntry entry, out Stream stream)
    {
        // A read-only cache does not own this path even if an older chain left user writes in
        // the store: the overlay below is the authority now.
        if (_writable && _writes.TryOpen(path, out var durable, out stream)) { entry = durable.Header; return true; }
        var key = ResidentKey(path);
        ObjectDatabase.Record? record;
        lock (Gate)
        {
            entry = null!; stream = null!;
            if (!TryGetResident(key, out var found) || found.Value.Header.IsDir) return false;
            var item = (ContentItem)found;
            entry = item.Value.Header;
            if (item.Content != null) { stream = item.Content.Acquire()!; return stream != null; }
            if (item.OpenContent != null) { stream = item.OpenContent.Acquire()!; return stream != null; }
            record = item.Record;
        }
        // Content and an open handle were both checked above, so a durable record is all
        // that is left to read from. An entry that has neither is a cache miss, not a
        // reason to throw inside the provider chain.
        if (record?.Blob == null) return false;
        try
        {
            if (!Reads.TryOpenRecord(record, out stream)) return false;
            if (record.Compressed || stream is not FileStream file || file.Length > MaxOpenBytes) return true;
            // Open outside the index lock. Publish a shared immutable handle only
            // if this record is still current; each reader has an independent offset.
            var opened = CacheContent.Own(file);
            lock (Gate)
            {
                if (IsDisposed || !TryGetResident(key, out var current) || current.Record != record)
                {
                    stream = Detach(opened);
                    return true;
                }
                var entryItem = (ContentItem)current;
                if (entryItem.OpenContent != null)
                {
                    opened.Dispose();
                    stream = entryItem.OpenContent.Acquire()!;
                    return true;
                }
                while (_openCount >= MaxOpenFiles || _openBytes + opened.Length > MaxOpenBytes)
                    ReleaseOpen((ContentItem)_openLru.Last!.Value);
                entryItem.OpenContent = opened;
                entryItem.OpenNode = _openLru.AddFirst(current);
                _openCount++; _openBytes += opened.Length;
                stream = opened.Acquire()!;
                return true;
            }
        }
        catch (FileNotFoundException) { ForgetRecord(key, record); return false; }
        catch (DirectoryNotFoundException) { ForgetRecord(key, record); return false; }
    }

    /// <summary>
    /// Serves a file from content this cache already holds under the version its source
    /// declared, even though the namespace key it was admitted under has moved on. The header
    /// the resolver just got is the authority on what is being asked for, so a record whose
    /// size disagrees is not that content and the caller falls through to the source.
    /// </summary>
    public bool TryOpenVersioned(string path, VfsEntry header, out VfsEntry entry, out Stream stream)
    {
        entry = null!;
        stream = null!;
        if (header.ContentVersion is not { Length: > 0 } version) return false;
        if (!TryGetVersioned(path, version, out var item, out var record)) return false;
        if (record.Value.Header.Size != header.Size) return false;
        try
        {
            if (!Reads.TryOpenRecord(record, out stream)) return false;
            entry = record.Value.Header;
            return true;
        }
        catch (FileNotFoundException) { ForgetVersioned(item); return false; }
        catch (DirectoryNotFoundException) { ForgetVersioned(item); return false; }
    }

    private static Stream Detach(CacheContent opened)
    {
        var stream = opened.Acquire()!;
        opened.Dispose();
        return stream;
    }

    private void ForgetRecord(CacheKey key, ObjectDatabase.Record? record)
    {
        lock (Gate) if (TryGetResident(key, out var item) && item.Record == record) RemoveResident(key);
    }

    public void ReleaseObject(string path)
    {
        _writes.Apply(new[] { new ObjectChange(path, null) });
        InvalidateEntry(path);
    }

    public override void Dispose()
    {
        base.Dispose();
        // A writable cache owns a second store for the writes it accepted itself.
        try { _writes.Dispose(); } catch (Exception) { }
    }

    public LayerStorageUsage GetStorageUsage()
    {
        var reads = Reads.GetStorageUsage();
        var writes = _writes.GetStorageUsage();
        return new(reads.DiskBytes + writes.DiskBytes, reads.ObjectBytes + writes.ObjectBytes,
            reads.FileObjects + writes.FileObjects, reads.DirectoryObjects + writes.DirectoryObjects,
            TotalBytes, Capacity, writes.ObjectBytes, writes.FileObjects + writes.DirectoryObjects,
            Lookups: reads.Lookups);
    }
}
