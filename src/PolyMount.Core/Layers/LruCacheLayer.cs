using System.Threading.Channels;
using PolyMount.Core;

namespace PolyMount.Layers;

/// <summary>Immediate memory publication with best-effort background read-cache persistence.</summary>
public sealed class LruCacheLayer : CacheLayer, ICacheLayer, IObjectCache, IObjectManagement, ILayerStorageUsage, IDisposable
{
    private sealed class Item(VirtualObject value, long bytes, CacheContent? content = null, ObjectDatabase.Record? record = null)
    {
        public VirtualObject Value = value;
        public long Bytes = bytes, Access = System.Diagnostics.Stopwatch.GetTimestamp();
        public CacheContent? Content = content;
        public CacheContent? OpenContent;
        public LinkedListNode<Item>? OpenNode;
        public ObjectDatabase.Record? Record = record;
        public long Reservation = bytes;
        public long Metadata;
    }
    private readonly ObjectDatabase _reads, _writes;
    private readonly Func<string> _version;
    private readonly string _readRoot;
    private readonly object _gate = new(), _persistGate = new();
    private readonly Dictionary<string, Item> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<(string Key, Item Item)> _queue = Channel.CreateBounded<(string, Item)>(
        new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private Task? _worker;
    private readonly Timer _storageCheck;
    private int _checkingStorage;
    private int _storageCursor;
    private long _bytes, _pendingBytes;
    private long _metadataBytes;
    private int _openCount;
    private readonly LinkedList<Item> _openLru = new();
    private long _openBytes;
    private const int MaxOpenFiles = 2048;
    private const long MaxOpenBytes = 64L * 1024 * 1024;
    private bool _disposed;
    private const long MaxPendingBytes = 256L * 1024 * 1024;
    public LruCacheLayer(string root, Func<string> version, long capacityBytes, bool compression = false)
    {
        _version = version; Capacity = capacityBytes;
        _readRoot = Path.Combine(root, "reads", "objects-v2");
        _reads = new(Path.Combine(root, "reads"), compression, recoverable: true, preferredPrefix: version() + "|");
        _writes = new(Path.Combine(root, "writes"), compression);
        foreach (var record in _reads.Records)
        {
            long metadata = CacheBudgets.Estimate(record.Path, record.Value);
            _items[record.Path] = new(record.Value, record.StoredBytes, record: record) { Metadata = metadata };
            _metadataBytes += metadata;
            _bytes += record.StoredBytes;
        }
        using (ExecutionContext.SuppressFlow())
            _storageCheck = new Timer(_ => ValidateStorage(), null, 1000, 1000);
    }
    private string Key(string path) => Generation + "|" + path;
    public override string Name => "cache";
    public override string Generation => _version();
    public override bool Writable => true;
    public bool HasDurableWrites => _writes.Records.Count > 0;
    public long Capacity { get; }
    public long TotalBytes { get { lock (_gate) return _bytes; } }
    public int Count { get { lock (_gate) return _items.Count; } }
    public long EstimatedMetadataBytes { get { lock (_gate) return _metadataBytes; } }
    public long PendingBytes { get { lock (_gate) return _pendingBytes; } }
    public int RetainedReadHandles { get { lock (_gate) return _openCount; } }
    public string? LastPersistenceError { get; private set; }
    public RequestDiagnostics? Diagnostics { get; set; }
    internal Action? BeforePersist { get; set; }

    private void Remove(string key)
    {
        if (!_items.Remove(key, out var item)) return;
        _bytes -= item.Bytes;
        _metadataBytes -= item.Metadata;
        item.Content?.Dispose(); item.Content = null;
        ReleaseOpen(item);
    }
    private void ReleaseOpen(Item item)
    {
        if (item.OpenContent == null) return;
        _openCount--; _openBytes -= item.OpenContent.Length;
        _openLru.Remove(item.OpenNode!); item.OpenNode = null;
        item.OpenContent.Dispose(); item.OpenContent = null;
    }
    private bool TryGet(string key, out Item item)
    {
        item = null!;
        if (!_items.TryGetValue(key, out var found)) return false;
        found.Access = System.Diagnostics.Stopwatch.GetTimestamp();
        if (found.OpenNode != null) { _openLru.Remove(found.OpenNode); _openLru.AddFirst(found.OpenNode); }
        item = found;
        return true;
    }
    internal Action? BeforeStorageCheck { get; set; }
    internal void ValidateStorage()
    {
        if (Interlocked.CompareExchange(ref _checkingStorage, 1, 0) != 0) return;
        try
        {
            KeyValuePair<string, Item>[] snapshot;
            lock (_gate)
            {
                if (_disposed || _items.Count == 0) return;
                snapshot = _items.ToArray();
            }
            BeforeStorageCheck?.Invoke();
            // Filesystem calls are deliberately outside the cache index lock.
            bool databaseExists = File.Exists(Path.Combine(_readRoot, "objects.db"));
            int checks = databaseExists ? Math.Min(256, snapshot.Length) : snapshot.Length;
            for (int i = 0; i < checks; i++)
            {
                var pair = snapshot[(_storageCursor + i) % snapshot.Length];
                var record = pair.Value.Record;
                if (databaseExists && (record?.Blob == null || File.Exists(Path.Combine(_readRoot, record.Blob)))) continue;
                lock (_gate)
                {
                    if (_items.TryGetValue(pair.Key, out var current) && ReferenceEquals(current, pair.Value) && current.Record == record)
                        Remove(pair.Key);
                }
            }
            _storageCursor = (_storageCursor + checks) % snapshot.Length;
        }
        catch (Exception ex) { LastPersistenceError = ex.Message; }
        finally { Volatile.Write(ref _checkingStorage, 0); }
    }
    public override bool TryReadObject(string path, out VirtualObject value)
    {
        if (_writes.TryGet(path, out value)) return true;
        string key = Key(path);
        lock (_gate)
        {
            bool found = TryGet(key, out var item);
            value = found ? item.Value : null!;
            return found;
        }
    }
    public override bool TryApply(IReadOnlyList<ObjectChange> changes) { _writes.Apply(changes); return true; }
    public void StoreObject(string path, VirtualObject value, Stream? content = null)
    {
        string key = Key(path);
        // Reserve bounded queue space before retaining/copying any file data.
        var payload = value.Content;
        long estimate = payload.PersistenceEstimate;
        long metadata = CacheBudgets.Estimate(key, value);
        lock (_gate)
        {
            if (_disposed || metadata > CacheBudgets.MetadataBytes || estimate > Capacity || estimate > MaxPendingBytes ||
                _pendingBytes + estimate > MaxPendingBytes || _queue.Reader.Count >= 4096 || _items.ContainsKey(key)) return;
            CacheContent? captured = null;
            try
            {
                captured = payload.RetainBytes(content);
                if (payload is FileObjectContent && captured == null) return;
                // Directory lists and headers are immutable snapshots for background work.
                var snapshot = value.Snapshot();
                var item = new Item(snapshot, estimate, captured) { Metadata = metadata };
                if (!_queue.Writer.TryWrite((key, item))) { captured?.Dispose(); return; }
                _items[key] = item; _bytes += estimate; _pendingBytes += estimate;
                _metadataBytes += metadata;
                while ((_bytes > Capacity || _metadataBytes > CacheBudgets.MetadataBytes || _items.Count > CacheBudgets.MaxObjects) && _items.Count > 0)
                    Remove(_items.MinBy(p => p.Value.Access).Key);
                if (_worker == null)
                {
                    using (ExecutionContext.SuppressFlow()) _worker = Task.Run(Persist);
                }
            }
            catch (Exception ex)
            {
                captured?.Dispose();
                LastPersistenceError = ex.Message;
                // Cache capture failure never invalidates the successful source read.
            }
        }
    }
    private void Persist()
    {
        while (true)
        {
            var batch = new List<(string Key, Item Item)>();
            lock (_gate)
            {
                long size = 0;
                while (batch.Count < 64 && size < 8L * 1024 * 1024 && _queue.Reader.TryRead(out var work))
                {
                    batch.Add(work); size += work.Item.Reservation;
                }
                if (batch.Count == 0) { _worker = null; return; }
            }
            var changes = new List<ObjectChange>();
            var streams = new List<Stream>();
            using var request = RequestDiagnostics.Begin("Cache.Persist", $"batch={batch.Count}", Diagnostics);
            try
            {
                request?.Stage("waiting persistence lock");
                lock (_persistGate)
                {
                    lock (_gate)
                    {
                        foreach (var work in batch)
                        {
                            if (_disposed || !_items.TryGetValue(work.Key, out var current) || current != work.Item) continue;
                            var stream = work.Item.Content?.Acquire();
                            if (stream != null) streams.Add(stream);
                            changes.Add(new ObjectChange(work.Key, work.Item.Value, stream));
                        }
                    }
                    if (changes.Count == 0) continue;
                    BeforePersist?.Invoke();
                    request?.Stage($"writing blobs and SQLite batch records={changes.Count}");
                    _reads.Apply(changes);
                    var records = _reads.Records;
                    var published = changes.ToDictionary(c => c.Path, c => _reads.GetPublishedRecord(c.Path), StringComparer.OrdinalIgnoreCase);
                    lock (_gate)
                    {
                        foreach (var work in batch)
                        {
                        if (_items.TryGetValue(work.Key, out var current) && current == work.Item)
                        {
                            if (!published.TryGetValue(work.Key, out var record) || record == null) { Remove(work.Key); continue; }
                            _bytes += record.StoredBytes - current.Bytes;
                            current.Bytes = record.StoredBytes;
                            current.Record = record;
                            current.Content?.Dispose(); current.Content = null;
                        }
                        }
                    }
                    // Disk eviction follows the same immediately visible memory LRU index.
                    string[] obsolete;
                    lock (_gate)
                    {
                        while (_bytes > Capacity && _items.Count > 0) Remove(_items.MinBy(p => p.Value.Access).Key);
                        obsolete = records.Where(r => !_items.ContainsKey(r.Path)).Select(r => r.Path).ToArray();
                    }
                    if (obsolete.Length > 0) _reads.Apply(obsolete.Select(k => new ObjectChange(k, null)).ToArray());
                }
            }
            catch (Exception ex)
            {
                request?.Stage("failed: " + ex.GetType().Name);
                lock (_gate)
                {
                    LastPersistenceError = ex.Message;
                    foreach (var work in batch)
                        if (_items.TryGetValue(work.Key, out var current) && current == work.Item) Remove(work.Key);
                }
            }
            finally
            {
                foreach (var stream in streams) stream.Dispose();
                lock (_gate) _pendingBytes -= batch.Sum(work => work.Item.Reservation);
            }
        }
    }
    public override bool TryGetEntry(string path, out VfsEntry entry)
    { bool found = TryReadObject(path, out var value); entry = found ? value.Header : null!; return found; }
    public override IReadOnlyList<VfsEntry> ListOwn(string path) => TryReadObject(path, out var value) ? value.Children ?? Array.Empty<VfsEntry>() : Array.Empty<VfsEntry>();
    public override bool TryOpenRead(string path, out VfsEntry entry, out Stream stream)
    {
        if (_writes.TryOpen(path, out var durable, out stream)) { entry = durable.Header; return true; }
        string key = Key(path);
        ObjectDatabase.Record? record;
        lock (_gate)
        {
            entry = null!; stream = null!;
            if (!TryGet(key, out var item) || item.Value.Header.IsDir) return false;
            entry = item.Value.Header;
            if (item.Content != null) { stream = item.Content.Acquire()!; return stream != null; }
            if (item.OpenContent != null) { stream = item.OpenContent.Acquire()!; return stream != null; }
            record = item.Record;
        }
        try
        {
            if (!_reads.TryOpenRecord(record!, out stream)) return false;
            if (record!.Compressed || stream is not FileStream file || file.Length > MaxOpenBytes) return true;
            // Open outside the index lock. Publish a shared immutable handle only
            // if this record is still current; each reader has an independent offset.
            var opened = CacheContent.Own(file);
            lock (_gate)
            {
                if (_disposed || !_items.TryGetValue(key, out var current) || current.Record != record)
                {
                    stream = opened.Acquire()!;
                    opened.Dispose();
                    return true;
                }
                if (current.OpenContent != null)
                {
                    opened.Dispose();
                    stream = current.OpenContent.Acquire()!;
                    return true;
                }
                while (_openCount >= MaxOpenFiles || _openBytes + opened.Length > MaxOpenBytes)
                    ReleaseOpen(_openLru.Last!.Value);
                current.OpenContent = opened;
                current.OpenNode = _openLru.AddFirst(current);
                _openCount++; _openBytes += opened.Length;
                stream = opened.Acquire()!;
                return true;
            }
        }
        catch (FileNotFoundException) { ForgetRecord(key, record); return false; }
        catch (DirectoryNotFoundException) { ForgetRecord(key, record); return false; }
    }
    private void ForgetRecord(string key, ObjectDatabase.Record? record)
    {
        lock (_gate) if (_items.TryGetValue(key, out var item) && item.Record == record) Remove(key);
    }
    public void Store(string path, string version, VfsEntry entry, Stream content) => StoreObject(path, new(entry), content);
    public void Invalidate() => Purge();
    public override void Clear() => Purge();
    public void Purge()
    {
        lock (_persistGate)
        {
            lock (_gate) foreach (var key in _items.Keys.ToArray()) Remove(key);
            _reads.Clear();
        }
    }
    public void InvalidateEntry(string path)
    {
        string key = Key(path);
        lock (_persistGate)
        {
            lock (_gate) Remove(key);
            _reads.Apply(new[] { new ObjectChange(key, null) });
        }
    }
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
            TotalBytes, Capacity, writes.ObjectBytes, writes.FileObjects + writes.DirectoryObjects);
    }
    internal Task DrainAsync() => WaitForPendingFillsAsync();
    public async Task WaitForPendingFillsAsync()
    {
        while (true)
        {
            lock (_gate) if (_pendingBytes == 0) return;
            await Task.Delay(10);
        }
    }
    public void Dispose()
    {
        _storageCheck.Dispose();
        Task? worker;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _queue.Writer.TryComplete();
            foreach (var key in _items.Keys.ToArray()) Remove(key);
            worker = _worker;
        }
        worker?.GetAwaiter().GetResult();
    }
}
