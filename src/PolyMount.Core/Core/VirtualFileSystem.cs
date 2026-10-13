using PolyMount.Core.Ipc;

namespace PolyMount.Core;

/// <summary>Ordered object resolver. Directories are complete, authoritative namespace values.</summary>
public sealed class VirtualFileSystem
{
    private readonly List<ILayer> _layers;
    private readonly object _gate = new();
    public VirtualFileSystem(IEnumerable<ILayer> layers) => _layers = layers.ToList();
    public IReadOnlyList<ILayer> Layers => _layers;
    public bool Writable => _layers.Any(l => l.Writable);
    public MetricsCollector? Metrics { get; set; }
    public string Generation => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(string.Join("|", _layers.Select(l => l.Name + ":" + l.Generation)))));

    private bool Resolve(string path, out VirtualObject value, out int index, bool fill = true)
    {
        for (index = 0; index < _layers.Count; index++)
        {
            if (!((IObjectLayer)_layers[index]).TryReadObject(path, out value!)) continue;
            if (fill && value.Header.IsDir) Fill(path, value, null, index);
            return true;
        }
        value = null!;
        if (path.Length == 0) { value = VirtualObject.Directory("", Array.Empty<VfsEntry>()); return true; }
        return false;
    }

    private bool Visible(string path, bool fill = true)
    {
        if (path.Length == 0) return true;
        string current = "";
        foreach (string name in path.Split('/'))
        {
            if (!Resolve(current, out var directory, out _, fill) || !directory.Header.IsDir ||
                !directory.Children!.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))) return false;
            current = VfsPath.Child(current, name);
        }
        return true;
    }
    public bool TryLookup(string path, out VfsEntry entry)
    {
        lock (_gate)
        {
            path = VfsPath.Normalize(path);
            entry = null!;
            if (!Visible(path) || !Resolve(path, out var value, out _)) return false;
            entry = value.Header;
            return true;
        }
    }
    public bool IsDeleted(string path) => !Visible(VfsPath.Normalize(path));
    public List<VfsEntry> List(string path)
    {
        lock (_gate)
        {
            path = VfsPath.Normalize(path);
            if (!Visible(path) || !Resolve(path, out var value, out _) || !value.Header.IsDir) return new();
            // Resolve headers from overridden child objects without merging directory membership.
            return value.Children!.Select(child => Resolve(VfsPath.Child(path, child.Name), out var obj, out _, false)
                ? obj.Header : child).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
    public Stream OpenRead(string path, out VfsEntry entry)
    {
        lock (_gate)
        {
            path = VfsPath.Normalize(path);
            if (!Visible(path)) throw new VfsNotFoundException(path);
            if (Resolve(path, out var visibleObject, out _, false) && visibleObject.Header.IsDir) throw new IOException("Path is a directory");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < _layers.Count; i++)
            {
                var layer = _layers[i];
                if (!layer.TryOpenRead(path, out entry!, out var stream)) continue;
                Metrics?.RecordRead(layer.Name, watch.Elapsed, entry.Size, layer is ICacheLayer);
                try { Fill(path, new(entry), stream, i); return stream; }
                catch { stream.Dispose(); throw; }
            }
            throw new VfsNotFoundException(path);
        }
    }
    private void Fill(string path, VirtualObject value, Stream? content, int hit)
    {
        for (int i = hit - 1; i >= 0; i--)
        {
            if (_layers[i] is not IObjectCache cache) continue;
            // Durable writes and read-through caches expose separate capabilities.
            if (_layers.Skip(i + 1).Take(hit - i).Any(l =>
                (l is NamedLayer named ? named.Inner : l) is PolyMount.Layers.LruCacheLayer lru ? lru.HasDurableWrites : l.Writable)) continue;
            long position = content?.Position ?? 0;
            try { cache.StoreObject(path, value, content); }
            finally { if (content != null) content.Position = position; }
        }
    }
    private List<VfsEntry> ParentChildren(string path)
    {
        string parent = VfsPath.Parent(path);
        if (!TryLookup(parent, out var header) || !header.IsDir) throw new VfsNotFoundException(parent);
        return List(parent);
    }
    private static void ReplaceChild(List<VfsEntry> children, VfsEntry child)
    {
        children.RemoveAll(c => string.Equals(c.Name, child.Name, StringComparison.OrdinalIgnoreCase));
        children.Add(child);
    }
    private ILayer Apply(IReadOnlyList<ObjectChange> changes, ILayer? selected = null)
    {
        foreach (var change in changes) if (change.Content != null) change.Content.Position = 0;
        if (selected != null)
        {
            if (!((IObjectLayer)selected).TryApply(changes)) throw new IOException("The selected write handle no longer supports this operation");
            InvalidateCaches();
            return selected;
        }
        foreach (var layer in _layers)
        {
            foreach (var change in changes) if (change.Content != null) change.Content.Position = 0;
            if (!((IObjectLayer)layer).TryApply(changes)) continue;
            InvalidateCaches();
            return layer;
        }
        throw new UnauthorizedAccessException("No provider accepts this write operation");
    }
    public Stream OpenWrite(string path, bool truncate, out VfsEntry entry)
    {
        lock (_gate)
        {
            path = VfsPath.Normalize(path);
            if (path.Length == 0) throw new IOException("Cannot write the root as a file");
            var children = ParentChildren(path);
            var scratch = PolyMount.Layers.ObjectDatabase.Scratch();
            try
            {
                if (TryLookup(path, out var existing))
                {
                    if (existing.IsDir) throw new IOException("Path is a directory");
                    if (!truncate) { using var source = OpenRead(path, out _); source.CopyTo(scratch); }
                }
                entry = new VfsEntry { Name = VfsPath.Name(path), Size = scratch.Length, TimeUtc = DateTime.UtcNow };
                ReplaceChild(children, entry);
                var target = Apply(new ObjectChange[] { new(path, new(entry), scratch), new(VfsPath.Parent(path), VirtualObject.Directory(VfsPath.Parent(path), children)) });
                scratch.Position = 0;
                return new ObjectWriteStream(scratch, () =>
                {
                    lock (_gate)
                    {
                        var current = new VfsEntry { Name = VfsPath.Name(path), Size = scratch.Length, TimeUtc = DateTime.UtcNow };
                        // Parent changes are merged at each commit; concurrent siblings are retained.
                        var list = ParentChildren(path);
                        ReplaceChild(list, current);
                        long position = scratch.Position;
                        try { Apply(new ObjectChange[] { new(path, new(current), scratch), new(VfsPath.Parent(path), VirtualObject.Directory(VfsPath.Parent(path), list)) }, target); }
                        finally { scratch.Position = position; }
                    }
                });
            }
            catch { scratch.Dispose(); throw; }
        }
    }
    public void CreateDirectory(string path)
    {
        lock (_gate)
        {
            path = VfsPath.Normalize(path);
            if (TryLookup(path, out _)) throw new IOException("Path already exists");
            var children = ParentChildren(path);
            var value = VirtualObject.Directory(path, Array.Empty<VfsEntry>());
            ReplaceChild(children, value.Header);
            Apply(new ObjectChange[] { new(path, value), new(VfsPath.Parent(path), VirtualObject.Directory(VfsPath.Parent(path), children)) });
        }
    }
    public void Delete(string path)
    {
        lock (_gate)
        {
            path = VfsPath.Normalize(path);
            if (path.Length == 0) throw new UnauthorizedAccessException("Cannot delete the root");
            if (!TryLookup(path, out _)) throw new VfsNotFoundException(path);
            var children = ParentChildren(path);
            children.RemoveAll(c => string.Equals(c.Name, VfsPath.Name(path), StringComparison.OrdinalIgnoreCase));
            Apply(new ObjectChange[] { new(path, null), new(VfsPath.Parent(path), VirtualObject.Directory(VfsPath.Parent(path), children)) });
        }
    }
    public void Rename(string path, string destination, bool replace)
    {
        lock (_gate)
        {
            path = VfsPath.Normalize(path); destination = VfsPath.Normalize(destination);
            if (path.Length == 0 || destination.Length == 0 || destination.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Invalid rename destination");
            if (string.Equals(path, destination, StringComparison.Ordinal)) return;
            if (!TryLookup(path, out _)) throw new VfsNotFoundException(path);
            if (!replace && !string.Equals(path, destination, StringComparison.OrdinalIgnoreCase) && TryLookup(destination, out _)) throw new IOException("Destination exists");
            var changes = new List<ObjectChange>();
            var streams = new List<Stream>();
            try
            {
                CopyTree(path, destination, changes, streams);
                string fromParent = VfsPath.Parent(path), toParent = VfsPath.Parent(destination);
                var from = ParentChildren(path);
                var to = string.Equals(fromParent, toParent, StringComparison.OrdinalIgnoreCase) ? from : ParentChildren(destination);
                from.RemoveAll(c => string.Equals(c.Name, VfsPath.Name(path), StringComparison.OrdinalIgnoreCase));
                ReplaceChild(to, changes[0].Object!.Header);
                // Case-only rename is the same KV key; removing it would delete the new value.
                if (!string.Equals(path, destination, StringComparison.OrdinalIgnoreCase)) changes.Add(new(path, null));
                changes.Add(new(fromParent, VirtualObject.Directory(fromParent, from)));
                if (!ReferenceEquals(from, to)) changes.Add(new(toParent, VirtualObject.Directory(toParent, to)));
                Apply(changes);
            }
            finally { foreach (var stream in streams) stream.Dispose(); }
        }
    }
    private void CopyTree(string source, string destination, List<ObjectChange> changes, List<Stream> streams)
    {
        if (!TryLookup(source, out var header)) throw new VfsNotFoundException(source);
        var renamed = new VfsEntry { Name = VfsPath.Name(destination), IsDir = header.IsDir, Size = header.Size, TimeUtc = header.TimeUtc };
        if (!header.IsDir)
        {
            var stream = OpenRead(source, out _); streams.Add(stream); changes.Add(new(destination, new(renamed), stream)); return;
        }
        var children = List(source);
        changes.Add(new(destination, new(renamed, children)));
        foreach (var child in children) CopyTree(VfsPath.Child(source, child.Name), VfsPath.Child(destination, child.Name), changes, streams);
    }
    public void InvalidateCaches() { foreach (var cache in _layers.OfType<ICacheLayer>()) cache.Invalidate(); }
    public List<LayerProbe> Probe(string path)
    {
        lock (_gate)
        {
            path = VfsPath.Normalize(path);
            // Source inspection must not populate caches or change the origin it observes.
            bool visible = Visible(path, false);
            Resolve(path, out _, out int effective, false);
            return _layers.Select((layer, index) =>
            {
                bool exists = ((IObjectLayer)layer).TryReadObject(path, out var value);
                return new LayerProbe { Name = layer.Name, Writable = layer.Writable, Exists = exists, IsDir = exists && value.Header.IsDir,
                    Size = exists ? value.Header.Size : 0, Effective = visible && exists && index == effective,
                    Shadowed = exists && (!visible || index != effective), Generation = layer.Generation };
            }).ToList();
        }
    }
}

/// <summary>Seekable staging handle; flush persists the complete file object and parent metadata.</summary>
internal sealed class ObjectWriteStream(Stream inner, Action commit) : Stream
{
    private bool _dirty, _disposed;
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); _dirty = true; Flush(); }
    public override void SetLength(long value) { inner.SetLength(value); _dirty = true; Flush(); }
    public override void Flush() { if (_dirty) { inner.Flush(); commit(); _dirty = false; } }
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing) { try { Flush(); } finally { inner.Dispose(); _disposed = true; } }
        base.Dispose(disposing);
    }
}
