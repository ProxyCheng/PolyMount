using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using PolyMount.Core;
using Microsoft.Data.Sqlite;

namespace PolyMount.Layers;

/// <summary>Path-keyed persistent KV store. Immutable blobs + atomic catalog publication.</summary>
internal sealed class ObjectDatabase
{
    internal sealed record Record(string Path, VirtualObject Value, string? Blob, bool Compressed, long StoredBytes, long Access);
    private readonly string _root, _connectionString;
    private readonly object _gate = new();
    private Dictionary<string, Record> _records;
    private long _metadataBytes;
    private readonly Dictionary<string, long> _recordSizes = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The reparse tag of every name that carries one, kept in memory: the resolver
    /// asks this for every entry it hands out, and a database read per entry would make an
    /// ordinary directory listing pay for a feature almost none of its names use.</summary>
    private readonly Dictionary<string, uint> _reparseTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _compress;
    private readonly bool _recoverable;
    private readonly long _metadataBudget;
    private long _usageCheckedAt;
    private long _diskBytes;
    private readonly object _usageGate = new();
    /// <summary>How often a lookup re-checks that the store is still on disk. The check is a
    /// filesystem round trip, and a directory listing performs one lookup per child per layer
    /// above the provider, so paying it on every lookup serialises enumeration behind a syscall:
    /// on 2026-10-15 one instance held this gate inside File.Exists for 55.7 s of a window while
    /// the lookups queued behind it accumulated a further 662 s. Once a second is how often a
    /// durable cache already revalidates its own storage.</summary>
    private const int StorageCheckMs = 1000;
    /// <summary>How stale a recency stamp may get before a hit refreshes it. Eviction orders
    /// records by this stamp; a second is finer than any eviction decision needs.</summary>
    private const long RecencyStampTicks = TimeSpan.TicksPerSecond;
    private long _storageCheckedAt;
    // Lookup cost, split by phase. Written under _gate, where the phases happen, so the
    // counters need no atomics of their own. TotalUs and MaxUs are gate-held time: the blob
    // check a disk read-back performs runs after the gate is released (see TryGet).
    private long _lookupCalls, _lookupTotalUs, _lookupGateUs, _lookupExistsUs, _lookupExistsChecks,
        _lookupLoadUs, _lookupMaxUs;
    /// <summary>How long a write may wait for companions before its transaction runs. One
    /// written file is two Apply calls (its stub and the commit at close) and a burst is
    /// hundreds, so a short window turns hundreds of FULL commits - each an fsync - into a
    /// handful. The resident index is updated when Apply returns, so this process still reads
    /// its own writes immediately; only durability moves, and a power cut loses at most the
    /// window still pending. Zero restores the old "commit before Apply returns" behaviour.</summary>
    private readonly int _commitWindowMs;
    /// <summary>Pending changes that trigger a commit without waiting for the window.</summary>
    private readonly int _commitBatchChanges;
    private const int CommitRetryMs = 1000;
    private readonly object _commitGate = new();
    private List<PendingChange> _pending = new();
    /// <summary>The latest pending change per path. A recoverable store's resident index may
    /// evict a row before its transaction runs, and a reader that falls through to the table
    /// must not see the row it replaced (or resurrect one that is pending deletion).</summary>
    private readonly Dictionary<string, PendingChange> _pendingByPath = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Paths whose previous row was not resident, so the blob to reclaim can only be
    /// learned from the table at commit time.</summary>
    private HashSet<string> _pendingAsk = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _pendingObsolete = new(StringComparer.Ordinal);
    /// <summary>Names whose reparse row goes with the object row in the same transaction.</summary>
    private HashSet<string> _pendingReparse = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _commitTimer;
    private int _commitArmed;
    private long _sequence;
    private Exception? _commitError;
    private long _commits;
    private bool _disposed;
    private sealed record PendingChange(long Sequence, string Path, Record? Value, string? Blob);
    /// <summary>Changes accepted but not yet in a transaction; exposed for the coalescing tests.</summary>
    internal int PendingChanges { get { lock (_gate) return _pending.Count; } }
    /// <summary>Transactions this store has actually committed; exposed for the coalescing tests.</summary>
    internal long Commits { get { lock (_gate) return _commits; } }
    public ObjectDatabase(string root, bool compress, bool recoverable = false, string? preferredPrefix = null,
        long metadataBudget = CacheBudgets.MetadataBytes, int commitWindowMs = 100, int commitBatchChanges = 256)
    {
        _commitWindowMs = commitWindowMs;
        _commitBatchChanges = Math.Max(1, commitBatchChanges);
        _root = Path.Combine(root, "objects-v2");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "objects.db"), Pooling = true, DefaultTimeout = 30 }.ToString();
        _compress = compress;
        _recoverable = recoverable;
        _metadataBudget = metadataBudget;
        Directory.CreateDirectory(_root);
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS objects(path TEXT PRIMARY KEY COLLATE NOCASE, record TEXT NOT NULL); CREATE TABLE IF NOT EXISTS reparse(path TEXT PRIMARY KEY COLLATE NOCASE, data BLOB NOT NULL);";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT record FROM objects";
        if (preferredPrefix != null)
        {
            // Old generations remain on disk, but must not consume the bounded
            // resident index before the currently mounted generation is loaded.
            command.CommandText += " ORDER BY CASE WHEN substr(path,1,length($prefix))=$prefix COLLATE NOCASE THEN 0 ELSE 1 END";
            command.Parameters.AddWithValue("$prefix", preferredPrefix);
        }
        using var reader = command.ExecuteReader();
        _records = new(StringComparer.OrdinalIgnoreCase);
        long metadata = 0;
        while (reader.Read())
        {
            var record = JsonSerializer.Deserialize<Record>(reader.GetString(0))!;
            long size = CacheBudgets.Estimate(record.Path, record.Value);
            if (_recoverable && (_records.Count >= CacheBudgets.MaxObjects || metadata + size > _metadataBudget)) continue;
            _records[record.Path] = record; metadata += size;
            _recordSizes[record.Path] = size;
            Track(record);
            _lru.AddLast(_positions[record.Path] = new LinkedListNode<string>(record.Path));
        }
        _metadataBytes = metadata;
        using (var reparse = connection.CreateCommand())
        {
            reparse.CommandText = "SELECT path,data FROM reparse";
            using var tags = reparse.ExecuteReader();
            while (tags.Read())
            {
                var data = (byte[])tags["data"];
                _reparseTags[tags.GetString(0)] = data.Length >= 4 ? BitConverter.ToUInt32(data, 0) : 0u;
            }
        }
    }

    /// <summary>Running totals over the resident index, so a status poll does not walk it.
    /// A quarter of a million records, three sums each, every second, under the metadata lock.</summary>
    private long _residentBytes;
    private int _residentFiles, _residentDirectories;
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _positions = new(StringComparer.OrdinalIgnoreCase);
    private void Track(Record record)
    {
        _residentBytes += record.StoredBytes;
        if (record.Value.Header.IsDir) _residentDirectories++; else _residentFiles++;
    }
    private void Untrack(Record record)
    {
        _residentBytes -= record.StoredBytes;
        if (record.Value.Header.IsDir) _residentDirectories--; else _residentFiles--;
    }
    /// <summary>Adds or replaces a resident record, keeping the totals and the recency order.</summary>
    private void Put(Record record)
    {
        if (_records.TryGetValue(record.Path, out var previous))
        {
            if (_recordSizes.TryGetValue(record.Path, out var size)) _metadataBytes -= size;
            Untrack(previous);
            if (_positions.TryGetValue(record.Path, out var node)) _lru.Remove(node);
        }
        long estimate = CacheBudgets.Estimate(record.Path, record.Value);
        _recordSizes[record.Path] = estimate; _metadataBytes += estimate;
        _records[record.Path] = record;
        Track(record);
        _lru.AddFirst(_positions[record.Path] = new LinkedListNode<string>(record.Path));
    }
    private void RemoveRecord(string key)
    {
        if (!_records.Remove(key, out var record)) return;
        Untrack(record);
        if (_recordSizes.Remove(key, out var size)) _metadataBytes -= size;
        if (_positions.Remove(key, out var node)) _lru.Remove(node);
    }
    private void MoveToFront(string key)
    {
        if (!_positions.TryGetValue(key, out var node) || _lru.First == node) return;
        _lru.Remove(node); _lru.AddFirst(node);
    }
    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(_connectionString); connection.Open();
        // A recoverable store is a cache: losing its last transactions to a power cut costs a
        // re-read from the source, so WAL's NORMAL (no fsync per commit) is the right trade.
        // User data keeps FULL.
        using var command = connection.CreateCommand(); command.CommandText = _recoverable ? "PRAGMA synchronous=NORMAL;" : "PRAGMA synchronous=FULL;"; command.ExecuteNonQuery();
        return connection;
    }
    /// <summary>Every stored key at or below <paramref name="path"/>, from the table rather than
    /// the resident index (which may be a bounded subset). A key range over the primary key:
    /// '/' is 0x2F and '0' is 0x30, so [path/, path0) is exactly the keys below path.</summary>
    internal IReadOnlyList<string> KeysAtOrBelow(string path)
    {
        // The table is the authority for keys the index may not hold, so accepted changes
        // have to reach it first. This is a management action, not a read path.
        Commit();
        var keys = new List<string>();
        lock (_gate)
        {
            EnsureAvailable();
            using var connection = Connect();
            using var command = connection.CreateCommand();
            if (path.Length == 0) command.CommandText = "SELECT path FROM objects";
            else
            {
                command.CommandText = "SELECT path FROM objects WHERE path=$path OR (path>=$low AND path<$high)";
                command.Parameters.AddWithValue("$path", path);
                command.Parameters.AddWithValue("$low", path + "/");
                command.Parameters.AddWithValue("$high", path + "0");
            }
            using var reader = command.ExecuteReader();
            while (reader.Read()) keys.Add(reader.GetString(0));
        }
        return keys;
    }
    /// <summary>Resident records, counted without copying the index.</summary>
    public int Count { get { lock (_gate) { VerifyStorage(); return _records.Count; } } }
    /// <summary>Whether the resident index already holds a key. A caller deciding whether a
    /// write is worth queueing asks this rather than <see cref="TryGet"/>: a recoverable store
    /// answers a miss by reading the table back, which is the right cost for a read and the
    /// wrong one for a decision.</summary>
    internal bool Contains(string key) { lock (_gate) return _records.ContainsKey(key); }
    /// <summary>Commits whatever is still waiting, then releases the pooled connections so the
    /// store's files can be deleted or moved (clearing an instance's cache, removing its store
    /// directory).</summary>
    public void Dispose()
    {
        // A shutdown is a durability point: a normal unmount waits for this, so the window
        // only loses writes when the process is killed or the machine goes down.
        try { Commit(); } catch (Exception) { }
        lock (_gate) { _disposed = true; _commitArmed = 0; _commitTimer?.Dispose(); _commitTimer = null; }
        SqliteConnection.ClearAllPools();
    }
    public IReadOnlyList<Record> Records { get { lock (_gate) { EnsureAvailable(); return _records.Values.ToArray(); } } }
    internal LayerStorageUsage GetStorageUsage()
    {
        // Status polling must not stat every blob under the metadata mutex.
        // Disk usage is observational and may lag writes by up to ten seconds.
        if (Environment.TickCount64 - Interlocked.Read(ref _usageCheckedAt) >= 10000 && Monitor.TryEnter(_usageGate))
        {
            try
            {
                long diskBytes = 0;
                if (Directory.Exists(_root))
                    foreach (var file in new DirectoryInfo(_root).EnumerateFiles())
                        try { diskBytes += file.Length; } catch (FileNotFoundException) { }
                Interlocked.Exchange(ref _diskBytes, diskBytes);
                Interlocked.Exchange(ref _usageCheckedAt, Environment.TickCount64);
            }
            finally { Monitor.Exit(_usageGate); }
        }
        lock (_gate)
        {
            return new(Interlocked.Read(ref _diskBytes), _residentBytes,
                _residentFiles, _residentDirectories,
                Lookups: new LookupProfile(_lookupCalls, _lookupTotalUs, _lookupGateUs, _lookupExistsUs,
                    _lookupExistsChecks, _lookupLoadUs, _lookupMaxUs));
        }
    }

    private static long Microseconds(long from, long to) =>
        (to - from) * 1_000_000L / Stopwatch.Frequency;
    internal Record? GetPublishedRecord(string key)
    {
        lock (_gate) return _records.GetValueOrDefault(key);
    }
    private void EnsureAvailable()
    {
        if (File.Exists(Path.Combine(_root, "objects.db"))) return;
        if (_records.Count > 0 && !_recoverable) throw new IOException("Persistent layer storage was removed: " + _root);
        if (_recoverable) { _records.Clear(); _recordSizes.Clear(); _positions.Clear(); _lru.Clear(); _metadataBytes = 0; _residentBytes = 0; _residentFiles = 0; _residentDirectories = 0; DropPendingLocked(); }
        Directory.CreateDirectory(_root);
        SqliteConnection.ClearAllPools();
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS objects(path TEXT PRIMARY KEY COLLATE NOCASE, record TEXT NOT NULL); CREATE TABLE IF NOT EXISTS reparse(path TEXT PRIMARY KEY COLLATE NOCASE, data BLOB NOT NULL);";
        command.ExecuteNonQuery();
    }

    /// <summary>The read path's version of <see cref="EnsureAvailable"/>: between checks a lookup
    /// answers from the resident index, so a store removed underneath us is noticed within
    /// <see cref="StorageCheckMs"/> instead of on the next lookup. A record read back from disk
    /// still has its blob checked, where a miss would otherwise be served as data — outside the
    /// gate, see <see cref="TryGet(string, out VirtualObject, out Record?)"/>. Writes and clears
    /// call <see cref="EnsureAvailable"/> directly — they cannot answer from a stale index.</summary>
    private void VerifyStorage()
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _storageCheckedAt) < StorageCheckMs) return;
        // Several threads may pass the check at once; each is one round trip, not a stampede.
        Interlocked.Exchange(ref _storageCheckedAt, now);
        _lookupExistsChecks++;
        EnsureAvailable();
    }
    public bool TryGet(string key, out VirtualObject value) => TryGet(key, out value, out _);

    /// <summary>Looks a row up and hands back the record it came from. A caller that keeps
    /// the value has to keep the record with it: the blob path is the only way back to the
    /// bytes, and nothing rewrites that row for it.
    ///
    /// A row read back from disk has its blob confirmed once, after the gate is released. A
    /// cache also confirms resident records, because that is how a vanished blob becomes a
    /// miss; an unrecoverable store does not — the per-hit stat was the single largest cost
    /// in an overlay lookup (tens of microseconds on this machine, paid by every lookup of
    /// every file record while the metadata gate waited on it), and the open path already
    /// fails loudly when the content is gone. The lookup profile counts the gate-held time.</summary>
    internal bool TryGet(string key, out VirtualObject value, out Record? record)
    {
        long started = Stopwatch.GetTimestamp();
        Record? found;
        bool resident = false;
        lock (_gate)
        {
            long acquired = Stopwatch.GetTimestamp();
            long existsUs = 0, loadUs = 0;
            try
            {
                value = null!;
                record = null;
                resident = _records.TryGetValue(key, out found);
                if (resident)
                {
                    // The store may have been removed underneath us; this drops the stale index.
                    long check = Stopwatch.GetTimestamp();
                    VerifyStorage();
                    existsUs += Microseconds(check, Stopwatch.GetTimestamp());
                    if (!_records.TryGetValue(key, out found)) return false;
                }
                else
                {
                    // An unrecoverable store must surface a removed database rather than
                    // report its objects as absent.
                    if (!_recoverable)
                    {
                        long check = Stopwatch.GetTimestamp();
                        VerifyStorage();
                        existsUs += Microseconds(check, Stopwatch.GetTimestamp());
                        return false;
                    }
                    // A recoverable store keeps a bounded resident index over a complete
                    // table, so a miss in memory is not a miss on disk. A change accepted but
                    // not yet committed is not in the table either: the pending map is the
                    // only place that knows it, and a pending deletion must not be resurrected
                    // by the row still on disk.
                    if (_pendingByPath.TryGetValue(key, out var pending))
                    {
                        if (pending.Value == null) return false;
                        found = pending.Value;
                    }
                    else
                    {
                        long load = Stopwatch.GetTimestamp();
                        found = LoadRecord(key);
                        loadUs += Microseconds(load, Stopwatch.GetTimestamp());
                        if (found == null) return false;
                    }
                }
                // Recency orders eviction and nothing reads its exact value, so a hit refreshes
                // the stamp at most once a second: stamping every hit allocated a record and
                // rewrote the index entry on every lookup of every file. A row just read back
                // from disk is always stamped, so a slot it was loaded into is fresh.
                long now = DateTime.UtcNow.Ticks;
                if (!resident || now - found.Access >= RecencyStampTicks)
                {
                    found = found with { Access = now };
                    if (_records.ContainsKey(key)) { _records[key] = found; MoveToFront(key); }
                    else Admit(found);
                }
                value = found.Value;
                record = found;
            }
            finally
            {
                long total = Microseconds(started, Stopwatch.GetTimestamp());
                _lookupCalls++;
                _lookupTotalUs += total;
                _lookupGateUs += Microseconds(started, acquired);
                _lookupExistsUs += existsUs;
                _lookupLoadUs += loadUs;
                if (total > _lookupMaxUs) _lookupMaxUs = total;
            }
        }
        // A record whose blob is gone is removed, not answered: a cache must fall through to
        // the source, and an unrecoverable store must say its content was removed rather than
        // quietly serve nothing. `Apply` re-enters the gate on its own on this rare path.
        //
        // A cache (recoverable) checks even its resident records: that is what turns a vanished
        // blob into a miss on the next header lookup. An unrecoverable store — the overlay and
        // the write store, where content is user data — checks only rows it had to read back
        // from disk; a resident one is confirmed when it is opened, which fails loudly, and
        // stat-ing it on every header lookup cost this machine tens of microseconds per file.
        if ((!resident || _recoverable) && record is { Blob: not null } current && !File.Exists(BlobPath(current.Blob)))
        {
            if (!_recoverable) throw new IOException("Persistent object content was removed: " + key);
            Apply(new[] { new ObjectChange(key, null) });
            value = null!;
            record = null;
            return false;
        }
        return true;
    }

    /// <summary>The file a record's content lives in, checked before it is opened or deleted.
    ///
    /// The name comes out of the catalog, which is a file on disk: whoever can write the store can
    /// also write a row whose blob reads <c>..\..\Windows\System32\anything</c>, and
    /// <see cref="Path.Combine(string, string)"/> is happy to hand that back. A record may only
    /// name a file this database created — the shape is a Guid and a suffix — so a row that names
    /// anything else is corrupt rather than interesting, and it is refused here instead of being
    /// opened or deleted with the service account's rights.</summary>
    private string BlobPath(string blob)
    {
        if (!IsBlobName(blob)) throw new InvalidDataException("the store names a content file outside it: " + blob);
        return Path.Combine(_root, blob);
    }

    /// <summary>Whether a name is one this database writes: 32 hex digits and <c>.blob</c>. The
    /// check is a length and a character scan because it runs on the read path.</summary>
    private static bool IsBlobName(string name)
    {
        if (name.Length is < 5 or > 96) return false;
        if (!name.EndsWith(".blob", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (char c in name)
            if (!char.IsAsciiHexDigit(c) && c != '.' && c != 'b' && c != 'l' && c != 'o')
                return false;
        return true;
    }

    /// <summary>Blob names of rows the resident index does not hold, read in batches.
    /// Each path is a statement variable, and SQLite refuses a statement with more of them
    /// than its build allows ("too many SQL variables"). The index is bounded, the table is
    /// not, so a caller can hand over every row of a large store at once — clearing G83's
    /// cache did.</summary>
    private const int StoredBlobBatch = 500;
    private void StoredBlobs(HashSet<string> keys, HashSet<string> into)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        var batch = new List<string>(StoredBlobBatch);
        foreach (var key in keys)
        {
            batch.Add(key);
            if (batch.Count < StoredBlobBatch) continue;
            Read(batch);
            batch.Clear();
        }
        if (batch.Count > 0) Read(batch);

        void Read(List<string> chunk)
        {
            command.Parameters.Clear();
            var names = new List<string>(chunk.Count);
            for (int i = 0; i < chunk.Count; i++)
            {
                string name = "$k" + i;
                names.Add(name);
                command.Parameters.AddWithValue(name, chunk[i]);
            }
            command.CommandText = "SELECT record FROM objects WHERE path IN (" + string.Join(",", names) + ")";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                if (JsonSerializer.Deserialize<Record>(reader.GetString(0))?.Blob is { } blob) into.Add(blob);
        }
    }

    /// <summary>Reads one row. Called only on a resident-index miss, where a pooled
    /// connection open costs far less than the source fetch it avoids.</summary>
    private Record? LoadRecord(string key)    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT record FROM objects WHERE path=$path";
        command.Parameters.AddWithValue("$path", key);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<Record>(json) : null;
    }

    /// <summary>Adds a record to the resident index and re-applies its bound.</summary>
    private void Admit(Record record)
    {
        Put(record);
        EvictResident();
    }

    /// <summary>Drops the least recently used resident records. Never issues a delete.</summary>
    private void EvictResident()
    {
        if (!_recoverable) return;
        if (_records.Count <= CacheBudgets.MaxObjects && _metadataBytes <= _metadataBudget) return;
        // Least recent first, from the recency list: one step per record dropped, where sorting
        // the whole index per admission was the cost of every read-back once it was full.
        while (_lru.Last is { } tail && (_records.Count > CacheBudgets.MaxObjects || _metadataBytes > _metadataBudget)) RemoveRecord(tail.Value);
    }
    public bool TryOpen(string key, out VirtualObject value, out Stream stream)
    {
        Record record;
        lock (_gate)
        {
            stream = null!;
            if (!TryGet(key, out value, out var found) || value.Header.IsDir || found == null) return false;
            record = found;
            if (record.Blob == null) throw new InvalidDataException("File object has no data: " + key);
        }
        return TryOpenRecord(record, out stream);
    }
    internal bool TryOpenRecord(Record record, out Stream stream)
    {
        // Immutable published records can be opened without waiting for a cache
        // writer's SQLite transaction or blob copy. Write sharing is allowed for one
        // reason: the commit thread fsyncs a blob while readers hold it, and a
        // read-only share would make that open fail. Nothing ever writes the bytes.
        FileStream file = new(BlobPath(record.Blob!), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!record.Compressed) { stream = file; return true; }
            // WinFsp requires seekable handles. Compressed objects are decoded to a temporary
            // disk-backed handle, not an unbounded in-memory byte array.
            var scratch = Scratch();
            try
            {
                using (file)
                using (var decoder = new BrotliStream(file, CompressionMode.Decompress))
                    Decode(decoder, scratch, record.Value.Header.Size);
                scratch.Position = 0;
                stream = scratch;
                return true;
            }
            catch { scratch.Dispose(); throw; }
    }
    /// <summary>Decodes a compressed object, refusing to write more than the record says it holds.
    /// Brotli amplifies: a few kilobytes of blob can expand to gigabytes, and the scratch file is
    /// on the system volume. The declared size is the bound the writer stored, so a body that
    /// outgrows it is a store that has been tampered with rather than a file that is larger than
    /// it said.</summary>
    private static void Decode(Stream decoder, Stream scratch, long size)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read = decoder.Read(buffer, 0, buffer.Length);
            if (read == 0) return;
            total += read;
            if (total > size)
                throw new InvalidDataException("a compressed object expands past the size its record holds");
            scratch.Write(buffer, 0, read);
        }
    }

    internal static FileStream Scratch() => new(Path.Combine(Path.GetTempPath(), "PolyMount-object-" + Guid.NewGuid().ToString("N")),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 131072, FileOptions.DeleteOnClose);

    public void Apply(IReadOnlyList<ObjectChange> changes)
    {
            // A transaction that failed in the background is retried before more work piles
            // up: the caller of the next write is the one that can still be told about it.
            if (Volatile.Read(ref _commitError) != null) Commit();

            lock (_gate) EnsureAvailable();
            var updates = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);
            var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var removedReparse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var obsolete = new HashSet<string>(StringComparer.Ordinal);
            var created = new List<string>();
            bool accepted = false, commitNow = false;
            try
            {
                foreach (var change in changes)
                {
                    if (change.Object == null) { updates.Remove(change.Path); removed.Add(change.Path); continue; }
                    removed.Remove(change.Path);
                    var value = change.Object;
                    string? blob = null;
                    long stored = 0;
                    if (!value.Header.IsDir)
                    {
                        if (change.Content == null) throw new ArgumentException("File update requires content");
                        blob = Guid.NewGuid().ToString("N") + ".blob";
                        created.Add(blob);
                        if (!_compress && change.Content is CommitSnapshot prepared)
                        {
                            prepared.PublishTo(Path.Combine(_root, blob));
                        }
                        else
                        {
                        using (var file = new FileStream(Path.Combine(_root, blob), FileMode.CreateNew, FileAccess.Write))
                        {
                            if (_compress)
                            {
                                using var encoder = new BrotliStream(file, CompressionLevel.Fastest, leaveOpen: true);
                                change.Content.CopyTo(encoder);
                            }
                            else change.Content.CopyTo(file);
                            file.Flush();
                        }
                        }
                        stored = new FileInfo(Path.Combine(_root, blob)).Length;
                    }
                    else if (value.Children == null) throw new ArgumentException("Directory update requires a complete child list");
                    stored += JsonSerializer.SerializeToUtf8Bytes(value).Length;
                    updates[change.Path] = new(change.Path, value, blob, _compress && !value.Header.IsDir, stored, DateTime.UtcNow.Ticks);
                }
                // The batch is accepted under the metadata lock: readers see the write as soon
                // as Apply returns, and the transaction carries everything accepted before it
                // runs. Blob preparation stays outside this lock.
                lock (_gate)
                {
                EnsureAvailable();
                // The blob a row is about to stop naming. A resident record knows it; one the
                // index dropped (or never loaded, in a recoverable store) does not, and reading
                // it back is only needed when the batch commits - a failed commit must not have
                // taken the old content away from the row still on disk.
                HashSet<string>? ask = null;
                foreach (var change in changes)
                {
                    if (_records.TryGetValue(change.Path, out var old)) { if (old.Blob != null) obsolete.Add(old.Blob); }
                    else if (_recoverable) (ask ??= new(StringComparer.OrdinalIgnoreCase)).Add(change.Path);
                }
                long sequence = ++_sequence;
                foreach (var change in changes)
                {
                    Record? record = change.Object == null ? null : updates[change.Path];
                    if (record == null) removedReparse.Add(change.Path);
                    var pending = new PendingChange(sequence, change.Path, record, record?.Blob);
                    _pending.Add(pending);
                    _pendingByPath[change.Path] = pending;
                }
                if (ask is { Count: > 0 }) _pendingAsk.UnionWith(ask);
                _pendingObsolete.UnionWith(obsolete);
                _pendingReparse.UnionWith(removedReparse);
                foreach (var key in removed) RemoveRecord(key);
                // The tag is what the resolver reads, so it goes as soon as the delete is
                // accepted; the row itself is dropped in the batch's transaction.
                foreach (var key in removedReparse) _reparseTags.Remove(key);
                foreach (var update in updates) Put(update.Value);
                if (_recoverable) EvictResident();
                accepted = true;
                commitNow = _commitWindowMs <= 0 || _pending.Count >= _commitBatchChanges;
                if (!commitNow) ArmTimerLocked(_commitWindowMs);
                }
            }
            catch
            {
                // The changes never entered the batch, so their immutable blobs are orphans.
                if (!accepted) foreach (var blob in created) { try { if (IsBlobName(blob)) File.Delete(Path.Combine(_root, blob)); } catch { } }
                throw;
            }
            if (commitNow) Commit();
    }
    /// <summary>Runs every accepted change in one transaction. Called when the coalescing
    /// window elapses, when a batch reaches its bound, and by anything that needs the table to
    /// be current: a read-back with a bounded index, a range query, a clear, a shutdown.
    /// Writers already read back what they wrote - Apply publishes to the resident index
    /// first - so what moves is durability, not visibility.</summary>
    internal void Commit()
    {
        while (true)
        {
            List<PendingChange> batch;
            HashSet<string> ask, obsolete, reparse;
            long last;
            lock (_commitGate)
            {
                lock (_gate)
                {
                    if (_pending.Count == 0) { DisarmLocked(); _commitError = null; return; }
                    batch = _pending; _pending = new List<PendingChange>(batch.Count);
                    ask = _pendingAsk; _pendingAsk = new(StringComparer.OrdinalIgnoreCase);
                    obsolete = _pendingObsolete; _pendingObsolete = new(StringComparer.Ordinal);
                    reparse = _pendingReparse; _pendingReparse = new(StringComparer.OrdinalIgnoreCase);
                    last = batch[^1].Sequence;
                }
                try { WriteBatch(batch, ask, obsolete, reparse); }
                catch (Exception ex)
                {
                    // The batch goes back in front. Changes accepted while it was running
                    // must not overtake it, and the next attempt covers both.
                    lock (_gate)
                    {
                        if (_pending.Count > 0) batch.AddRange(_pending);
                        _pending = batch;
                        _pendingAsk.UnionWith(ask);
                        _pendingObsolete.UnionWith(obsolete);
                        _pendingReparse.UnionWith(reparse);
                        _commitError = ex;
                        _commitArmed = 0;
                        ArmTimerLocked(CommitRetryMs);
                    }
                    throw;
                }
                lock (_gate)
                {
                    foreach (var change in batch)
                        if (_pendingByPath.TryGetValue(change.Path, out var latest) && latest.Sequence <= last)
                            _pendingByPath.Remove(change.Path);
                    _commitError = null;
                    _commits++;
                    if (_pending.Count == 0) { DisarmLocked(); return; }
                    if (_commitArmed == 0) ArmTimerLocked(_commitWindowMs);
                }
            }
        }
    }

    private void WriteBatch(List<PendingChange> batch, HashSet<string> ask, HashSet<string> obsolete,
        HashSet<string> reparseDrops)
    {
        // The bytes a row names have to be on disk before the row that names them is. The
        // writer only handed them to the operating system; the fsyncs happen here, on the
        // commit thread, so a burst pays them together instead of each caller paying one.
        MakeBlobsDurable(batch, obsolete);
        // A row the index did not hold still names a blob in the table. It is only safe to
        // reclaim once this transaction is in place, so the lookup happens here rather than
        // when the change was accepted.
        if (ask.Count > 0) StoredBlobs(ask, obsolete);
        // Handled here, copied: the caller's set goes back into the queue if this batch
        // fails, and it has to still name everything that was not deleted.
        var drops = new HashSet<string>(reparseDrops, StringComparer.OrdinalIgnoreCase);
        using (var connection = Connect())
        using (var transaction = connection.BeginTransaction())
        {
            foreach (var change in batch)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.Parameters.AddWithValue("$path", change.Path);
                if (change.Value == null)
                {
                    command.CommandText = "DELETE FROM objects WHERE path=$path";
                    command.ExecuteNonQuery();
                    // A reparse point belongs to the name it was set on; a delete takes it
                    // with the record, in the same transaction.
                    if (drops.Remove(change.Path)) DropReparse(connection, transaction, change.Path);
                    continue;
                }
                command.CommandText = "INSERT INTO objects(path,record) VALUES($path,$record) ON CONFLICT(path) DO UPDATE SET record=excluded.record";
                command.Parameters.AddWithValue("$record", JsonSerializer.Serialize(change.Value));
                command.ExecuteNonQuery();
            }
            // A name can carry a reparse row without an object row (the row was evicted from
            // the index, or the name never had one); its delete still belongs to this batch.
            foreach (var path in drops) DropReparse(connection, transaction, path);
            transaction.Commit();
        }
        foreach (var blob in obsolete) { try { if (IsBlobName(blob)) File.Delete(Path.Combine(_root, blob)); } catch { } }
    }

    private static void DropReparse(SqliteConnection connection, SqliteTransaction transaction, string path)
    {
        using var reparse = connection.CreateCommand();
        reparse.Transaction = transaction;
        reparse.CommandText = "DELETE FROM reparse WHERE path=$path";
        reparse.Parameters.AddWithValue("$path", path);
        reparse.ExecuteNonQuery();
    }

    /// <summary>Flushes the batch's blob files to the device, before the transaction that
    /// names them commits. A blob this same transaction stops naming - the empty stub an
    /// OpenWrite publishes, replaced by the close's content - needs no flush: no committed
    /// row will refer to it. A missing blob is a cache's miss (it will fall through to the
    /// source) and a hard failure for user data.</summary>
    private void MakeBlobsDurable(List<PendingChange> batch, HashSet<string> obsolete)
    {
        foreach (var change in batch)
        {
            if (change.Blob is not { } blob || obsolete.Contains(blob)) continue;
            string path = Path.Combine(_root, blob);
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                file.Flush(true);
            }
            catch (FileNotFoundException) when (_recoverable) { }
        }
    }

    /// <summary>Arms the one-shot commit timer. Called under <see cref="_gate"/>; a burst
    /// does not push the deadline forward, because a sliding window under continuous writes
    /// would never fire.</summary>
    private void ArmTimerLocked(int delayMs)
    {
        if (_disposed) return;
        if (_commitArmed == 1) return;
        _commitArmed = 1;
        _commitTimer ??= new Timer(_ => OnCommitWindow(), null, Timeout.Infinite, Timeout.Infinite);
        _commitTimer.Change(delayMs, Timeout.Infinite);
    }
    private void DisarmLocked()
    {
        _commitArmed = 0;
        _commitTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }
    private void OnCommitWindow()
    {
        lock (_gate) _commitArmed = 0;
        try { Commit(); }
        catch (Exception ex)
        {
            // The store is not answering; keep the batch and try again later rather than
            // dropping what the callers were told was accepted.
            lock (_gate) { _commitError = ex; ArmTimerLocked(CommitRetryMs); }
        }
    }
    /// <summary>Forgets accepted changes without running them: only a recoverable store may
    /// do this, and only when its file disappeared underneath it.</summary>
    private void DropPendingLocked()
    {
        _pending.Clear();
        _pendingByPath.Clear();
        _pendingAsk.Clear();
        _pendingObsolete.Clear();
        _pendingReparse.Clear();
    }
    /// <summary>The reparse data stored for a path, if any. It lives beside the object records
    /// but is not one of them: the bytes are opaque to this layer and are handed back to
    /// Windows, which resolves a name-surrogate target itself.</summary>
    public bool TryGetReparse(string path, out byte[]? data)
    {
        lock (_gate)
        {
            EnsureAvailable();
            // A delete that has been accepted but not yet committed is already invisible: the
            // row leaves the table in the batch's transaction, and until then the name must
            // not resolve through data that is on its way out.
            if (_pendingReparse.Contains(path)) { data = null; return false; }
        }
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM reparse WHERE path=$path";
        command.Parameters.AddWithValue("$path", path);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) { data = null; return false; }
        data = (byte[])reader["data"];
        return true;
    }

    public void SetReparse(string path, byte[] data)
    {
        // Anything already accepted goes first, so a delete waiting in the batch cannot land
        // after the row this call writes and take it away.
        Commit();
        lock (_gate) EnsureAvailable();
        using var connection = Connect();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO reparse(path,data) VALUES($path,$data) ON CONFLICT(path) DO UPDATE SET data=excluded.data";
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$data", data);
        command.ExecuteNonQuery();
        transaction.Commit();
        lock (_gate) _reparseTags[path] = data.Length >= 4 ? BitConverter.ToUInt32(data, 0) : 0u;
    }

    public bool DeleteReparse(string path)
    {
        lock (_gate) EnsureAvailable();
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM reparse WHERE path=$path";
        command.Parameters.AddWithValue("$path", path);
        bool removed = command.ExecuteNonQuery() > 0;
        lock (_gate) _reparseTags.Remove(path);
        return removed;
    }

    /// <summary>The tag of a name's reparse point, from the resident map.</summary>
    public bool TryGetReparseTag(string path, out uint tag)
    {
        lock (_gate) return _reparseTags.TryGetValue(path, out tag);
    }

    // Records is only the resident subset, so clearing through it would leave rows behind
    // for a bounded index — they would reappear on the next restart. Delete the table.
    //
    // One statement, not one per row through Apply: the index is bounded, the table is not,
    // and going through Apply asked it to name every non-resident path in a single
    // `IN (...)` — more variables than SQLite accepts on a store the size of G83's cache.
    // Collecting the blob names and emptying the table under the same lock keeps a write
    // racing with the clear whole: it either commits before (and is cleared too) or waits
    // and commits after with the blob it just wrote, which this list never saw.
    public void Clear()
    {
        // Accepted changes have to reach the table first; a row still in the batch would
        // commit after the clear and come back. The commit gate is held throughout: a batch
        // already taken from the queue either finishes before this (and is cleared too) or
        // waits and lands after, where its own row is the one that survives.
        lock (_commitGate)
        {
            Commit();
            var blobs = new List<string>();
            lock (_gate)
            {
                EnsureAvailable();
                using var connection = Connect();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT record FROM objects";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                        if (JsonSerializer.Deserialize<Record>(reader.GetString(0))?.Blob is { } blob) blobs.Add(blob);
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "DELETE FROM objects";
                    command.ExecuteNonQuery();
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "DELETE FROM reparse";
                    command.ExecuteNonQuery();
                }
                _records.Clear();
                _recordSizes.Clear();
                _positions.Clear();
                _lru.Clear();
                _reparseTags.Clear();
                _metadataBytes = 0;
                _residentBytes = 0;
                _residentFiles = 0;
                _residentDirectories = 0;
            }
            foreach (var blob in blobs) { try { if (IsBlobName(blob)) File.Delete(Path.Combine(_root, blob)); } catch { } }
        }
    }
}
