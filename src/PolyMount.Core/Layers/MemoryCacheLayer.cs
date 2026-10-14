using PolyMount.Core;

namespace PolyMount.Layers;

/// <summary>Bounded LRU of directory snapshots and file blocks. Dirty blocks belong to a live staging stream.</summary>
public sealed class MemoryCacheLayer : CacheLayer, ICacheLayer, IWriteAwareCache, IFileStreamLayer, ILayerStorageUsage, IObjectManagement, IDisposable
{
    private sealed class Page(string key, string path, long offset, byte[] bytes, Session session)
    {
        public readonly string Key = key, Path = path;
        public readonly long Offset = offset;
        public readonly byte[] Bytes = bytes;
        // Clean read pages must not retain closed streams (or their whole-file buffers).
        public readonly Session? Owner = session.Writable ? session : null;
        public bool Dirty;
        public LinkedListNode<object> Node = null!;
    }
    private sealed class Session(string key, string path, Stream backing, bool writable)
    {
        public readonly string Key = key, Path = path;
        public readonly Stream Backing = backing;
        public readonly bool Writable = writable;
        public long Length = backing.Length;
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Page> _pages = new();
    private sealed class DirectorySnapshot(string path, VirtualObject value, long bytes)
    {
        public readonly string Path = path;
        public readonly VirtualObject Value = value;
        public readonly long Bytes = bytes;
        public LinkedListNode<object> Node = null!;
    }
    private readonly Dictionary<string, DirectorySnapshot> _directories = new();
    private readonly LinkedList<object> _lru = new();
    private long _directoryBytes;
    private string? _directoryGeneration;
    private readonly Func<string> _generation;
    private bool _disposed;
    private long _hits, _misses, _writeBacks;
    public MemoryCacheLayer(long capacityBytes = 16 * 1048576, int blockSize = 65536, Func<string>? generation = null)
    {
        if (blockSize < 4096 || blockSize > 1048576 || (blockSize & (blockSize - 1)) != 0 || capacityBytes < blockSize)
            throw new ArgumentException("Memory cache requires a power-of-two block size from 4 KiB to 1 MiB and capacity >= block size");
        Capacity = capacityBytes; BlockSize = blockSize; _generation = generation ?? (() => "memory");
    }
    public override string Name => "memory-cache";
    public override string Generation => _generation();
    // Stream writes are buffered; namespace objects always pass to the next provider.
    public override bool Writable => false;
    public long Capacity { get; }
    public int BlockSize { get; }
    public long Hits { get { lock (_gate) return _hits; } }
    public long Misses { get { lock (_gate) return _misses; } }
    public long WriteBacks { get { lock (_gate) return _writeBacks; } }
    public long MemoryBytes { get { lock (_gate) return (long)_pages.Count * BlockSize + _directoryBytes; } }
    private static string Normalize(string path) => VfsPath.Normalize(path).ToUpperInvariant();
    private void CheckDirectoryGeneration()
    {
        string generation = Generation;
        if (_directoryGeneration == generation) return;
        foreach (var item in _directories.Values.ToArray()) Remove(item);
        _directoryGeneration = generation;
    }
    public override bool TryReadObject(string path, out VirtualObject value)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bool found = TryGetDirectory(path, out var item);
            value = found ? item.Value : null!;
            return found;
        }
    }
    public void TouchObject(string path)
    {
        // Same recency update a hit performs, for reads a higher cache answered.
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); TryGetDirectory(path, out _); }
    }
    private bool TryGetDirectory(string path, out DirectorySnapshot item)
    {
        CheckDirectoryGeneration();
        if (!_directories.TryGetValue(Normalize(path), out var found)) { item = null!; return false; }
        _lru.Remove(found.Node); _lru.AddFirst(found.Node);
        item = found; return true;
    }
    public override bool TryApply(IReadOnlyList<ObjectChange> changes) => false;
    public override bool TryGetEntry(string path, out VfsEntry entry)
    {
        bool found = TryReadObject(path, out var value); entry = found ? value.Header : null!; return found;
    }
    public override IReadOnlyList<VfsEntry> ListOwn(string path) => TryReadObject(path, out var value)
        ? value.Children! : Array.Empty<VfsEntry>();
    public override bool TryOpenRead(string path, out VfsEntry entry, out Stream stream) { entry = null!; stream = null!; return false; }
    public void Store(string path, string version, VfsEntry entry, Stream content) { }
    public void StoreObject(string path, VirtualObject value, Stream? content = null)
    {
        if (!value.Header.IsDir) return;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CheckDirectoryGeneration();
            string key = Normalize(path);
            if (_directories.TryGetValue(key, out var previous)) Remove(previous);
            long bytes = CacheBudgets.Estimate(key, value);
            if (bytes > Capacity) return;
            while (MemoryBytes + bytes > Capacity || _directories.Count >= CacheBudgets.MaxObjects) RemoveOldest();
            var snapshot = value.Snapshot();
            var item = new DirectorySnapshot(key, snapshot, bytes);
            item.Node = _lru.AddFirst(item); _directories.Add(key, item); _directoryBytes += bytes;
        }
    }
    public void UpdateAfterWrite(IReadOnlyList<ObjectChange> changes)
    {
        lock (_gate)
        {
            CheckDirectoryGeneration();
            foreach (var page in _pages.Values.Where(p => p.Owner == null).ToArray()) Remove(page);
            foreach (var change in changes)
            {
                string key = Normalize(change.Path);
                // Deleting/replacing a directory must also forget its cached descendants.
                if (change.Object == null || !change.Object.Header.IsDir)
                    foreach (var item in _directories.Values.Where(d => d.Path == key ||
                        key.Length == 0 || d.Path.StartsWith(key + "/", StringComparison.Ordinal)).ToArray()) Remove(item);
                else StoreObject(key, change.Object);
            }
        }
    }
    public Stream WrapStream(string path, string version, Stream stream, bool writable)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string normalized = VfsPath.Normalize(path).ToUpperInvariant();
            string key = writable ? Guid.NewGuid().ToString("N") : normalized + "|" + version;
            return new BlockStream(this, new Session(key, normalized, stream, writable));
        }
    }
    private void WriteBack(Page page)
    {
        if (!page.Dirty) return;
        var session = page.Owner!;
        int count = (int)Math.Min(BlockSize, session.Length - page.Offset);
        if (count > 0) { session.Backing.Position = page.Offset; session.Backing.Write(page.Bytes, 0, count); }
        page.Dirty = false; _writeBacks++;
    }
    private void Remove(Page page)
    {
        WriteBack(page); // Failed writes retain the dirty page and propagate to the caller.
        _pages.Remove(page.Key); _lru.Remove(page.Node);
    }
    private void Remove(DirectorySnapshot item)
    {
        _directories.Remove(item.Path); _lru.Remove(item.Node); _directoryBytes -= item.Bytes;
    }
    private void RemoveOldest()
    {
        if (_lru.Last!.Value is Page page) Remove(page);
        else Remove((DirectorySnapshot)_lru.Last.Value);
    }
    private Page GetPage(Session session, long offset, bool overwrite = false)
    {
        string key = session.Key + "|" + offset;
        if (_pages.TryGetValue(key, out var page))
        {
            _hits++; _lru.Remove(page.Node); _lru.AddFirst(page.Node); return page;
        }
        _misses++;
        while (MemoryBytes + BlockSize > Capacity) RemoveOldest();
        var bytes = new byte[BlockSize];
        if (!overwrite && offset < session.Backing.Length)
        {
            session.Backing.Position = offset;
            int count = (int)Math.Min(BlockSize, session.Backing.Length - offset), read = 0;
            while (read < count)
            {
                int n = session.Backing.Read(bytes, read, count - read);
                if (n == 0) throw new EndOfStreamException("Backing stream changed during block read");
                read += n;
            }
        }
        page = new Page(key, session.Path, offset, bytes, session);
        page.Node = _lru.AddFirst(page); _pages.Add(key, page); return page;
    }
    private void Flush(Session session)
    {
        // Clean read pages have no session owner and nothing to write back.
        // Closing each cached file must not scan all resident blocks.
        if (!session.Writable) return;
        foreach (var page in _pages.Values.Where(p => ReferenceEquals(p.Owner, session))) WriteBack(page);
        if (session.Writable && session.Backing.Length != session.Length) session.Backing.SetLength(session.Length);
        session.Backing.Flush();
    }
    public void Invalidate()
    {
        lock (_gate)
        {
            foreach (var page in _pages.Values.Where(p => p.Owner == null).ToArray()) Remove(page);
            foreach (var item in _directories.Values.ToArray()) Remove(item);
        }
    }
    public override void Clear()
    {
        lock (_gate)
        {
            foreach (var page in _pages.Values.ToArray()) Remove(page);
            foreach (var item in _directories.Values.ToArray()) Remove(item);
        }
    }
    public void ReleaseObject(string path)
    {
        string normalized = VfsPath.Normalize(path).ToUpperInvariant();
        lock (_gate)
        {
            foreach (var page in _pages.Values.Where(p => p.Path == normalized).ToArray()) Remove(page);
            if (_directories.TryGetValue(normalized, out var item)) Remove(item);
        }
    }
    public LayerStorageUsage GetStorageUsage()
    {
        lock (_gate) return new(0, 0, _pages.Values.Select(p => p.Path).Distinct().Count(), _directories.Count,
            CacheBytes: MemoryBytes, CacheCapacity: Capacity, MemoryBytes: MemoryBytes);
    }
    public void Dispose() { lock (_gate) { if (_disposed) return; Clear(); _disposed = true; } }

    private sealed class BlockStream(MemoryCacheLayer cache, Session session) : Stream
    {
        private long _position;
        private bool _closed;
        public override bool CanRead => !_closed;
        public override bool CanSeek => !_closed;
        public override bool CanWrite => !_closed && session.Writable;
        private void Check() { ObjectDisposedException.ThrowIf(_closed || cache._disposed, this); }
        public override long Length { get { lock (cache._gate) { Check(); return session.Length; } } }
        public override long Position { get => _position; set { Check(); ArgumentOutOfRangeException.ThrowIfNegative(value); _position = value; } }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer); ArgumentOutOfRangeException.ThrowIfNegative(offset); ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (offset > buffer.Length - count) throw new ArgumentException("Invalid buffer range");
            return Read(buffer.AsSpan(offset, count));
        }
        public override int Read(Span<byte> buffer)
        {
            lock (cache._gate)
            {
                Check(); int total = (int)Math.Min(buffer.Length, Math.Max(0, session.Length - _position)), done = 0;
                while (done < total)
                {
                    long start = _position / cache.BlockSize * cache.BlockSize;
                    var page = cache.GetPage(session, start);
                    int within = (int)(_position - start), n = Math.Min(total - done, cache.BlockSize - within);
                    page.Bytes.AsSpan(within, n).CopyTo(buffer.Slice(done, n)); _position += n; done += n;
                }
                return done;
            }
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer); ArgumentOutOfRangeException.ThrowIfNegative(offset); ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (offset > buffer.Length - count) throw new ArgumentException("Invalid buffer range");
            lock (cache._gate)
            {
                Check(); if (!session.Writable) throw new NotSupportedException("Read-only stream");
                _ = checked(_position + count);
                int done = 0;
                while (done < count)
                {
                    long start = _position / cache.BlockSize * cache.BlockSize;
                    int within = (int)(_position - start), n = Math.Min(count - done, cache.BlockSize - within);
                    var page = cache.GetPage(session, start, within == 0 && n == cache.BlockSize);
                    Array.Copy(buffer, offset + done, page.Bytes, within, n); page.Dirty = true;
                    _position += n; done += n; session.Length = Math.Max(session.Length, _position);
                }
            }
        }
        public override void SetLength(long value)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            lock (cache._gate)
            {
                Check(); if (!session.Writable) throw new NotSupportedException("Read-only stream");
                if (value == session.Length) return;
                cache.Flush(session);
                foreach (var page in cache._pages.Values.Where(p => ReferenceEquals(p.Owner, session)).ToArray()) cache.Remove(page);
                session.Backing.SetLength(value); session.Length = value;
            }
        }
        public override long Seek(long offset, SeekOrigin origin)
        { Position = checked(offset + (origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => Position, SeekOrigin.End => Length, _ => throw new ArgumentException("Invalid seek origin") })); return Position; }
        public override void Flush() { lock (cache._gate) { Check(); cache.Flush(session); } }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                lock (cache._gate)
                {
                    cache.Flush(session);
                    if (session.Writable)
                        foreach (var page in cache._pages.Values.Where(p => ReferenceEquals(p.Owner, session)).ToArray()) cache.Remove(page);
                    session.Backing.Dispose(); _closed = true;
                }
            }
            base.Dispose(disposing);
        }
    }
}
