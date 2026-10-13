namespace PolyMount.Core;

public sealed class VirtualFileSystem
{
    private readonly List<ILayer> _layers;
    private readonly ILayer _writeTarget;

    public VirtualFileSystem(IEnumerable<ILayer> layers)
    {
        _layers = layers.ToList();
        if (_layers.Count == 0) throw new ArgumentException("at least one provider layer is required");
        _writeTarget = _layers.FirstOrDefault(l => l.Writable)
            ?? throw new ArgumentException("the chain must contain a writable layer");
    }

    public IReadOnlyList<ILayer> Layers => _layers;

    public string Generation => _layers[^1].Generation;

    public bool IsDeleted(string rel)
    {
        foreach (var l in _layers)
            if (l.IsDeleted(rel)) return true;
        return false;
    }

    public bool TryLookup(string rel, out VfsEntry entry)
    {
        entry = null!;
        foreach (var l in _layers)
        {
            if (l.IsDeleted(rel)) return false;
            if (l.TryGetEntry(rel, out var e)) { entry = e; return true; }
        }
        return false;
    }

    public List<VfsEntry> List(string rel)
    {
        if (IsDeleted(rel)) return new List<VfsEntry>();

        var map = new Dictionary<string, VfsEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in _layers)
        {
            foreach (var e in layer.ListOwn(rel))
            {
                if (IsDeleted(VfsPath.Child(rel, e.Name))) continue;
                map.TryAdd(e.Name, e);
            }
        }
        return map.Values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Stream OpenRead(string rel, out VfsEntry entry)
    {
        for (int i = 0; i < _layers.Count; i++)
        {
            var layer = _layers[i];
            if (layer.IsDeleted(rel)) break;
            if (!layer.TryOpenRead(rel, out var e, out var stream)) continue;

            for (int j = 0; j < i; j++)
            {
                if (_layers[j] is ICacheLayer cache)
                {
                    cache.Store(rel, layer.Generation, e, stream);
                    stream.Seek(0, SeekOrigin.Begin);
                }
            }

            entry = e;
            return stream;
        }
        throw new VfsNotFoundException(rel);
    }

    public Stream OpenWrite(string rel, bool truncate, out VfsEntry entry)
    {
        if (!_writeTarget.TryGetEntry(rel, out _))
        {
            if (TryLookup(rel, out var existing))
            {
                if (existing.IsDir) throw new IOException("is a directory: " + rel);
                using var src = OpenRead(rel, out _);
                var dst = _writeTarget.Create(rel, out entry);
                src.CopyTo(dst);
                dst.Seek(0, SeekOrigin.Begin);
                if (truncate) dst.SetLength(0);
                return dst;
            }
            return _writeTarget.Create(rel, out entry);
        }

        var s = _writeTarget.OpenExistingWrite(rel, out entry);
        if (truncate) s.SetLength(0);
        return s;
    }

    public void CreateDirectory(string rel) => _writeTarget.CreateDirectory(rel);

    public void Delete(string rel) => _writeTarget.Delete(rel, ExistsBelowWriteTarget(rel));

    public void Rename(string rel, string dst, bool replace)
    {
        if (!_writeTarget.TryGetEntry(rel, out var src))
        {
            if (!TryLookup(rel, out src)) throw new VfsNotFoundException(rel);
            if (src.IsDir)
            {
                _writeTarget.CreateDirectory(rel);
            }
            else
            {
                using var s = OpenRead(rel, out _);
                using var d = _writeTarget.Create(rel, out _);
                s.CopyTo(d);
            }
        }
        _writeTarget.Rename(rel, dst, replace, ExistsBelowWriteTarget(rel));
    }

    private bool ExistsBelowWriteTarget(string rel)
    {
        bool passedTarget = false;
        foreach (var l in _layers)
        {
            if (ReferenceEquals(l, _writeTarget)) { passedTarget = true; continue; }
            if (passedTarget && l.TryGetEntry(rel, out _)) return true;
        }
        return false;
    }
}
