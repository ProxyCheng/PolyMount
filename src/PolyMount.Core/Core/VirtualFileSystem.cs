using PolyMount.Core.Ipc;

namespace PolyMount.Core;

/// <summary>Ordered object resolver. Directories are complete, authoritative namespace values.</summary>
public sealed class VirtualFileSystem
{
    private readonly List<ILayer> _layers;
    // Only mutations coordinate parent snapshot updates. Provider reads never
    // take this lock, including network-backed metadata and directory queries.
    private readonly object _mutationGate = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SharedObjectFile> _openFiles = new(StringComparer.OrdinalIgnoreCase);
    public VirtualFileSystem(IEnumerable<ILayer> layers) => _layers = layers.ToList();
    public IReadOnlyList<ILayer> Layers => _layers;
    public bool Writable => _layers.Any(l => l.Writable);
    public MetricsCollector? Metrics { get; set; }
    public RequestDiagnostics? Diagnostics { get; set; }
    public string Generation => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(string.Join("|", _layers.Select(l => l.Name + ":" + l.Generation)))));

    private bool Resolve(string path, out VirtualObject value, out int index, bool fill = true)
    {
        for (index = 0; index < _layers.Count; index++)
        {
            using var request = RequestDiagnostics.Begin("Layer.ReadObject:" + _layers[index].Name, path);
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
    // Attribute queries must not read a directory's children. Remote providers can
    // obtain the header from its parent's already resolved listing.
    private bool ResolveHeader(string path, out VfsEntry entry, out int index)
    {
        for (index = 0; index < _layers.Count; index++)
        {
            using var request = RequestDiagnostics.Begin("Layer.GetEntry:" + _layers[index].Name, path);
            if (_layers[index].TryGetEntry(path, out entry!)) return true;
        }
        entry = null!;
        if (path.Length == 0) { entry = new VfsEntry { Name = "", IsDir = true }; return true; }
        return false;
    }
    public bool TryLookup(string path, out VfsEntry entry)
    {
        using var request = RequestDiagnostics.Begin("VFS.Lookup", path, Diagnostics);
        path = VfsPath.Normalize(path);
        if (_openFiles.TryGetValue(path, out var open))
        {
            entry = new VfsEntry { Name = VfsPath.Name(path), Size = open.Length, TimeUtc = open.LastWriteTimeUtc };
            return true;
        }
        request?.Stage("resolving metadata");
        entry = null!;
        return Visible(path) && ResolveHeader(path, out entry, out _);
    }
    public bool IsDeleted(string path) => !Visible(VfsPath.Normalize(path));
    public List<VfsEntry> List(string path, bool recordAccess = true)
    {
        using var request = RequestDiagnostics.Begin("VFS.List", path, Diagnostics);
        request?.Stage("resolving directory");
        path = VfsPath.Normalize(path);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (!Visible(path) || !Resolve(path, out var value, out var index) || !value.Header.IsDir) return new();
        // Provider objects are immutable directory snapshots. Copy before merging
        // upper child headers so unrelated reads and mutations can proceed.
        var items = value.Children!.Select(child => ResolveHeader(VfsPath.Child(path, child.Name), out var header, out _)
            ? header : child).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (recordAccess && index < _layers.Count) Metrics?.RecordRead(_layers[index].Name, watch.Elapsed, 0, _layers[index] is ICacheLayer);
        return items;
    }
    public Stream OpenRead(string path, out VfsEntry entry)
    {
            using var request = RequestDiagnostics.Begin("VFS.OpenRead", path, Diagnostics);
            path = VfsPath.Normalize(path);
            if (_openFiles.TryGetValue(path, out var open) && open.TryAcquire(false, out var handle))
            {
                entry = new VfsEntry { Name = VfsPath.Name(path), Size = handle.Length, TimeUtc = open.LastWriteTimeUtc };
                Metrics?.RecordRead(open.LayerName, TimeSpan.Zero, entry.Size);
                return handle;
            }
            if (!Visible(path)) throw new VfsNotFoundException(path);
            if (ResolveHeader(path, out var visibleHeader, out _) && visibleHeader.IsDir) throw new IOException("Path is a directory");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < _layers.Count; i++)
            {
                var layer = _layers[i];
                request?.Stage("opening content: " + layer.Name);
                if (!layer.TryOpenRead(path, out entry!, out var stream)) continue;
                Metrics?.RecordRead(layer.Name, watch.Elapsed, entry.Size, layer is ICacheLayer);
                try { Fill(path, new(entry), stream, i); return stream; }
                catch { stream.Dispose(); throw; }
            }
            throw new VfsNotFoundException(path);
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
        return List(parent, recordAccess: false);
    }
    private static void ReplaceChild(List<VfsEntry> children, VfsEntry child)
    {
        children.RemoveAll(c => string.Equals(c.Name, child.Name, StringComparison.OrdinalIgnoreCase));
        children.Add(child);
    }
    private ILayer Apply(IReadOnlyList<ObjectChange> changes, ILayer? selected = null)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
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
            Metrics?.RecordWrite(layer.Name, watch.Elapsed, 0);
            InvalidateCaches();
            return layer;
        }
        throw new UnauthorizedAccessException("No provider accepts this write operation");
    }
    public Stream OpenWrite(string path, bool truncate, out VfsEntry entry)
    {
        lock (_mutationGate)
        {
            path = VfsPath.Normalize(path);
            if (path.Length == 0) throw new IOException("Cannot write the root as a file");
            if (_openFiles.TryGetValue(path, out var shared) && shared.TryAcquire(true, out var sharedHandle))
            {
                if (truncate) sharedHandle.SetLength(0);
                entry = new VfsEntry { Name = VfsPath.Name(path), Size = sharedHandle.Length, TimeUtc = shared.LastWriteTimeUtc };
                return sharedHandle;
            }
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
                var file = new SharedObjectFile(scratch, target.Name, bytes => Metrics?.RecordWrite(target.Name, TimeSpan.Zero, bytes), snapshot =>
                {
                    lock (_mutationGate)
                    {
                        var current = new VfsEntry { Name = VfsPath.Name(path), Size = snapshot.Length, TimeUtc = DateTime.UtcNow };
                        // Parent changes are merged at each commit; concurrent siblings are retained.
                        var list = ParentChildren(path);
                        ReplaceChild(list, current);
                        Apply(new ObjectChange[] { new(path, new(current), snapshot), new(VfsPath.Parent(path), VirtualObject.Directory(VfsPath.Parent(path), list)) }, target);
                    }
                }, () => _openFiles.TryRemove(path, out _));
                _openFiles[path] = file;
                file.TryAcquire(true, out var firstHandle);
                return firstHandle;
            }
            catch { scratch.Dispose(); throw; }
        }
    }
    public void CreateDirectory(string path)
    {
        lock (_mutationGate)
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
        lock (_mutationGate)
        {
            path = VfsPath.Normalize(path);
            if (path.Length == 0) throw new UnauthorizedAccessException("Cannot delete the root");
            foreach (var key in _openFiles.Keys.Where(p => p.Equals(path, StringComparison.OrdinalIgnoreCase) || p.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
                if (_openFiles.TryRemove(key, out var opened)) opened.Detach();
            if (!TryLookup(path, out _)) throw new VfsNotFoundException(path);
            var children = ParentChildren(path);
            children.RemoveAll(c => string.Equals(c.Name, VfsPath.Name(path), StringComparison.OrdinalIgnoreCase));
            Apply(new ObjectChange[] { new(path, null), new(VfsPath.Parent(path), VirtualObject.Directory(VfsPath.Parent(path), children)) });
        }
    }
    public void Rename(string path, string destination, bool replace)
    {
        lock (_mutationGate)
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
                foreach (var key in _openFiles.Keys.Where(p => p.Equals(path, StringComparison.OrdinalIgnoreCase) || p.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
                    if (_openFiles.TryRemove(key, out var opened)) opened.Detach();
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
        using var request = RequestDiagnostics.Begin("VFS.Probe", path, Diagnostics);
        request?.Stage("resolving origins");
        path = VfsPath.Normalize(path);
        // Source inspection must not populate caches or change the origin it observes.
        bool visible = Visible(path, false);
        ResolveHeader(path, out _, out int effective);
        return _layers.Select((layer, index) =>
        {
            using var layerRequest = RequestDiagnostics.Begin("Layer.Probe:" + layer.Name, path);
            bool exists = layer.TryGetEntry(path, out var header);
            return new LayerProbe { Name = layer.Name, Writable = layer.Writable, Exists = exists, IsDir = exists && header.IsDir,
                Size = exists ? header.Size : 0, Effective = visible && exists && index == effective,
                Shadowed = exists && (!visible || index != effective), Generation = layer.Generation };
        }).ToList();
    }
}

/// <summary>Seekable staging handle; flush persists the complete file object and parent metadata.</summary>
internal sealed class SharedObjectFile(Stream data, string layerName, Action<long> recordWrite, Action<Stream> commit, Action closed)
{
    public string LayerName => layerName;
    private readonly object _gate = new();
    private readonly object _commitGate = new();
    private int _references;
    private bool _closed;
    private bool _detached;
    private DateTime _lastWriteTimeUtc = DateTime.UtcNow;
    public DateTime LastWriteTimeUtc { get { lock (_gate) return _lastWriteTimeUtc; } }
    public void Detach() { lock (_gate) _detached = true; }
    private long _version, _persisted;
    public bool TryAcquire(bool writable, out Stream handle)
    {
        lock (_gate) { handle = null!; if (_closed) return false; _references++; handle = new SharedHandle(this, writable); return true; }
    }
    public long Length { get { lock (_gate) return data.Length; } }
    public int Read(long position, byte[] buffer, int offset, int count)
    { lock (_gate) { data.Position = position; return data.Read(buffer, offset, count); } }
    public void Write(long position, byte[] buffer, int offset, int count)
    { lock (_gate) { data.Position = position; data.Write(buffer, offset, count); _version++; _lastWriteTimeUtc = DateTime.UtcNow; } recordWrite(count); }
    public void SetLength(long length) { lock (_gate) { data.SetLength(length); _version++; _lastWriteTimeUtc = DateTime.UtcNow; } recordWrite(0); }
    public void Flush()
    {
        lock (_commitGate)
        {
            long version;
            using var snapshot = PolyMount.Layers.ObjectDatabase.Scratch();
            lock (_gate)
            {
                version = _version;
                if (version == _persisted || _detached) return;
                // Commit reads the same seekable staging file; writes are held only for
                // this file, so unrelated file data operations remain parallel.
                data.Flush();
                long position = data.Position;
                try { data.Position = 0; data.CopyTo(snapshot); }
                finally { data.Position = position; }
            }
            snapshot.Position = 0;
            commit(snapshot);
            lock (_gate) _persisted = version;
        }
    }
    public void Release()
    {
        try { Flush(); }
        finally
        {
            bool final;
            lock (_gate)
            {
                final = --_references == 0;
                if (final) _closed = true;
            }
            if (final)
            {
                try { Flush(); }
                finally { data.Dispose(); closed(); }
            }
        }
    }
}

internal sealed class SharedHandle(SharedObjectFile file, bool writable) : Stream
{
    private long _position;
    private bool _disposed;
    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => !_disposed && writable;
    public override long Length => file.Length;
    public override long Position { get => _position; set { ArgumentOutOfRangeException.ThrowIfNegative(value); _position = value; } }
    public override int Read(byte[] buffer, int offset, int count) { int read = file.Read(_position, buffer, offset, count); _position += read; return read; }
    public override long Seek(long offset, SeekOrigin origin)
    { Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, SeekOrigin.End => Length + offset, _ => throw new ArgumentException("Invalid seek origin") }; return _position; }
    public override void Write(byte[] buffer, int offset, int count)
    { if (!CanWrite) throw new UnauthorizedAccessException(); file.Write(_position, buffer, offset, count); _position += count; }
    public override void SetLength(long value) { if (!CanWrite) throw new UnauthorizedAccessException(); file.SetLength(value); }
    public override void Flush() => file.Flush();
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing) { file.Release(); _disposed = true; }
        base.Dispose(disposing);
    }
}
