using System.Collections.Concurrent;
using PolyMount.Core;

namespace PolyMount.Layers;

public sealed class LruCacheLayer : ICacheLayer
{
    private readonly string _root;
    private readonly Func<string> _version;
    private readonly long _capacity;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, long> _size = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _access = new(StringComparer.OrdinalIgnoreCase);
    private long _total;

    public LruCacheLayer(string root, Func<string> version, long capacityBytes)
    {
        _root = root;
        _version = version;
        _capacity = capacityBytes;
        Directory.CreateDirectory(_root);
        Index();
    }

    public string Name => "cache";
    public string Generation => _version();
    public bool Writable => false;

    public long TotalBytes => Interlocked.Read(ref _total);
    public int Count => _size.Count;
    public long Capacity => _capacity;

    public bool IsDeleted(string rel) => false;

    private string PathFor(string rel) => Path.Combine(_root, Generation, rel.Replace('/', '\\'));

    public bool TryGetEntry(string rel, out VfsEntry entry)
    {
        entry = null!;
        var p = PathFor(rel);
        if (!File.Exists(p)) return false;
        var fi = new FileInfo(p);
        Touch(p, fi.Length);
        entry = new VfsEntry { Name = VfsPath.Name(rel), IsDir = false, Size = fi.Length, TimeUtc = fi.LastWriteTimeUtc };
        return true;
    }

    public IReadOnlyList<VfsEntry> ListOwn(string rel) => Array.Empty<VfsEntry>();

    public bool TryOpenRead(string rel, out VfsEntry entry, out Stream stream)
    {
        stream = null!;
        if (!TryGetEntry(rel, out entry)) return false;
        stream = new FileStream(PathFor(rel), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return true;
    }

    public void Store(string rel, string version, VfsEntry entry, Stream content)
    {
        if (entry.IsDir) return;
        try
        {
            var p = Path.Combine(_root, version, rel.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);

            var tmp = p + ".tmp-" + Guid.NewGuid().ToString("N");
            using (var fs = File.Create(tmp)) content.CopyTo(fs);
            if (File.Exists(p)) File.Delete(p);
            File.Move(tmp, p);

            Touch(p, new FileInfo(p).Length);
            Evict();
        }
        catch { }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _size.Clear();
            _access.Clear();
            _total = 0;
        }
    }

    public void Purge()
    {
        lock (_gate)
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                        try { File.Delete(file); } catch { }
                    foreach (var dir in Directory.EnumerateDirectories(_root))
                        try { Directory.Delete(dir, true); } catch { }
                }
            }
            catch { }
            _size.Clear();
            _access.Clear();
            _total = 0;
        }
    }

    public Stream Create(string rel, out VfsEntry entry) => throw new NotSupportedException("cache is read-only");
    public Stream OpenExistingWrite(string rel, out VfsEntry entry) => throw new NotSupportedException("cache is read-only");
    public void CreateDirectory(string rel) => throw new NotSupportedException("cache is read-only");
    public void Delete(string rel, bool shadowedBelow) => throw new NotSupportedException("cache is read-only");
    public void Rename(string rel, string dst, bool replace, bool shadowedBelow) => throw new NotSupportedException("cache is read-only");

    private void Touch(string path, long size)
    {
        var key = Path.GetFullPath(path);
        if (_size.TryGetValue(key, out var prev)) Interlocked.Add(ref _total, size - prev);
        else Interlocked.Add(ref _total, size);
        _size[key] = size;
        _access[key] = DateTime.UtcNow.Ticks;
    }

    private void Evict()
    {
        if (Interlocked.Read(ref _total) <= _capacity) return;
        lock (_gate)
        {
            while (Interlocked.Read(ref _total) > _capacity)
            {
                string? victim = null;
                long oldest = long.MaxValue;
                foreach (var kv in _access)
                    if (kv.Value < oldest) { oldest = kv.Value; victim = kv.Key; }
                if (victim == null) break;

                if (_size.TryRemove(victim, out var sz)) Interlocked.Add(ref _total, -sz);
                _access.TryRemove(victim, out _);
                try { if (File.Exists(victim)) File.Delete(victim); } catch { }
            }
        }
    }

    private void Index()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                var fi = new FileInfo(f);
                var key = Path.GetFullPath(f);
                _size[key] = fi.Length;
                _access[key] = fi.LastAccessTimeUtc.Ticks;
                Interlocked.Add(ref _total, fi.Length);
            }
        }
        catch { }
    }
}
