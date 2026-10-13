using PolyMount.Core;
using System.IO.Enumeration;

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

    public bool TryApply(IReadOnlyList<ObjectChange> changes)
    {
        if (!_writable) return false;
        // This provider writes the physical tree. Directory objects remove absent children.
        foreach (var change in changes.Where(c => c.Object != null))
        {
            var value = change.Object!;
            if (value.Header.IsDir) Directory.CreateDirectory(LocalPath(change.Path));
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LocalPath(change.Path))!);
                using var file = File.Create(LocalPath(change.Path));
                change.Content!.CopyTo(file);
            }
        }
        foreach (var change in changes.Where(c => c.Object?.Header.IsDir == true))
        {
            var members = change.Object!.Children!.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in Directory.EnumerateFileSystemEntries(LocalPath(change.Path)))
                if (!members.Contains(Path.GetFileName(entry)))
                { if (Directory.Exists(entry)) Directory.Delete(entry, true); else File.Delete(entry); }
        }
        foreach (var change in changes.Where(c => c.Object == null))
        { var path = LocalPath(change.Path); if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path); }
        return true;
    }

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
        var p = LocalPath(rel);
        if (!Directory.Exists(p)) return Array.Empty<VfsEntry>();
        // Windows enumeration already returns length/time/attributes. Use these
        // fields rather than stat every child and scan the directory twice.
        return new FileSystemEnumerable<VfsEntry>(p, (ref FileSystemEntry entry) => new VfsEntry
        {
            Name = entry.FileName.ToString(), IsDir = entry.IsDirectory,
            Size = entry.IsDirectory ? 0 : entry.Length, TimeUtc = entry.LastWriteTimeUtc.UtcDateTime
        }, new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0,
            IgnoreInaccessible = false, ReturnSpecialDirectories = false }).ToArray();
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
