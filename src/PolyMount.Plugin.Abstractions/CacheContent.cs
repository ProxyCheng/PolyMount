namespace PolyMount.Plugins;

/// <summary>Independent, reference-counted access to an immutable read-fill snapshot.</summary>
public sealed class CacheContent : IDisposable
{
    private readonly FileStream? _file;
    private readonly byte[]? _bytes;
    private int _references = 1;
    private CacheContent(FileStream file) { _file = file; Length = file.Length; }
    private CacheContent(byte[] bytes) { _bytes = bytes; Length = bytes.Length; }
    public long Length { get; }
    public static CacheContent Own(FileStream file) => new(file);

    public static CacheContent? Capture(Stream stream)
    {
        // Do not copy arbitrary streams on the request thread or seek the caller's
        // handle. File-backed source reads retain a separate handle to the same file.
        if (stream is FileStream file)
            return new(new FileStream(file.Name, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 1, FileOptions.RandomAccess));
        if (stream is MemoryStream memory && memory.Length <= 8 * 1024 * 1024)
            return new(memory.ToArray());
        return null;
    }

    public Stream? Acquire()
    {
        int count;
        do
        {
            count = Volatile.Read(ref _references);
            if (count == 0) return null;
        } while (Interlocked.CompareExchange(ref _references, count + 1, count) != count);
        return new Handle(this);
    }

    public void Dispose()
    {
        if (Interlocked.Decrement(ref _references) == 0) _file?.Dispose();
    }

    private sealed class Handle(CacheContent owner) : Stream
    {
        private long _position;
        private bool _disposed;
        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override long Length => owner.Length;
        public override long Position { get => _position; set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); _position = value; } }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int count;
            if (owner._file is { } file)
                count = RandomAccess.Read(file.SafeFileHandle, buffer, _position);
            else
            {
                count = (int)Math.Min(buffer.Length, Math.Max(0, Length - _position));
                owner._bytes!.AsSpan((int)Math.Min(_position, Length), count).CopyTo(buffer);
            }
            _position += count;
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = checked((origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? Position : Length) + offset);
            return Position;
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (!_disposed) { _disposed = true; owner.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
