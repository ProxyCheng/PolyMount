using PolyMount.Core;
using System.IO.Enumeration;

namespace PolyMount.Layers;

/// <summary>
/// A layer that maps straight onto an existing local directory. It records nothing of its own:
/// it simply reflects whatever is on disk. It can act either as a read-only data source (the
/// default, writes land in the overlay above it) or as a writable pass-through layer when
/// <c>writable</c> is set.
/// </summary>
public sealed class DirectoryLayer : ILayer, ILocalSource, IDisposable
{
    private readonly string _root;
    private readonly bool _writable;
    private readonly FileSystemWatcher? _watcher;
    private long _touched;

    public DirectoryLayer(string root, bool writable = false, bool watch = false)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("directory layer requires a non-empty path", nameof(root));

        _root = Path.GetFullPath(root);
        _writable = writable;
        // The root is deliberately not created here. A source that points inside another
        // instance's mount point (a mounting-inside-a-mount layout) would otherwise materialise a
        // real directory at that path whenever this worker starts first, and the mount it lives
        // under then refuses to mount because its point is no longer empty. Writes create it.
        _watcher = watch ? Watch() : null;
    }

    /// <summary>Watches the backing tree, so a change made by someone else — the engine writing
    /// through another instance's mount, a colleague on a share — invalidates what the caches
    /// above this layer hold. The counter is part of <see cref="Generation"/>, which is the key
    /// those caches index on; one event is enough, so the handler does no work of its own.
    /// Requires the source to raise change notifications: a PolyMount mount only does so from the
    /// version that forwards resolver changes to WinFsp.</summary>
    private FileSystemWatcher? Watch()
    {
        if (!Directory.Exists(_root)) return null;
        try
        {
            var watcher = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes,
                // A deep tree under a slow share overflows a small buffer; an overflow is
                // reported as an error, which is also just "assume everything moved".
                InternalBufferSize = 64 * 1024
            };
            FileSystemEventHandler touched = (_, _) => Interlocked.Increment(ref _touched);
            watcher.Created += touched;
            watcher.Changed += touched;
            watcher.Deleted += touched;
            watcher.Renamed += (_, _) => Interlocked.Increment(ref _touched);
            watcher.Error += (_, _) => Interlocked.Increment(ref _touched);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_watcher == null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
    }

    public string Name => "dir";
    public bool Writable => _writable;

    // The listing/content changes whenever the backing tree is touched; expose the root's
    // last-write time so any cache keyed on Generation is at least invalidated on top-level edits.
    // With a watcher on, every event under the root also bumps the counter, which is what makes a
    // deep change invalidate the caches above: a timestamp of the root alone never moves for it.
    //
    // The timestamp is remembered for a second rather than read on every call. Every cache layer
    // asks for the generation of the source below it during each lookup, and this source may be
    // another instance's mount: reading it every time made one lookup a stat that crossed a mount
    // boundary, which measured as a memory cache "hitting" in 4 ms instead of microseconds.
    private long _ticks;
    private long _ticksAt;
    private bool _ticksKnown;
    private readonly object _ticksGate = new();
    public string Generation
    {
        get
        {
            long touched = Interlocked.Read(ref _touched);
            long now = Environment.TickCount64;
            if (!Volatile.Read(ref _ticksKnown) || now - Interlocked.Read(ref _ticksAt) >= 1000)
            {
                lock (_ticksGate)
                {
                    if (!_ticksKnown || now - _ticksAt >= 1000)
                    {
                        try { _ticks = Directory.GetLastWriteTimeUtc(_root).Ticks; }
                        catch { _ticks = 0; }
                        Volatile.Write(ref _ticksAt, now);
                        Volatile.Write(ref _ticksKnown, true);
                    }
                }
            }
            return Interlocked.Read(ref _ticks) + ":" + touched;
        }
    }

    public string Root => _root;

    public bool TryApply(IReadOnlyList<ObjectChange> changes)
    {
        if (!_writable) return false;
        // This provider writes the physical tree. Only explicit removals delete anything: a
        // directory object is the resolver's view of that directory, which may be a cached or
        // a slightly older one, and deleting whatever on disk it does not name would take files
        // another process just created with it.
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
        foreach (var change in changes.Where(c => c.Object == null))
        { var path = LocalPath(change.Path); if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path); }
        return true;
    }

    public string LocalPath(string rel) => Path.Combine(_root, rel.Replace('/', '\\'));

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
