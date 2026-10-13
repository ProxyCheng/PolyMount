using PolyMount.Core;

namespace PolyMount.Layers;

/// <summary>
/// A layer that maps straight onto an existing local directory. Unlike <see cref="OverlayLayer"/>
/// it keeps no tombstones: it simply reflects whatever is on disk. It can act either as a
/// read-only data source (the default, writes land in the overlay above it) or as a writable
/// pass-through layer when <c>writable</c> is set.
/// </summary>
public sealed class DirectoryLayer : ILayer, ILocalSource
{
    private readonly string _root;
    private readonly bool _writable;

    public DirectoryLayer(string root, bool writable = false)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("directory layer requires a non-empty path", nameof(root));

        _root = Path.GetFullPath(root);
        _writable = writable;
        Directory.CreateDirectory(_root);
    }

    public string Name => "dir";
    public bool Writable => _writable;

    // The listing/content changes whenever the backing tree is touched; expose the root's
    // last-write time so any cache keyed on Generation is at least invalidated on top-level edits.
    public string Generation
    {
        get
        {
            try { return Directory.GetLastWriteTimeUtc(_root).Ticks.ToString(); }
            catch { return "0"; }
        }
    }

    public string Root => _root;

    public string LocalPath(string rel) => Path.Combine(_root, rel.Replace('/', '\\'));

    public bool IsDeleted(string rel) => false;

    public bool TryGetEntry(string rel, out VfsEntry entry)
    {
        entry = null!;
        if (rel.Length == 0)
        {
            if (!Directory.Exists(_root)) return false;
            entry = new VfsEntry { Name = string.Empty, IsDir = true, TimeUtc = Directory.GetLastWriteTimeUtc(_root) };
            return true;
        }

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
        EnsureWritable();
        var p = LocalPath(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var stream = new FileStream(p, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        entry = new VfsEntry { Name = VfsPath.Name(rel), IsDir = false, TimeUtc = DateTime.UtcNow };
        return stream;
    }

    public Stream OpenExistingWrite(string rel, out VfsEntry entry)
    {
        EnsureWritable();
        var p = LocalPath(rel);
        var stream = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        entry = new VfsEntry { Name = VfsPath.Name(rel), IsDir = false, Size = stream.Length, TimeUtc = File.GetLastWriteTimeUtc(p) };
        return stream;
    }

    public void CreateDirectory(string rel)
    {
        EnsureWritable();
        Directory.CreateDirectory(LocalPath(rel));
    }

    public void Delete(string rel, bool shadowedBelow)
    {
        EnsureWritable();
        var p = LocalPath(rel);
        if (Directory.Exists(p)) Directory.Delete(p, true);
        else if (File.Exists(p)) File.Delete(p);
    }

    public void Rename(string rel, string dst, bool replace, bool shadowedBelow)
    {
        EnsureWritable();
        var from = LocalPath(rel);
        var to = LocalPath(dst);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);

        if (Directory.Exists(to))
        {
            if (!replace) throw new IOException("target exists: " + dst);
            Directory.Delete(to, true);
        }
        else if (File.Exists(to))
        {
            if (!replace) throw new IOException("target exists: " + dst);
            File.Delete(to);
        }

        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to, replace);
    }

    private void EnsureWritable()
    {
        if (!_writable) throw new NotSupportedException("directory layer is read-only");
    }
}
