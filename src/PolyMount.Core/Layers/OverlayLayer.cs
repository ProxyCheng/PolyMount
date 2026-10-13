using PolyMount.Core;

namespace PolyMount.Layers;

public sealed class OverlayLayer : ILayer
{
    private readonly string _root;
    private readonly string _tombstonePath;
    private readonly HashSet<string> _tombstones = new(StringComparer.OrdinalIgnoreCase);

    public OverlayLayer(string root, string tombstonePath)
    {
        _root = root;
        _tombstonePath = tombstonePath;
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(_tombstonePath)!);
        LoadTombstones();
    }

    public string Name => "overlay";
    public string Generation => "local";
    public bool Writable => true;

    public string LocalPath(string rel) => Path.Combine(_root, rel.Replace('/', '\\'));

    public bool IsDeleted(string rel)
    {
        var cur = rel;
        while (cur.Length > 0)
        {
            if (_tombstones.Contains(cur)) return true;
            cur = VfsPath.Parent(cur);
        }
        return false;
    }

    public bool TryGetEntry(string rel, out VfsEntry entry)
    {
        entry = null!;
        var p = LocalPath(rel);
        if (Directory.Exists(p))
        {
            entry = new VfsEntry { Name = VfsPath.Name(rel), IsDir = true, TimeUtc = Directory.GetLastWriteTimeUtc(p) };
            return true;
        }
        if (File.Exists(p))
        {
            var fi = new FileInfo(p);
            entry = new VfsEntry { Name = fi.Name, IsDir = false, Size = fi.Length, TimeUtc = fi.LastWriteTimeUtc };
            return true;
        }
        return false;
    }

    public IReadOnlyList<VfsEntry> ListOwn(string rel)
    {
        var list = new List<VfsEntry>();
        var p = LocalPath(rel);
        if (!Directory.Exists(p)) return list;

        foreach (var d in Directory.EnumerateDirectories(p))
            list.Add(new VfsEntry { Name = Path.GetFileName(d), IsDir = true, TimeUtc = Directory.GetLastWriteTimeUtc(d) });
        foreach (var f in Directory.EnumerateFiles(p))
        {
            var fi = new FileInfo(f);
            list.Add(new VfsEntry { Name = fi.Name, IsDir = false, Size = fi.Length, TimeUtc = fi.LastWriteTimeUtc });
        }
        return list;
    }

    public bool TryOpenRead(string rel, out VfsEntry entry, out Stream stream)
    {
        stream = null!;
        if (!TryGetEntry(rel, out entry) || entry.IsDir) return false;
        stream = new FileStream(LocalPath(rel), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return true;
    }

    public Stream Create(string rel, out VfsEntry entry)
    {
        var p = LocalPath(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        RemoveTombstone(rel);
        var stream = new FileStream(p, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        entry = new VfsEntry { Name = VfsPath.Name(rel), IsDir = false, TimeUtc = DateTime.UtcNow };
        return stream;
    }

    public Stream OpenExistingWrite(string rel, out VfsEntry entry)
    {
        var p = LocalPath(rel);
        RemoveTombstone(rel);
        var stream = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        entry = new VfsEntry { Name = VfsPath.Name(rel), IsDir = false, Size = stream.Length, TimeUtc = File.GetLastWriteTimeUtc(p) };
        return stream;
    }

    public void CreateDirectory(string rel)
    {
        RemoveTombstone(rel);
        Directory.CreateDirectory(LocalPath(rel));
    }

    public void Delete(string rel, bool shadowedBelow)
    {
        var p = LocalPath(rel);
        if (Directory.Exists(p)) Directory.Delete(p, true);
        else if (File.Exists(p)) File.Delete(p);

        if (shadowedBelow) AddTombstone(rel);
        else RemoveTombstone(rel);
    }

    public void Rename(string rel, string dst, bool replace, bool shadowedBelow)
    {
        var from = LocalPath(rel);
        var to = LocalPath(dst);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);

        if (Directory.Exists(to))
        {
            if (!replace)             throw new IOException("target exists: " + dst);
            Directory.Delete(to, true);
        }
        else if (File.Exists(to))
        {
            if (!replace)             throw new IOException("target exists: " + dst);
            File.Delete(to);
        }

        RemoveTombstone(dst);
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to, replace);

        if (shadowedBelow) AddTombstone(rel);
        else RemoveTombstone(rel);
    }

    // ---- tombstones ----

    // Does this layer directly tombstone this exact path (i.e. explicitly "不存在", as opposed to
    // inheriting a tombstone from an ancestor directory)?
    public bool HasTombstone(string rel) => _tombstones.Contains(rel);

    // 不存在 -> 不知道 : stop hiding the lower layers for this path.
    public void ClearTombstone(string rel) => RemoveTombstone(rel);

    // -> 不存在 : explicitly hide this path (and anything below it).
    public void MarkTombstone(string rel) => AddTombstone(rel);

    private void LoadTombstones()    {
        try
        {
            if (!File.Exists(_tombstonePath)) return;
            foreach (var line in File.ReadAllLines(_tombstonePath))
                if (line.Trim().Length > 0) _tombstones.Add(line.Trim());
        }
        catch { }
    }

    private void SaveTombstones()
    {
        try
        {
            File.WriteAllLines(_tombstonePath,
                _tombstones.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        }
        catch { }
    }

    private void AddTombstone(string rel)
    {
        if (_tombstones.Add(rel)) SaveTombstones();
    }

    private void RemoveTombstone(string rel)
    {
        if (_tombstones.Remove(rel)) SaveTombstones();
    }
}
