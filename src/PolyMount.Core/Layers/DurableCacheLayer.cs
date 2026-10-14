using System.Diagnostics;
using System.Threading.Channels;
using PolyMount.Core;

namespace PolyMount.Layers;

/// <summary>
/// Base for a cache whose records live in a durable store behind a bounded in-memory
/// index. Admission publishes into the index immediately and a single background worker
/// flushes the write, so a read never waits on the disk.
/// </summary>
public abstract class DurableCacheLayer : CacheLayer, ICacheLayer, IObjectCache, IDisposable
{
    // Members that mention ObjectDatabase are private protected: the store is an
    // assembly-internal detail, and only the caches in this assembly derive from here.
    private protected class Item(VirtualObject value, long bytes, ObjectDatabase.Record? record = null)
    {
        public VirtualObject Value = value;
        public long Bytes = bytes;
        public long Access = Stopwatch.GetTimestamp();
        public ObjectDatabase.Record? Record = record;
        public long Reservation = bytes;
        public long Metadata;
        /// <summary>Queued but not yet written, so it has no durable copy to fall back to.</summary>
        public bool Pending;
        /// <summary>Admission epoch, so a purge can retire writes already in flight.</summary>
        public long Epoch;
    }

    private const int MaxQueuedWrites = 4096;
    private const long MaxPendingBytes = 256L * 1024 * 1024;
    private const int PersistBatchObjects = 64;
    private const long PersistBatchBytes = 8L * 1024 * 1024;

    private readonly ObjectDatabase _store;
    private readonly string _storeRoot;
    private readonly Func<string> _version;
    private readonly long _indexCapacity;
    private readonly long _metadataBudget;
    private readonly Func<VirtualObject, bool> _keep;
    private readonly object _gate = new(), _persistGate = new();
    private readonly Dictionary<string, Item> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<(string Key, Item Item)> _queue = Channel.CreateBounded<(string, Item)>(
        new BoundedChannelOptions(MaxQueuedWrites) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Timer _storageCheck;
    private Task? _worker;
    private int _checkingStorage, _storageCursor;
    private long _bytes, _pendingBytes, _metadataBytes;
    private long _epoch;
    // Keys explicitly removed; a write still queued for one must not resurrect it.
    private readonly HashSet<string> _retired = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <param name="store">Durable record store backing this cache.</param>
    /// <param name="storeRoot">Directory holding <paramref name="store"/>'s database file.</param>
    /// <param name="version">Lower-chain generation; every key is namespaced by it.</param>
    /// <param name="indexCapacityBytes">Bound on the resident index. Evicting from the index never touches the store.</param>
    /// <param name="metadataBudget">Upper bound on the index's estimated metadata footprint.</param>
    /// <param name="keep">Which values this cache holds. Passed in rather than overridden
    /// because the constructor loads records before derived state exists.</param>
    private protected DurableCacheLayer(ObjectDatabase store, string storeRoot, Func<string> version,
        long indexCapacityBytes, long metadataBudget, Func<VirtualObject, bool> keep)
    {
        _store = store;
        _storeRoot = storeRoot;
        _version = version;
        _indexCapacity = indexCapacityBytes;
        _metadataBudget = metadataBudget;
        _keep = keep;
        foreach (var record in store.Records) Load(record);
        using (ExecutionContext.SuppressFlow())
            _storageCheck = new Timer(_ => ValidateStorage(), null, 1000, 1000);
    }

    /// <summary>Whether a value belongs in this cache at all.</summary>
    private bool KeepIndexed(VirtualObject value) => _keep(value);

    /// <summary>Creates the index entry for a value. Override to carry content alongside it.</summary>
    private protected virtual Item NewItem(VirtualObject value, long bytes, ObjectDatabase.Record? record = null) =>
        new(value, bytes, record);

    /// <summary>Attaches content to a pending entry; return false to reject the admission.</summary>
    private protected virtual bool Prepare(Item item, VirtualObject source, Stream? content) => true;

    /// <summary>Releases whatever <see cref="Prepare"/> attached. Called with the index lock held.</summary>
    private protected virtual void ReleaseItem(Item item) { }

    /// <summary>Opens the content stream to persist for an admitted entry.</summary>
    private protected virtual Stream? AcquireContent(Item item) => null;

    /// <summary>Drops the staged content once the store owns the bytes. Called with the index lock held.</summary>
    private protected virtual void SettleItem(Item item) { }

    /// <summary>Extra recency work for a hit. Called with the index lock held.</summary>
    private protected virtual void OnTouch(Item item) { }

    /// <summary>Whether this index entry is still backed by its durable record.</summary>
    private protected virtual bool IsBacked(Item item) => true;

    /// <summary>Keys the store should delete once this batch is written. The base never deletes.</summary>
    private protected virtual string[] ObsoleteKeys(IReadOnlyList<ObjectDatabase.Record> written) => [];

    protected string Key(string path) => Generation + "|" + path;
    public override string Generation => _version();

    /// <summary>Guards the index; subclasses keep their own entry state under the same lock.</summary>
    private protected object Gate => _gate;

    private protected ObjectDatabase Reads => _store;
    private protected string StoreRoot => _storeRoot;

    /// <summary>Whether the index currently holds a key. Caller must hold <see cref="Gate"/>.</summary>
    private protected bool Contains(string key) => _items.ContainsKey(key);

    /// <summary>Resident lookup with the recency update a hit performs.</summary>
    private protected bool TryGetResident(string key, out Item item) { lock (_gate) return TryGet(key, out item); }

    /// <summary>Drops a resident entry. Never touches the store.</summary>
    private protected void RemoveResident(string key) { lock (_gate) Remove(key); }

    private protected bool IsDisposed { get { lock (_gate) return _disposed; } }

    public RequestDiagnostics? Diagnostics { get; set; }
    public string? LastPersistenceError { get; private set; }
    public long Capacity => _indexCapacity;
    public long TotalBytes { get { lock (_gate) return _bytes; } }
    public int Count { get { lock (_gate) return _items.Count; } }
    public long EstimatedMetadataBytes { get { lock (_gate) return _metadataBytes; } }
    public long PendingBytes { get { lock (_gate) return _pendingBytes; } }

    /// <summary>Blocks the persistence worker once it has drained a batch; used by tests.</summary>
    internal Action? BeforePersist { get; set; }

    /// <summary>Runs inside the storage check before filesystem probing; used by tests.</summary>
    internal Action? BeforeStorageCheck { get; set; }

    private void Load(ObjectDatabase.Record record)
    {
        // Records this cache no longer admits stay in the store; the next persist batch
        // reports them as obsolete so they clean themselves up.
        if (!KeepIndexed(record.Value)) return;
        long metadata = CacheBudgets.Estimate(record.Path, record.Value);
        var item = NewItem(record.Value, record.StoredBytes, record);
        item.Metadata = metadata;
        _items[record.Path] = item;
        _metadataBytes += metadata;
        _bytes += record.StoredBytes;
    }

    private void Remove(string key)
    {
        if (!_items.Remove(key, out var item)) return;
        _bytes -= item.Bytes;
        _metadataBytes -= item.Metadata;
        ReleaseItem(item);
    }

    private bool TryGet(string key, out Item item)
    {
        item = null!;
        if (!_items.TryGetValue(key, out var found)) return false;
        found.Access = Stopwatch.GetTimestamp();
        OnTouch(found);
        item = found;
        return true;
    }

    /// <summary>Re-admits a value read back from the store when the index no longer holds it.</summary>
    private void Reindex(string key, VirtualObject value)
    {
        long bytes = value.Content.PersistenceEstimate;
        long metadata = CacheBudgets.Estimate(key, value);
        var item = NewItem(value, bytes);
        item.Metadata = metadata;
        _items[key] = item;
        _bytes += bytes;
        _metadataBytes += metadata;
        Trim();
    }

    private bool Evictable() =>
        _bytes > _indexCapacity || _metadataBytes > _metadataBudget || _items.Count > CacheBudgets.MaxObjects;

    /// <summary>
    /// Oldest entry in the requested class. Dropping an entry the store already owns costs
    /// a local re-read, while dropping one that is still queued throws away the pending
    /// write, so <see cref="Trim"/> drains the durable class first and only reaches for
    /// queued entries when the writer has fallen far enough behind that nothing else is left.
    /// </summary>
    private string? OldestEvictableKey(bool pending)
    {
        string? oldest = null;
        long oldestAccess = long.MaxValue;
        foreach (var pair in _items)
        {
            if (pair.Value.Pending != pending || pair.Value.Access >= oldestAccess) continue;
            oldestAccess = pair.Value.Access;
            oldest = pair.Key;
        }
        return oldest;
    }

    private void Trim()
    {
        while (Evictable())
        {
            if ((OldestEvictableKey(pending: false) ?? OldestEvictableKey(pending: true)) is not { } key) return;
            Remove(key);
        }
    }

    public override bool TryReadObject(string path, out VirtualObject value)
    {
        string key = Key(path);
        lock (_gate)
        {
            if (TryGet(key, out var item)) { value = item.Value; return true; }
        }
        // The store is the authority, so a resident-index miss is a local read rather
        // than a source read. Namespace records in particular must survive being dropped
        // from the index: losing one costs every path beneath it. Re-admit so the next
        // lookup comes from memory again.
        if (!_store.TryGet(key, out value) || !KeepIndexed(value)) { value = null!; return false; }
        lock (_gate) Reindex(key, value);
        return true;
    }

    public void TouchObject(string path)
    {
        // Same recency update a hit performs, for reads a higher cache answered.
        lock (_gate) TryGet(Key(path), out _);
    }

    public virtual void StoreObject(string path, VirtualObject value, Stream? content = null)
    {
        if (!KeepIndexed(value)) return;
        string key = Key(path);
        long estimate = value.Content.PersistenceEstimate;
        long metadata = CacheBudgets.Estimate(key, value);
        lock (_gate)
        {
            // Reserve bounded queue space before retaining or copying any content.
            if (_disposed || metadata > _metadataBudget || estimate > _indexCapacity ||
                estimate > MaxPendingBytes || _pendingBytes + estimate > MaxPendingBytes ||
                _queue.Reader.Count >= MaxQueuedWrites || _items.ContainsKey(key)) return;
            Item? item = null;
            try
            {
                item = NewItem(value.Snapshot(), estimate);
                item.Metadata = metadata;
                if (!Prepare(item, value, content)) { ReleaseItem(item); return; }
                if (!_queue.Writer.TryWrite((key, item))) { ReleaseItem(item); return; }
                _items[key] = item;
                item.Pending = true;
                item.Epoch = _epoch;
                _retired.Remove(key);
                _bytes += estimate;
                _pendingBytes += estimate;
                _metadataBytes += metadata;
                Trim();
                StartWorker();
            }
            catch (Exception ex)
            {
                if (item != null) ReleaseItem(item);
                LastPersistenceError = ex.Message;
                // A cache capture failure must never invalidate the successful source read.
            }
        }
    }

    private void StartWorker()
    {
        if (_worker != null) return;
        using (ExecutionContext.SuppressFlow()) _worker = Task.Run(Persist);
    }

    private void Persist()
    {
        while (true)
        {
            var batch = new List<(string Key, Item Item)>();
            lock (_gate)
            {
                long size = 0;
                while (batch.Count < PersistBatchObjects && size < PersistBatchBytes && _queue.Reader.TryRead(out var work))
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
                            if (_disposed) continue;
                            if (_retired.Remove(work.Key)) continue;
                            bool resident = _items.TryGetValue(work.Key, out var current) && current == work.Item;
                            // A namespace record dropped from the index before the writer
                            // reached it would be lost outright, and it is tiny, so write
                            // it anyway. Content that lost its slot is reclaimed instead.
                            // A purge retires everything admitted before it.
                            bool namespaceRecord = work.Item.Value.Header.IsDir && work.Item.Epoch == _epoch;
                            if (!resident && !namespaceRecord) continue;
                            var stream = AcquireContent(work.Item);
                            if (stream != null) streams.Add(stream);
                            changes.Add(new ObjectChange(work.Key, work.Item.Value, stream));
                        }
                    }
                    if (changes.Count == 0) continue;
                    BeforePersist?.Invoke();
                    request?.Stage($"writing records={changes.Count}");
                    _store.Apply(changes);
                    var records = _store.Records;
                    var published = changes.ToDictionary(c => c.Path, c => _store.GetPublishedRecord(c.Path), StringComparer.OrdinalIgnoreCase);
                    lock (_gate)
                    {
                        foreach (var work in batch)
                        {
                            if (!_items.TryGetValue(work.Key, out var current) || current != work.Item) continue;
                            if (!published.TryGetValue(work.Key, out var record) || record == null) { Remove(work.Key); continue; }
                            _bytes += record.StoredBytes - current.Bytes;
                            current.Bytes = record.StoredBytes;
                            current.Record = record;
                            current.Pending = false;
                            SettleItem(current);
                        }
                    }
                    // Store-side eviction follows the same immediately visible index.
                    string[] obsolete;
                    lock (_gate)
                    {
                        Trim();
                        obsolete = ObsoleteKeys(records);
                    }
                    if (obsolete.Length > 0) _store.Apply(obsolete.Select(k => new ObjectChange(k, null)).ToArray());
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
            bool present = File.Exists(Path.Combine(_storeRoot, "objects.db"));
            int checks = present ? Math.Min(256, snapshot.Length) : snapshot.Length;
            for (int i = 0; i < checks; i++)
            {
                var pair = snapshot[(_storageCursor + i) % snapshot.Length];
                if (present && IsBacked(pair.Value)) continue;
                lock (_gate)
                {
                    if (_items.TryGetValue(pair.Key, out var current) && ReferenceEquals(current, pair.Value))
                        Remove(pair.Key);
                }
            }
            _storageCursor = (_storageCursor + checks) % snapshot.Length;
        }
        catch (Exception ex) { LastPersistenceError = ex.Message; }
        finally { Volatile.Write(ref _checkingStorage, 0); }
    }

    public override bool TryGetEntry(string path, out VfsEntry entry)
    {
        bool found = TryReadObject(path, out var value);
        entry = found ? value.Header : null!;
        return found;
    }

    public override IReadOnlyList<VfsEntry> ListOwn(string path) =>
        TryReadObject(path, out var value) ? value.Children ?? Array.Empty<VfsEntry>() : Array.Empty<VfsEntry>();

    public void Store(string path, string version, VfsEntry entry, Stream content) => StoreObject(path, new(entry), content);

    public void Invalidate() => Purge();
    public override void Clear() => Purge();

    public void Purge()
    {
        lock (_persistGate)
        {
            lock (_gate)
            {
                _epoch++;
                _retired.Clear();
                // Discard fills that have not been written yet. The worker persists
                // queued namespace records even after they leave the index, so without
                // this a cleared cache would repopulate itself from its own backlog.
                long dropped = 0;
                while (_queue.Reader.TryRead(out var queued))
                {
                    dropped += queued.Item.Reservation;
                    ReleaseItem(queued.Item);
                }
                _pendingBytes -= dropped;
                foreach (var key in _items.Keys.ToArray()) Remove(key);
            }
            _store.Clear();
        }
    }

    public void InvalidateEntry(string path)
    {
        string key = Key(path);
        lock (_persistGate)
        {
            lock (_gate)
            {
                // The store row goes now, so a write still queued for this key has to be
                // retired or it would put the entry back after the eviction.
                if (_retired.Count > 8192) _retired.Clear();
                _retired.Add(key);
                Remove(key);
            }
            _store.Apply(new[] { new ObjectChange(key, null) });
        }
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
