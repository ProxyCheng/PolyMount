using System.Diagnostics;
using System.Threading.Channels;
using PolyMount.Core;

namespace PolyMount.Layers;

/// <summary>
/// Base for a cache whose records live in a durable store behind a bounded in-memory
/// index. Admission publishes into the index immediately and a single background worker
/// flushes the write, so a read never waits on the disk.
/// </summary>
public abstract class DurableCacheLayer : CacheLayer, ICacheLayer, IObjectCache, IQuietProbe, IDisposable
{
    // Members that mention ObjectDatabase are private protected: the store is an
    // assembly-internal detail, and only the caches in this assembly derive from here.
    private protected class Item(VirtualObject value, long bytes, ObjectDatabase.Record? record = null)
    {
        public VirtualObject Value = value;
        public long Bytes = bytes;
        public long Access = Stopwatch.GetTimestamp();
        /// <summary>Position in the recency list; null while not resident.</summary>
        public LinkedListNode<Item>? Node;
        public ObjectDatabase.Record? Record = record;
        public long Reservation = bytes;
        public long Metadata;
        /// <summary>Queued but not yet written, so it has no durable copy to fall back to.</summary>
        public bool Pending;
        /// <summary>Admission epoch, so a purge can retire writes already in flight.</summary>
        public long Epoch;
        /// <summary>The resident key this item was admitted under, so content-version
        /// bookkeeping can retire it without searching the index.</summary>
        public CacheKey Key;
        /// <summary>The composite the record is stored under. Built once when the item is
        /// admitted — the write needs it anyway — and kept so the persist batch can ask
        /// "is this stored row resident" with a plain lookup instead of splitting a key per
        /// record per pass.</summary>
        public string StoreKey = string.Empty;
    }

    private const int MaxQueuedWrites = 4096;
    private const long MaxPendingBytes = 256L * 1024 * 1024;
    private const int PersistBatchObjects = 64;
    private const long PersistBatchBytes = 8L * 1024 * 1024;

    /// <summary>The resident index's key: a path in a namespace. It is a struct over the two
    /// strings the caller already holds, not the "generation|path" store key, because the
    /// composite was rebuilt on every read — a fresh string per lookup of every cache. The
    /// store key is still a string; it is built only where a record is written or found.</summary>
    private protected readonly record struct CacheKey(string Path, string Generation);

    private sealed class CacheKeyComparer : IEqualityComparer<CacheKey>
    {
        public static readonly CacheKeyComparer Instance = new();
        public bool Equals(CacheKey left, CacheKey right) =>
            StringComparer.OrdinalIgnoreCase.Equals(left.Path, right.Path) &&
            StringComparer.OrdinalIgnoreCase.Equals(left.Generation, right.Generation);
        public int GetHashCode(CacheKey key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Path),
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Generation));
    }

    private sealed class ContentKeyComparer : IEqualityComparer<(string Path, string Version)>
    {
        public static readonly ContentKeyComparer Instance = new();
        public bool Equals((string Path, string Version) left, (string Path, string Version) right) =>
            StringComparer.OrdinalIgnoreCase.Equals(left.Path, right.Path) &&
            string.Equals(left.Version, right.Version, StringComparison.Ordinal);
        public int GetHashCode((string Path, string Version) key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Path),
                StringComparer.Ordinal.GetHashCode(key.Version));
    }

    private readonly ObjectDatabase _store;
    private readonly string _storeRoot;
    private readonly Func<string> _version;
    private readonly long _indexCapacity;
    private readonly long _metadataBudget;
    private readonly Func<VirtualObject, bool> _keep;
    private readonly object _gate = new(), _persistGate = new();
    private readonly Dictionary<CacheKey, Item> _items = new(CacheKeyComparer.Instance);
    /// <summary>The store keys of the resident items. The store-facing membership test runs
    /// once per stored record per persist batch, so it is a plain lookup on the composite the
    /// item was written under — never a split of that composite.</summary>
    private readonly HashSet<string> _residentStoreKeys = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Durable file records by the content version their source declared, whatever
    /// generation admitted them. This is the content that survives the namespace moving: a
    /// path still at version v is still v's bytes, so a cache can answer it before the source
    /// is asked. Only records (not staged content) are indexed, and entries go with their item.</summary>
    private readonly Dictionary<(string Path, string Version), Item> _byContent = new(ContentKeyComparer.Instance);
    private readonly Channel<(CacheKey Key, string StoreKey, Item Item)> _queue = Channel.CreateBounded<(CacheKey, string, Item)>(
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

    /// <summary>Keys the store should delete once this batch is written: content the index
    /// dropped since the last batch and has not re-admitted. The base never deletes.</summary>
    private protected virtual string[] ObsoleteKeys(IReadOnlyCollection<string> evicted) => [];

    private protected CacheKey ResidentKey(string path) => new(path, Generation);
    /// <summary>The composite a record is stored under. Built only when a record is written
    /// or looked up in the store, never on a resident hit.</summary>
    private protected static string StoreKey(CacheKey key) => key.Generation + "|" + key.Path;
    public override string Generation => _version();

    /// <summary>Guards the index; subclasses keep their own entry state under the same lock.</summary>
    private protected object Gate => _gate;

    private protected ObjectDatabase Reads => _store;
    private protected string StoreRoot => _storeRoot;

    /// <summary>Whether a stored record's row is resident. A plain lookup on the key the
    /// entry was admitted under.</summary>
    private protected bool Contains(ObjectDatabase.Record record) => _residentStoreKeys.Contains(record.Path);
    /// <summary>Whether a store key is resident. Called with the index lock held.</summary>
    private protected bool IsResidentStoreKey(string storeKey) => _residentStoreKeys.Contains(storeKey);

    /// <summary>Resident lookup with the recency update a hit performs.</summary>
    private protected bool TryGetResident(CacheKey key, out Item item) { lock (_gate) return TryGet(key, out item); }

    /// <summary>Drops a resident entry. Never touches the store.</summary>
    private protected void RemoveResident(CacheKey key) { lock (_gate) Remove(key); }

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
        // A row's key is the composite it was written under; the resident index wants its
        // two halves. A row without the separator predates the composite or is not ours.
        int bar = record.Path.IndexOf('|');
        if (bar < 0) return;
        var key = new CacheKey(record.Path[(bar + 1)..], record.Path[..bar]);
        // Records this cache no longer admits stay in the store; the next persist batch
        // reports them as obsolete so they clean themselves up.
        if (!KeepIndexed(record.Value)) return;
        long metadata = CacheBudgets.Estimate(key.Path, record.Value);
        var item = NewItem(record.Value, record.StoredBytes, record);
        item.Metadata = metadata;
        item.Key = key;
        item.StoreKey = record.Path;
        _items[key] = item;
        item.Node = _order.AddLast(item);
        _residentStoreKeys.Add(record.Path);
        IndexContent(item);
        _metadataBytes += metadata;
        _bytes += record.StoredBytes;
    }

    private void Remove(CacheKey key)
    {
        if (!_items.Remove(key, out var item)) return;
        if (item.Node != null) { _order.Remove(item.Node); item.Node = null; }
        _residentStoreKeys.Remove(item.StoreKey);
        _bytes -= item.Bytes;
        _metadataBytes -= item.Metadata;
        // Content that leaves the index is what the store may reclaim next batch; namespace
        // records never are (see LruCacheLayer.ObsoleteKeys), so they are not remembered.
        if (!item.Value.Header.IsDir && !_disposed && item.StoreKey.Length > 0) _evicted.Add(item.StoreKey);
        ForgetContent(item);
        ReleaseItem(item);
    }
    /// <summary>Store keys of content dropped from the index since the last persist batch.
    /// Used to be found by copying every resident store record per batch and asking which ones
    /// the index no longer held - a full-index copy for a batch of at most 64 writes.</summary>
    private readonly HashSet<string> _evicted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Publishes a durable record under its source-declared content version. Records
    /// without one — a directory, or a file from a source that cannot name a version — are
    /// simply not addressable this way.</summary>
    private void IndexContent(Item item)
    {
        if (item.Record is not { } record || record.Value.Header is not { IsDir: false, ContentVersion: { Length: > 0 } version }) return;
        _byContent[(item.Key.Path, version)] = item;
    }

    private void ForgetContent(Item item)
    {
        if (item.Key.Path is not { } path || item.Value.Header.ContentVersion is not { Length: > 0 } version) return;
        if (_byContent.TryGetValue((path, version), out var current) && current == item)
            _byContent.Remove((path, version));
    }

    /// <summary>Finds durable content by what the source said it is, across generations. The
    /// caller validates the header it resolved against the record before serving it.</summary>
    private protected bool TryGetVersioned(string path, string version, out Item item, out ObjectDatabase.Record record)
    {
        lock (_gate)
        {
            if (_byContent.TryGetValue((path, version), out item!) && item.Record is { } found &&
                !found.Value.Header.IsDir)
            {
                // Serving it is a read of this entry too, so its recency has to move or the
                // eviction that keeps the working set would drop what is still being opened.
                Touch(item);
                record = found;
                return true;
            }
        }
        item = null!;
        record = null!;
        return false;
    }

    /// <summary>Drops a versioned record whose content turned out to be unreadable. The whole
    /// resident item goes, so the next lookup re-reads the store or the source.</summary>
    private protected void ForgetVersioned(Item item) { lock (_gate) Remove(item.Key); }

    private bool TryGet(CacheKey key, out Item item)
    {
        item = null!;
        if (!_items.TryGetValue(key, out var found)) return false;
        Touch(found);
        OnTouch(found);
        item = found;
        return true;
    }

    /// <summary>Re-admits a value read back from the store when the index no longer holds it.
    /// The record travels with the value: a content entry that comes back without it looks
    /// resident yet has neither staged bytes nor a blob to open, and the read that follows
    /// asks a null record for its blob.</summary>
    private void Reindex(CacheKey key, string storeKey, VirtualObject value, ObjectDatabase.Record? record)
    {
        // Two misses on one key can both read the row back; the second must not count it twice
        // or drop the first one's handles on the floor.
        if (_items.ContainsKey(key)) return;
        long bytes = record?.StoredBytes ?? value.Content.PersistenceEstimate;
        long metadata = CacheBudgets.Estimate(key.Path, value);
        var item = NewItem(value, bytes, record);
        item.Metadata = metadata;
        item.Key = key;
        item.StoreKey = storeKey;
        _items[key] = item;
        item.Node = _order.AddFirst(item);
        _residentStoreKeys.Add(storeKey);
        IndexContent(item);
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
    private CacheKey? OldestEvictableKey(bool pending)
    {
        // From the least recent end of the recency list. Durable entries are nearly all of it,
        // so this is a step or two; queued ones are bounded by the write queue. It used to scan
        // the whole index once per evicted entry.
        for (var node = _order.Last; node != null; node = node.Previous)
            if (node.Value.Pending == pending) return node.Value.Key;
        return null;
    }

    private void Touch(Item item)
    {
        item.Access = Stopwatch.GetTimestamp();
        if (item.Node != null && _order.First != item.Node) { _order.Remove(item.Node); _order.AddFirst(item.Node); }
    }

    private readonly LinkedList<Item> _order = new();

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
        var key = ResidentKey(path);
        lock (_gate)
        {
            if (TryGet(key, out var item)) { value = item.Value; return true; }
        }
        // The store is the authority, so a resident-index miss is a local read rather
        // than a source read. Namespace records in particular must survive being dropped
        // from the index: losing one costs every path beneath it. Re-admit so the next
        // lookup comes from memory again.
        string storeKey = StoreKey(key);
        if (!_store.TryGet(storeKey, out value, out var record) || !KeepIndexed(value)) { value = null!; return false; }
        lock (_gate) Reindex(key, storeKey, value, record);
        return true;
    }

    /// <summary>Resident index only: inspection neither moves recency nor reads the store back.</summary>
    public bool TryPeekEntry(string path, out VfsEntry entry)
    {
        lock (_gate)
        {
            bool found = _items.TryGetValue(ResidentKey(path), out var item);
            entry = found ? item!.Value.Header : null!;
            if (found) return true;
        }
        return TryGetEntry(path, out entry);
    }

    public void TouchObject(string path)
    {
        // Same recency update a hit performs, for reads a higher cache answered.
        lock (_gate) TryGet(ResidentKey(path), out _);
    }

    public virtual void StoreObject(string path, VirtualObject value, Stream? content = null)
    {
        // A value with neither children nor content is header-only: a namespace fact the
        // resolver learned by asking for one path by name. The store has no shape for one —
        // the row would carry no children and no blob, and TryReadObject hands a row back as
        // a complete directory listing — and writing a row per resolved path is exactly the
        // on-disk growth that header records exist to avoid. They live in the memory layer.
        if (value.Children == null && content == null) return;
        if (!KeepIndexed(value)) return;
        var key = ResidentKey(path);
        string storeKey = StoreKey(key);
        long estimate = value.Content.PersistenceEstimate;
        long metadata = CacheBudgets.Estimate(key.Path, value);
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
                if (!_queue.Writer.TryWrite((key, storeKey, item))) { ReleaseItem(item); return; }
                _items[key] = item;
                item.Node = _order.AddFirst(item);
                item.Pending = true;
                item.Key = key;
                item.StoreKey = storeKey;
                _residentStoreKeys.Add(storeKey);
                item.Epoch = _epoch;
                _retired.Remove(storeKey);
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
            var batch = new List<(CacheKey Key, string StoreKey, Item Item)>();
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
                            if (_retired.Remove(work.StoreKey)) continue;
                            bool resident = _items.TryGetValue(work.Key, out var current) && current == work.Item;
                            // A namespace record dropped from the index before the writer
                            // reached it would be lost outright, and it is tiny, so write
                            // it anyway. Content that lost its slot is reclaimed instead.
                            // A purge retires everything admitted before it.
                            bool namespaceRecord = work.Item.Value.Header.IsDir && work.Item.Epoch == _epoch;
                            if (!resident && !namespaceRecord) continue;
                            var stream = AcquireContent(work.Item);
                            if (stream != null) streams.Add(stream);
                            changes.Add(new ObjectChange(work.StoreKey, work.Item.Value, stream));
                        }
                    }
                    if (changes.Count == 0) continue;
                    BeforePersist?.Invoke();
                    request?.Stage($"writing records={changes.Count}");
                    _store.Apply(changes);
                    var published = changes.ToDictionary(c => c.Path, c => _store.GetPublishedRecord(c.Path), StringComparer.OrdinalIgnoreCase);
                    lock (_gate)
                    {
                        foreach (var work in batch)
                        {
                            if (!_items.TryGetValue(work.Key, out var current) || current != work.Item) continue;
                            if (!published.TryGetValue(work.StoreKey, out var record) || record == null) { Remove(work.Key); continue; }
                            _bytes += record.StoredBytes - current.Bytes;
                            current.Bytes = record.StoredBytes;
                            current.Record = record;
                            current.Pending = false;
                            IndexContent(current);
                            SettleItem(current);
                        }
                    }
                    // Store-side eviction follows the same immediately visible index.
                    string[] obsolete;
                    lock (_gate)
                    {
                        Trim();
                        obsolete = _evicted.Count == 0 ? [] : ObsoleteKeys(_evicted);
                        _evicted.Clear();
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
            KeyValuePair<CacheKey, Item>[] snapshot;
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
                // The table is about to be emptied, so nothing is left to reclaim one by one.
                _evicted.Clear();
            }
            _store.Clear();
        }
    }

    public void InvalidateEntry(string path) => InvalidateEntries(new[] { path });

    /// <summary>Drops several paths in one store transaction. A save carries the file and its
    /// parent, a directory rename carries every path below it; one commit each was one fsync
    /// each, all inside the resolver's cache-update gate.</summary>
    public void InvalidateEntries(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return;
        var storeKeys = new List<string>(paths.Count);
        lock (_persistGate)
        {
            lock (_gate)
            {
                // The store rows go now, so a write still queued for one of these keys has to be
                // retired or it would put the entry back after the eviction.
                if (_retired.Count > 8192) _retired.Clear();
                foreach (var path in paths)
                {
                    var key = ResidentKey(path);
                    string storeKey = StoreKey(key);
                    storeKeys.Add(storeKey);
                    _retired.Add(storeKey);
                    Remove(key);
                }
            }
            _store.Apply(storeKeys.Select(k => new ObjectChange(k, null)).ToArray());
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
