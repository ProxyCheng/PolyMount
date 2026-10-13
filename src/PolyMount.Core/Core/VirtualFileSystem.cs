namespace PolyMount.Core;

using PolyMount.Core.Ipc;
using PolyMount.Layers;

public sealed class VirtualFileSystem
{
    private readonly List<ILayer> _layers;
    private readonly ILayer? _writeTarget;
    private ILayer WriteTarget => _writeTarget ?? throw new UnauthorizedAccessException("This provider chain is read-only");
    public bool Writable => _writeTarget != null;

    public VirtualFileSystem(IEnumerable<ILayer> layers)
    {
        _layers = layers.ToList();
        _writeTarget = _layers.FirstOrDefault(l => l.Writable);
    }

    public IReadOnlyList<ILayer> Layers => _layers;

    // Optional read analytics sink (set by MountInstance).
    public MetricsCollector? Metrics { get; set; }

    public string Generation => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(string.Join("|", _layers.Select(l => l.Name + ":" + l.Generation)))));

    public bool IsDeleted(string rel)
    {
        foreach (var l in _layers)
        {
            if (l.IsDeleted(rel)) return true;
            if (l.TryGetEntry(rel, out _)) return false;
        }
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
        if (rel.Length == 0) { entry = new VfsEntry { Name = "", IsDir = true, TimeUtc = DateTime.UnixEpoch }; return true; }
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
        var sw = Metrics != null ? System.Diagnostics.Stopwatch.StartNew() : null;
        for (int i = 0; i < _layers.Count; i++)
        {
            var layer = _layers[i];
            if (layer.IsDeleted(rel)) break;
            if (!layer.TryOpenRead(rel, out var e, out var stream)) continue;

            if (sw != null)
            {
                sw.Stop();
                Metrics!.RecordRead(layer.Name, sw.Elapsed, e.Size, layer is ICacheLayer);
            }

            for (int j = 0; j < i; j++)
            {
                // A mutable provider can change visibility without changing its generation.
                // Such content must not be retained by a cache above it.
                if (!_layers.Skip(j + 1).Take(i - j).Any(l => l.Writable) && _layers[j] is ICacheLayer cache)
                {
                    cache.Store(rel, cache.Generation, e, stream);
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
        var target = WriteTarget;
        InvalidateCachesAbove(target);
        if (!target.TryGetEntry(rel, out _))
        {
            if (TryLookup(rel, out var existing))
            {
                if (existing.IsDir) throw new IOException("is a directory: " + rel);
                using var src = OpenRead(rel, out _);
                var dst = target.Create(rel, out entry);
                src.CopyTo(dst);
                dst.Seek(0, SeekOrigin.Begin);
                if (truncate) dst.SetLength(0);
                return dst;
            }
            return target.Create(rel, out entry);
        }

        var s = target.OpenExistingWrite(rel, out entry);
        if (truncate) s.SetLength(0);
        return s;
    }

    public void CreateDirectory(string rel) { var target = WriteTarget; InvalidateCachesAbove(target); target.CreateDirectory(rel); }

    public void Delete(string rel) { var target = WriteTarget; InvalidateCachesAbove(target); target.Delete(rel, ExistsBelowWriteTarget(rel)); }

    public void Rename(string rel, string dst, bool replace)
    {
        var target = WriteTarget;
        InvalidateCachesAbove(target);
        if (!target.TryGetEntry(rel, out var src))
        {
            if (!TryLookup(rel, out src)) throw new VfsNotFoundException(rel);
            if (src.IsDir)
            {
                target.CreateDirectory(rel);
            }
            else
            {
                using var s = OpenRead(rel, out _);
                using var d = target.Create(rel, out _);
                s.CopyTo(d);
            }
        }
        target.Rename(rel, dst, replace, ExistsBelowWriteTarget(rel));
    }

    public void InvalidateCaches()
    {
        foreach (var cache in _layers.OfType<ICacheLayer>()) cache.Invalidate();
    }
    private void InvalidateCachesAbove(ILayer target)
    {
        foreach (var cache in _layers.TakeWhile(l => !ReferenceEquals(l, target)).OfType<ICacheLayer>()) cache.Invalidate();
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

    /// <summary>
    /// Reports, for every layer in resolution order, whether <paramref name="rel"/> exists there,
    /// which layer's entry is effective, which are shadowed, and which carry a tombstone. Mirrors
    /// the resolution rules of <see cref="TryLookup"/>.
    /// </summary>
    public List<LayerProbe> Probe(string rel)
    {
        // Resolve the effective layer the same way TryLookup does: top-down, a tombstone hides
        // everything below it, otherwise the first layer with an entry wins.
        int effectiveIndex = -1;
        for (int i = 0; i < _layers.Count; i++)
        {
            if (_layers[i].IsDeleted(rel)) break;              // hidden; nothing below is effective
            if (_layers[i].TryGetEntry(rel, out _)) { effectiveIndex = i; break; }
        }

        var result = new List<LayerProbe>(_layers.Count);
        for (int i = 0; i < _layers.Count; i++)
        {
            var l = _layers[i];
            bool exists = l.TryGetEntry(rel, out var e);
            result.Add(new LayerProbe
            {
                Name = l.Name,
                Writable = l.Writable,
                Exists = exists,
                IsDir = exists && e.IsDir,
                Size = exists && !e.IsDir ? e.Size : 0,
                Effective = i == effectiveIndex,
                Shadowed = exists && i != effectiveIndex,
                Tombstoned = l.IsDeleted(rel),
                DirectTombstone = (l is NamedLayer named ? named.Inner : l) is OverlayLayer ov && ov.HasTombstone(rel),
                Generation = l.Generation
            });
        }
        return result;
    }
}
