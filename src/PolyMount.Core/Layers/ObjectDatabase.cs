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
    private long _storageCheckedAt;
    // Lookup cost, split by phase. Written under _gate, where the phases happen, so the
    // counters need no atomics of their own.
    private long _lookupCalls, _lookupTotalUs, _lookupGateUs, _lookupExistsUs, _lookupExistsChecks,
        _lookupLoadUs, _lookupMaxUs;
    public ObjectDatabase(string root, bool compress, bool recoverable = false, string? preferredPrefix = null,
        long metadataBudget = CacheBudgets.MetadataBytes)
    {
        _root = Path.Combine(root, "objects-v2");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "objects.db"), Pooling = true, DefaultTimeout = 30 }.ToString();
        _compress = compress;
        _recoverable = recoverable;
        _metadataBudget = metadataBudget;
        Directory.CreateDirectory(_root);
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS objects(path TEXT PRIMARY KEY COLLATE NOCASE, record TEXT NOT NULL);";
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
        }
        _metadataBytes = metadata;
    }
    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(_connectionString); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "PRAGMA synchronous=FULL;"; command.ExecuteNonQuery();
        return connection;
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
            return new(Interlocked.Read(ref _diskBytes), _records.Values.Sum(r => r.StoredBytes),
                _records.Values.Count(r => !r.Value.Header.IsDir), _records.Values.Count(r => r.Value.Header.IsDir),
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
        if (_recoverable) { _records.Clear(); _recordSizes.Clear(); _metadataBytes = 0; }
        Directory.CreateDirectory(_root);
        SqliteConnection.ClearAllPools();
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS objects(path TEXT PRIMARY KEY COLLATE NOCASE, record TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }

    /// <summary>The read path's version of <see cref="EnsureAvailable"/>: between checks a lookup
    /// answers from the resident index, so a store removed underneath us is noticed within
    /// <see cref="StorageCheckMs"/> instead of on the next lookup. Content blobs are still
    /// checked per hit, where a miss would otherwise be served as data. Writes and clears call
    /// <see cref="EnsureAvailable"/> directly — they cannot answer from a stale index.</summary>
    private void VerifyStorage()
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _storageCheckedAt) < StorageCheckMs) return;
        // Several threads may pass the check at once; each is one round trip, not a stampede.
        Interlocked.Exchange(ref _storageCheckedAt, now);
        _lookupExistsChecks++;
        EnsureAvailable();
    }
    public bool TryGet(string key, out VirtualObject value)
    {
        long started = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            long acquired = Stopwatch.GetTimestamp();
            long existsUs = 0, loadUs = 0;
            try
            {
                value = null!;
                bool resident = _records.TryGetValue(key, out var record);
                if (resident)
                {
                    // The store may have been removed underneath us; this drops the stale index.
                    long check = Stopwatch.GetTimestamp();
                    VerifyStorage();
                    existsUs += Microseconds(check, Stopwatch.GetTimestamp());
                    if (!_records.TryGetValue(key, out record)) return false;
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
                    // table, so a miss in memory is not a miss on disk.
                    long load = Stopwatch.GetTimestamp();
                    record = LoadRecord(key);
                    loadUs += Microseconds(load, Stopwatch.GetTimestamp());
                    if (record == null) return false;
                }
                if (record!.Blob != null)
                {
                    long check = Stopwatch.GetTimestamp();
                    bool present = File.Exists(Path.Combine(_root, record.Blob));
                    existsUs += Microseconds(check, Stopwatch.GetTimestamp());
                    if (!present)
                    {
                        if (!_recoverable) throw new IOException("Persistent object content was removed: " + key);
                        Apply(new[] { new ObjectChange(key, null) });
                        return false;
                    }
                }
                record = record with { Access = DateTime.UtcNow.Ticks };
                // Admission can evict the row again straight away under a tight budget; the
                // record just read stays valid, so answer from it either way.
                if (_records.ContainsKey(key)) _records[key] = record;
                else Admit(record);
                value = record.Value;
                return true;
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
    }

    /// <summary>Reads one row. Called only on a resident-index miss, where a pooled
    /// connection open costs far less than the source fetch it avoids.</summary>
    private Record? LoadRecord(string key)
    {
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
        if (_recordSizes.TryGetValue(record.Path, out var previous)) _metadataBytes -= previous;
        long size = CacheBudgets.Estimate(record.Path, record.Value);
        _recordSizes[record.Path] = size;
        _metadataBytes += size;
        _records[record.Path] = record;
        EvictResident();
    }

    /// <summary>Drops the least recently used resident records. Never issues a delete.</summary>
    private void EvictResident()
    {
        if (!_recoverable) return;
        if (_records.Count <= CacheBudgets.MaxObjects && _metadataBytes <= _metadataBudget) return;
        foreach (var record in _records.Values.OrderBy(r => r.Access).ToArray())
        {
            if (_records.Count <= CacheBudgets.MaxObjects && _metadataBytes <= _metadataBudget) break;
            _records.Remove(record.Path);
            if (_recordSizes.Remove(record.Path, out var size)) _metadataBytes -= size;
        }
    }
    public bool TryOpen(string key, out VirtualObject value, out Stream stream)
    {
        Record record;
        lock (_gate)
        {
            stream = null!;
            if (!TryGet(key, out value) || value.Header.IsDir) return false;
            record = _records[key];
            if (record.Blob == null) throw new InvalidDataException("File object has no data: " + key);
        }
        return TryOpenRecord(record, out stream);
    }
    internal bool TryOpenRecord(Record record, out Stream stream)
    {
        // Immutable published records can be opened without waiting for a cache
        // writer's SQLite transaction or blob copy.
        FileStream file = new(Path.Combine(_root, record.Blob!), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (!record.Compressed) { stream = file; return true; }
            // WinFsp requires seekable handles. Compressed objects are decoded to a temporary
            // disk-backed handle, not an unbounded in-memory byte array.
            var scratch = Scratch();
            try
            {
                using (file)
                using (var decoder = new BrotliStream(file, CompressionMode.Decompress)) decoder.CopyTo(scratch);
                scratch.Position = 0;
                stream = scratch;
                return true;
            }
            catch { scratch.Dispose(); throw; }
    }
    internal static FileStream Scratch() => new(Path.Combine(Path.GetTempPath(), "PolyMount-object-" + Guid.NewGuid().ToString("N")),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 131072, FileOptions.DeleteOnClose);

    public void Apply(IReadOnlyList<ObjectChange> changes)
    {
            lock (_gate) EnsureAvailable();
            var updates = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);
            var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var obsolete = new HashSet<string>();
            var created = new List<string>();
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
                            file.Flush(true);
                        }
                        }
                        stored = new FileInfo(Path.Combine(_root, blob)).Length;
                    }
                    else if (value.Children == null) throw new ArgumentException("Directory update requires a complete child list");
                    stored += JsonSerializer.SerializeToUtf8Bytes(value).Length;
                    updates[change.Path] = new(change.Path, value, blob, _compress && !value.Header.IsDir, stored, DateTime.UtcNow.Ticks);
                }
                // Blob copy/compression/fsync is independent per transaction and
                // must not hold the metadata mutex during large-file preparation.
                lock (_gate)
                {
                EnsureAvailable();
                foreach (var change in changes)
                    if (_records.TryGetValue(change.Path, out var old) && old.Blob != null) obsolete.Add(old.Blob);
                using (var connection = Connect())
                using (var transaction = connection.BeginTransaction())
                {
                    foreach (var change in changes)
                    {
                        using var command = connection.CreateCommand(); command.Transaction = transaction;
                        command.Parameters.AddWithValue("$path", change.Path);
                        if (change.Object == null) command.CommandText = "DELETE FROM objects WHERE path=$path";
                        else
                        {
                            command.CommandText = "INSERT INTO objects(path,record) VALUES($path,$record) ON CONFLICT(path) DO UPDATE SET record=excluded.record";
                            command.Parameters.AddWithValue("$record", JsonSerializer.Serialize(updates[change.Path]));
                        }
                        command.ExecuteNonQuery();
                    }
                    transaction.Commit();
                }
                foreach (var key in removed)
                {
                    _records.Remove(key);
                    if (_recordSizes.Remove(key, out var size)) _metadataBytes -= size;
                }
                foreach (var update in updates)
                {
                    if (_recordSizes.TryGetValue(update.Key, out var oldSize)) _metadataBytes -= oldSize;
                    long size = CacheBudgets.Estimate(update.Key, update.Value.Value);
                    _recordSizes[update.Key] = size; _metadataBytes += size;
                    _records[update.Key] = update.Value;
                }
                if (_recoverable) EvictResident();
                }
            }
            catch
            {
                foreach (var blob in created) { try { File.Delete(Path.Combine(_root, blob)); } catch { } }
                throw;
            }
            foreach (var blob in obsolete) { try { File.Delete(Path.Combine(_root, blob)); } catch { } }
    }
    // Records is only the resident subset, so clearing through it would leave rows behind
    // for a bounded index — they would reappear on the next restart. Delete the table.
    public void Clear()
    {
        var changes = new List<ObjectChange>();
        var blobs = new List<string>();
        lock (_gate)
        {
            EnsureAvailable();
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT path, record FROM objects";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                changes.Add(new ObjectChange(reader.GetString(0), null));
                var record = JsonSerializer.Deserialize<Record>(reader.GetString(1));
                if (record?.Blob != null) blobs.Add(record.Blob);
            }
        }
        if (changes.Count > 0) Apply(changes);
        foreach (var blob in blobs) { try { File.Delete(Path.Combine(_root, blob)); } catch { } }
    }
}
