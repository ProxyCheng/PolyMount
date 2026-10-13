using System.Threading.Channels;
using PolyMount.Core;

namespace PolyMount.Layers;

/// <summary>Immediate memory publication with best-effort background read-cache persistence.</summary>
public sealed class LruCacheLayer : ICacheLayer, IObjectCache, IObjectManagement, ILayerStorageUsage, IDisposable
{
    private sealed class Item(VirtualObject value, long bytes, CacheContent? content = null, ObjectDatabase.Record? record = null)
    {
        public VirtualObject Value = value;
        public long Bytes = bytes, Access = Environment.TickCount64;
        public CacheContent? Content = content;
        public ObjectDatabase.Record? Record = record;
        public long Reservation = bytes;
    }
    private readonly ObjectDatabase _reads, _writes;
    private readonly Func<string> _version;
    private readonly string _readRoot;
    private readonly object _gate = new(), _persistGate = new();
    private readonly Dictionary<string, Item> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<(string Key, Item Item)> _queue = Channel.CreateBounded<(string, Item)>(
        new BoundedChannelOptions(128) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private Task? _worker;
    private long _bytes, _pendingBytes;
    private bool _disposed;
    private const long MaxPendingBytes = 256L * 1024 * 1024;
    public LruCacheLayer(string root, Func<string> version, long capacityBytes, bool compression = false)
    {
        _version = version; Capacity = capacityBytes;
        _readRoot = Path.Combine(root, "reads", "objects-v2");
        _reads = new(Path.Combine(root, "reads"), compression, recoverable: true);
        _writes = new(Path.Combine(root, "writes"), compression);
        foreach (var record in _reads.Records)
        {
            _items[record.Path] = new(record.Value, record.StoredBytes, record: record);
            _bytes += record.StoredBytes;
        }
    }
    private string Key(string path) => Generation + "|" + path;
    public string Name => "cache";
    public string Generation => _version();
    public bool Writable => true;
    public bool HasDurableWrites => _writes.Records.Count > 0;
    public long Capacity { get; }
    public long TotalBytes { get { lock (_gate) return _bytes; } }
    public int Count { get { lock (_gate) return _items.Count; } }
    public string? LastPersistenceError { get; private set; }
    public RequestDiagnostics? Diagnostics { get; set; }
    internal Action? BeforePersist { get; set; }

    private void Remove(string key)
    {
        if (!_items.Remove(key, out var item)) return;
        _bytes -= item.Bytes;
        item.Content?.Dispose(); item.Content = null;
    }
    private bool TryGet(string key, out Item item)
    {
        item = null!;
        if (!_items.TryGetValue(key, out var found)) return false;
        if (!File.Exists(Path.Combine(_readRoot, "objects.db")) ||
            (found.Record?.Blob is { } blob && !File.Exists(Path.Combine(_readRoot, blob))))
        {
            Remove(key);
            return false;
        }
        found.Access = Environment.TickCount64;
        item = found;
        return true;
    }
    public bool TryReadObject(string path, out VirtualObject value)
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
    public bool TryApply(IReadOnlyList<ObjectChange> changes) { _writes.Apply(changes); return true; }
    public void StoreObject(string path, VirtualObject value, Stream? content = null)
    {
        string key = Key(path);
        // Reserve bounded queue space before retaining/copying any file data.
        long estimate = value.Header.IsDir
            ? 256L + (value.Children?.Sum(c => 256L + c.Name.Length * 6L) ?? 0)
            : value.Header.Size + 128;
        lock (_gate)
        {
            if (_disposed || estimate > Capacity || estimate > MaxPendingBytes ||
                _pendingBytes + estimate > MaxPendingBytes || _queue.Reader.Count >= 128 || _items.ContainsKey(key)) return;
            CacheContent? captured = null;
            try
            {
                if (!value.Header.IsDir)
                {
                    if (content == null || (captured = CacheContent.Capture(content)) == null) return;
                }
                // Directory lists and headers are immutable snapshots for background work.
                var snapshot = new VirtualObject(Clone(value.Header), value.Children?.Select(Clone).ToArray());
                var item = new Item(snapshot, estimate, captured);
                if (!_queue.Writer.TryWrite((key, item))) { captured?.Dispose(); return; }
                _items[key] = item; _bytes += estimate; _pendingBytes += estimate;
                while (_bytes > Capacity && _items.Count > 0)
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
    private static VfsEntry Clone(VfsEntry entry) => new()
        { Name = entry.Name, IsDir = entry.IsDir, Size = entry.Size, TimeUtc = entry.TimeUtc };

    private void Persist()
    {
        while (true)
        {
            (string Key, Item Item) work;
            lock (_gate)
            {
                if (!_queue.Reader.TryRead(out work)) { _worker = null; return; }
            }
            Stream? stream = null;
            using var request = RequestDiagnostics.Begin("Cache.Persist", work.Key, Diagnostics);
            try
            {
                request?.Stage("waiting persistence lock");
                lock (_persistGate)
                {
                    lock (_gate)
                    {
                        if (_disposed || !_items.TryGetValue(work.Key, out var current) || current != work.Item) continue;
                        stream = work.Item.Content?.Acquire();
                    }
                    BeforePersist?.Invoke();
                    request?.Stage("writing blob and SQLite record");
                    _reads.Apply(new[] { new ObjectChange(work.Key, work.Item.Value, stream) });
                    var record = _reads.Records.Single(r => r.Path == work.Key);
                    lock (_gate)
                    {
                        if (_items.TryGetValue(work.Key, out var current) && current == work.Item)
                        {
                            _bytes += record.StoredBytes - current.Bytes;
                            current.Bytes = record.StoredBytes;
                            current.Record = record;
                            current.Content?.Dispose(); current.Content = null;
                        }
                    }
                    // Disk eviction follows the same immediately visible memory LRU index.
                    string[] obsolete;
                    var records = _reads.Records;
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
                    if (_items.TryGetValue(work.Key, out var current) && current == work.Item) Remove(work.Key);
                }
            }
            finally
            {
                stream?.Dispose();
                lock (_gate) _pendingBytes -= work.Item.Reservation;
            }
        }
    }
    public bool TryGetEntry(string path, out VfsEntry entry)
    { bool found = TryReadObject(path, out var value); entry = found ? value.Header : null!; return found; }
    public IReadOnlyList<VfsEntry> ListOwn(string path) => TryReadObject(path, out var value) ? value.Children ?? Array.Empty<VfsEntry>() : Array.Empty<VfsEntry>();
    public bool TryOpenRead(string path, out VfsEntry entry, out Stream stream)
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
            record = item.Record;
        }
        try { return _reads.TryOpenRecord(record!, out stream); }
        catch (FileNotFoundException) { ForgetRecord(key, record); return false; }
        catch (DirectoryNotFoundException) { ForgetRecord(key, record); return false; }
    }
    private void ForgetRecord(string key, ObjectDatabase.Record? record)
    {
        lock (_gate) if (_items.TryGetValue(key, out var item) && item.Record == record) Remove(key);
    }
    public void Store(string path, string version, VfsEntry entry, Stream content) => StoreObject(path, new(entry), content);
    public void Invalidate() => Purge();
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
    internal async Task DrainAsync()
    {
        while (true)
        {
            lock (_gate) if (_pendingBytes == 0) return;
            await Task.Delay(10);
        }
    }
    public void Dispose()
    {
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
