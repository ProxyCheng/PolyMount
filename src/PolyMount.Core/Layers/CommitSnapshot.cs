namespace PolyMount.Layers;

/// <summary>Single-use commit snapshot whose flushed file can become an immutable blob.</summary>
internal sealed class CommitSnapshot : Stream
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "PolyMount-commit-" + Guid.NewGuid().ToString("N"));
    private readonly FileStream _file;
    private bool _transferred;
    public CommitSnapshot() => _file = new(_path, FileMode.CreateNew, FileAccess.ReadWrite,
        FileShare.Read | FileShare.Delete, 131072);
    public void PublishTo(string destination)
    {
        // The catalog commit fsyncs this blob along with the rest of its batch; flushing
        // here would put a device round trip back on the writer's close path.
        _file.Flush();
        if (!string.Equals(Path.GetPathRoot(_path), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
        {
            // A different store volume cannot adopt the temporary file by rename.
            _file.Position = 0;
            using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
            _file.CopyTo(target); target.Flush();
            return;
        }
        _file.Dispose();
        File.Move(_path, destination);
        _transferred = true;
    }
    public override bool CanRead => _file.CanRead;
    public override bool CanWrite => _file.CanWrite;
    public override bool CanSeek => _file.CanSeek;
    public override long Length => _file.Length;
    public override long Position { get => _file.Position; set => _file.Position = value; }
    public override void Flush() => _file.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _file.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _file.Seek(offset, origin);
    public override void SetLength(long value) => _file.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _file.Write(buffer, offset, count);
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _file.Dispose();
            if (!_transferred) { try { File.Delete(_path); } catch (IOException) { } }
        }
        base.Dispose(disposing);
    }
}
