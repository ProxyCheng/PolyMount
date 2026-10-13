using PolyMount.Core;

namespace PolyMount.Plugins.Svn;

public sealed class SvnLayer : ILayer, IRevisionSource
{
    private readonly SvnClient _svn;
    private readonly string _pinPath;
    private readonly Dictionary<string, IReadOnlyList<VfsEntry>> _memo = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VfsEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private long _rev;
    private DateTime _pinMtime = DateTime.MinValue;
    private readonly object _gate = new();
    private long _nextPinCheck;

    private SvnLayer(SvnClient svn, string pinPath)
    {
        _svn = svn;
        _pinPath = pinPath;
        Directory.CreateDirectory(Path.GetDirectoryName(pinPath)!);
    }

    public static SvnLayer Create(string url, string trustFailures, string pinPath,
        string? username = null, string? password = null)
        => new(new SvnClient(url, trustFailures, username, password), pinPath);

    public string Name => "svn";
    public bool Writable => false;

    public long HeadRevision() => _svn.GetHeadRevision();

    public void SetRevision(long rev)
    {
        File.WriteAllText(_pinPath, rev.ToString());
        lock (_gate)
        {
            _rev = rev;
            _pinMtime = File.GetLastWriteTimeUtc(_pinPath);
            _memo.Clear();
            _entries.Clear();
        }
    }

    public long Revision
    {
        get
        {
            // Generation is consulted for every cache lookup. Poll external pin
            // changes at most four times per second rather than stat per object.
            long now = Environment.TickCount64;
            long next = Volatile.Read(ref _nextPinCheck);
            if (now < next || Interlocked.CompareExchange(ref _nextPinCheck, now + 250, next) != next)
                return Interlocked.Read(ref _rev);
            try
            {
                var mtime = File.GetLastWriteTimeUtc(_pinPath);
                if (mtime != _pinMtime)
                {
                    var txt = File.ReadAllText(_pinPath).Trim();
                    if (long.TryParse(txt, out var r) && r > 0)
                    {
                        lock (_gate)
                        {
                            if (r != _rev) { _rev = r; _memo.Clear(); _entries.Clear(); }
                            _pinMtime = mtime;
                        }
                    }
                }
            }
            catch { }
            return Interlocked.Read(ref _rev);
        }
    }

    public string Generation => Revision.ToString();


    public bool TryGetEntry(string rel, out VfsEntry entry)
    {
        entry = null!;
        if (rel.Length == 0)
        {
            entry = new VfsEntry { Name = string.Empty, IsDir = true };
            return true;
        }

        string key = Generation + "|" + rel;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out entry!)) return true;
        }
        ListOwn(VfsPath.Parent(rel));
        lock (_gate) return _entries.TryGetValue(key, out entry!);
    }

    public IReadOnlyList<VfsEntry> ListOwn(string rel)
    {
        // WinFsp and shell probes are case-insensitive; SVN URLs are not. Resolve
        // every component to the spelling advertised by its parent listing.
        string canonical = "";
        foreach (string component in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryGetEntry(VfsPath.Child(canonical, component), out var header) || !header.IsDir)
                return Array.Empty<VfsEntry>();
            canonical = VfsPath.Child(canonical, header.Name);
        }
        rel = canonical;
        var generation = Generation;
        var key = generation + "|" + rel;
        lock (_gate)
        {
            if (_memo.TryGetValue(key, out var cached)) return cached;
        }

        IReadOnlyList<VfsEntry> list;
        try
        {
            list = _svn.List(Revision, rel)
                .Select(e => new VfsEntry { Name = e.Name, IsDir = e.IsDir, Size = e.Size, TimeUtc = e.TimeUtc })
                .ToList();
        }
        catch (SvnNotFoundException) { list = Array.Empty<VfsEntry>(); }

        lock (_gate)
        {
            _memo[key] = list;
            foreach (var child in list) _entries[generation + "|" + VfsPath.Child(rel, child.Name)] = child;
        }
        return list;
    }

    public bool TryOpenRead(string rel, out VfsEntry entry, out Stream stream)
    {
        stream = null!;
        if (!TryGetEntry(rel, out entry) || entry.IsDir) return false;

        var tmp = Path.Combine(Path.GetTempPath(), "PolyMount-svn-" + Guid.NewGuid().ToString("N") + ".tmp");
        _svn.CatToFile(Revision, rel, tmp);
        stream = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            128 * 1024, FileOptions.DeleteOnClose);
        return true;
    }

    public bool TryReadObject(string path, out VirtualObject value)
    {
        value = null!;
        if (!TryGetEntry(path, out var header)) return false;
        value = new(header, header.IsDir ? ListOwn(path) : null);
        return true;
    }
    public bool TryApply(IReadOnlyList<ObjectChange> changes) => false;
}
