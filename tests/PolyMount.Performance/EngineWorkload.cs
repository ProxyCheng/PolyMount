using PolyMount.Core;
using PolyMount.Layers;

internal sealed class EngineWorkload : IDisposable
{
    private readonly string _root;
    private readonly int _directories, _files;
    private readonly VirtualFileSystem? _vfs;
    private readonly LruCacheLayer? _cache;
    public EngineWorkload(string root, string backend, int directories, int files, int delayMs)
    {
        _root = root; _directories = directories; _files = files;
        Directory.CreateDirectory(root);
        for (int d = 0; d < directories; d++)
        {
            string folder = Path.Combine(root, "d" + d);
            Directory.CreateDirectory(folder);
            for (int f = 0; f < files; f++) File.WriteAllBytes(Path.Combine(folder, "f" + f), Payload(d, f));
        }
        if (backend != "Native")
        {
            ILayer source = new DelayedSource(new DirectoryLayer(root), backend == "SimulatedRemote" ? delayMs : 0);
            _cache = new LruCacheLayer(root + ".cache", () => "fixed", 256L * 1024 * 1024);
            _vfs = new VirtualFileSystem(new ILayer[] { _cache, source });
        }
    }
    private static byte[] Payload(int directory, int file) => Enumerable.Repeat((byte)((directory * 17 + file) % 251), 16384).ToArray();
    public void Load(int worker, int workers, int rounds, Action<Action> measure)
    {
        for (int round = 0; round < rounds; round++)
        for (int d = worker; d < _directories; d += workers)
        {
            int directory = d;
            string folder = "d" + directory;
            measure(() =>
            {
                int count = _vfs != null ? _vfs.List(folder).Count : Directory.EnumerateFileSystemEntries(Path.Combine(_root, folder)).Count();
                if (count != _files) throw new InvalidDataException("Directory entry count mismatch");
            });
            for (int f = 0; f < _files; f++)
            {
                int file = f;
                string path = folder + "/f" + file;
                measure(() =>
                {
                    long size;
                    if (_vfs != null)
                    {
                        if (!_vfs.TryLookup(path, out var entry)) throw new FileNotFoundException(path);
                        size = entry.Size;
                    }
                    else size = new FileInfo(Path.Combine(_root, path)).Length;
                    if (size != 16384) throw new InvalidDataException("Metadata size mismatch");
                });
                measure(() =>
                {
                    using Stream stream = _vfs != null ? _vfs.OpenRead(path, out _) : File.OpenRead(Path.Combine(_root, path));
                    // Read a header and a non-sequential page from each resource.
                    byte[] buffer = new byte[4096];
                    stream.ReadExactly(buffer);
                    if (buffer.Any(b => b != (byte)((directory * 17 + file) % 251))) throw new InvalidDataException("Header mismatch");
                    stream.Position = 8192;
                    stream.ReadExactly(buffer);
                    if (buffer.Any(b => b != (byte)((directory * 17 + file) % 251))) throw new InvalidDataException("Random page mismatch");
                });
            }
        }
    }
    public void WaitForPersistence()
    {
        // Warm phase starts after fills have settled; this wait is outside timing.
        _cache?.WaitForPendingFillsAsync().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
    }
    public void Dispose() => _cache?.Dispose();

    private sealed class DelayedSource(ILayer inner, int delayMs) : ILayer
    {
        public string Name => "source";
        public string Generation => "fixed";
        public bool Writable => false;
        public bool TryGetEntry(string path, out VfsEntry entry) => inner.TryGetEntry(path, out entry);
        public IReadOnlyList<VfsEntry> ListOwn(string path)
        { if (delayMs > 0) Thread.Sleep(delayMs); return inner.ListOwn(path); }
        public bool TryOpenRead(string path, out VfsEntry entry, out Stream stream)
        { if (delayMs > 0) Thread.Sleep(delayMs); return inner.TryOpenRead(path, out entry, out stream); }
    }
}
