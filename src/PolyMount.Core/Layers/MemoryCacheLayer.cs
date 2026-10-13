using PolyMount.Core;

namespace PolyMount.Layers;

/// <summary>Bounded, block-granularity LRU. Dirty blocks belong to a live staging stream.</summary>
public sealed class MemoryCacheLayer : ICacheLayer, IFileStreamLayer, ILayerStorageUsage, IObjectManagement, IDisposable
{
    private sealed class Page(string key, string path, long offset, byte[] bytes, Session session)
    {
        public readonly string Key = key, Path = path;
        public readonly long Offset = offset;
        public readonly byte[] Bytes = bytes;
        // Clean read pages must not retain closed streams (or their whole-file buffers).
        public readonly Session? Owner = session.Writable ? session : null;
        public bool Dirty;
        public LinkedListNode<Page> Node = null!;
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
    private readonly LinkedList<Page> _lru = new();
    private readonly Func<string> _generation;
    private bool _disposed;
    private long _hits, _misses, _writeBacks;
    public MemoryCacheLayer(long capacityBytes = 16 * 1048576, int blockSize = 65536, Func<string>? generation = null)
    {
        if (blockSize < 4096 || blockSize > 1048576 || (blockSize & (blockSize - 1)) != 0 || capacityBytes < blockSize)
            throw new ArgumentException("Memory cache requires a power-of-two block size from 4 KiB to 1 MiB and capacity >= block size");
        Capacity = capacityBytes; BlockSize = blockSize; _generation = generation ?? (() => "memory");
    }
    public string Name => "memory-cache";
    public string Generation => _generation();
    // Stream writes are buffered; namespace objects always pass to the next provider.
    public bool Writable => false;
    public long Capacity { get; }
    public int BlockSize { get; }
    public long Hits { get { lock (_gate) return _hits; } }
    public long Misses { get { lock (_gate) return _misses; } }
    public long WriteBacks { get { lock (_gate) return _writeBacks; } }
    public long MemoryBytes { get { lock (_gate) return (long)_pages.Count * BlockSize; } }
    public bool TryReadObject(string path, out VirtualObject value) { value = null!; return false; }
    public bool TryApply(IReadOnlyList<ObjectChange> changes) => false;
    public bool TryGetEntry(string path, out VfsEntry entry) { entry = null!; return false; }
    public IReadOnlyList<VfsEntry> ListOwn(string path) => Array.Empty<VfsEntry>();
    public bool TryOpenRead(string path, out VfsEntry entry, out Stream stream) { entry = null!; stream = null!; return false; }
    public void Store(string path, string version, VfsEntry entry, Stream content) { }
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
    private Page GetPage(Session session, long offset, bool overwrite = false)
    {
        string key = session.Key + "|" + offset;
        if (_pages.TryGetValue(key, out var page))
        {
            _hits++; _lru.Remove(page.Node); _lru.AddFirst(page.Node); return page;
        }
        _misses++;
        while ((long)(_pages.Count + 1) * BlockSize > Capacity) Remove(_lru.Last!.Value);
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
        foreach (var page in _pages.Values.Where(p => ReferenceEquals(p.Owner, session))) WriteBack(page);
        if (session.Writable && session.Backing.Length != session.Length) session.Backing.SetLength(session.Length);
        session.Backing.Flush();
    }
    public void Invalidate()
    {
        lock (_gate) foreach (var page in _pages.Values.Where(p => p.Owner == null).ToArray()) Remove(page);
    }
    public void Clear()
    {
        lock (_gate) foreach (var page in _pages.Values.ToArray()) Remove(page);
    }
    public void ReleaseObject(string path)
    {
        string normalized = VfsPath.Normalize(path).ToUpperInvariant();
        lock (_gate) foreach (var page in _pages.Values.Where(p => p.Path == normalized).ToArray()) Remove(page);
    }
    public LayerStorageUsage GetStorageUsage()
    {
        lock (_gate) return new(0, 0, _pages.Values.Select(p => p.Path).Distinct().Count(), 0,
            CacheBytes: (long)_pages.Count * BlockSize, CacheCapacity: Capacity, MemoryBytes: (long)_pages.Count * BlockSize);
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
            lock (cache._gate)
            {
                Check(); int total = (int)Math.Min(count, Math.Max(0, session.Length - _position)), done = 0;
                while (done < total)
                {
                    long start = _position / cache.BlockSize * cache.BlockSize;
                    var page = cache.GetPage(session, start);
                    int within = (int)(_position - start), n = Math.Min(total - done, cache.BlockSize - within);
                    Array.Copy(page.Bytes, within, buffer, offset + done, n); _position += n; done += n;
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
