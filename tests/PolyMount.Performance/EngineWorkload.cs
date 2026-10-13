using PolyMount.Core;
using PolyMount.Layers;

internal sealed class EngineWorkload : IDisposable
{
    private readonly string _root;
    private readonly int _directories, _files;
    private readonly VirtualFileSystem? _vfs;
    private readonly LruCacheLayer? _cache;
    private readonly DelayedSource? _source;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Count, double Ms)> _timings = new();
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
            ILayer source = _source = new DelayedSource(new DirectoryLayer(root), backend == "SimulatedRemote" ? delayMs : 0);
            _cache = new LruCacheLayer(root + ".cache", () => "fixed", 256L * 1024 * 1024);
            _vfs = new VirtualFileSystem(new ILayer[] { _cache, source });
        }
    }
    private static byte[] Payload(int directory, int file) => Enumerable.Repeat((byte)((directory * 17 + file) % 251), 16384).ToArray();
    private void Measure(string kind, Action<Action> measure, Action action)
    {
        measure(() =>
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try { action(); }
            finally
            {
                double ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                _timings.AddOrUpdate(kind, (1, ms), (_, old) => (old.Count + 1, old.Ms + ms));
            }
        });
    }
    public void PrintBreakdown(string phase)
    {
        Console.WriteLine($"  {phase}: " + string.Join("; ", _timings.OrderBy(p => p.Key).Select(p => $"{p.Key} count={p.Value.Count} mean={p.Value.Ms / p.Value.Count:0.000}ms")));
        _timings.Clear();
        if (_source != null)
            Console.WriteLine($"  source: metadata={Interlocked.Exchange(ref _source.Metadata, 0)}, listings={Interlocked.Exchange(ref _source.Listings, 0)}, opens={Interlocked.Exchange(ref _source.Opens, 0)}; cache objects={_cache!.Count}");
    }
    public void PrewarmFully()
    {
        if (_cache == null) return;
        for (int d = 0; d < _directories; d++)
        {
            Load(d, _directories, 1, action => action());
            WaitForPersistence();
        }
        _timings.Clear();
        if (_source != null) { _source.Metadata = 0; _source.Listings = 0; _source.Opens = 0; }
    }
    public void MetadataMicrobench(int workers)
    {
        if (_cache == null) return;
        string[] blobs = Directory.GetFiles(_root + ".cache/reads/objects-v2", "*.blob");
        foreach (string mode in new[] { "FileExists", "CacheHeader", "VfsLookup", "NativeOpen", "NativeSharedOpen", "NativeBlobOpen", "CacheOpen", "VfsOpen" })
        {
            using var ready = new CountdownEvent(workers);
            using var go = new ManualResetEventSlim();
            var tasks = Enumerable.Range(0, workers).Select(index => Task.Factory.StartNew(() =>
            {
                ready.Signal(); go.Wait();
                for (int i = 0; i < (mode.EndsWith("Open") ? 200 : 1000); i++)
                {
                    string path = $"d{index % _directories}/f{i % _files}";
                    if (mode.EndsWith("Open"))
                    {
                        Stream stream;
                        if (mode == "NativeOpen") stream = File.OpenRead(Path.Combine(_root, path));
                        else if (mode == "NativeSharedOpen") stream = new FileStream(Path.Combine(_root, path), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                        else if (mode == "NativeBlobOpen") stream = new FileStream(blobs[(index * 16 + i) % blobs.Length], FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                        else if (mode == "VfsOpen") stream = _vfs!.OpenRead(path, out _);
                        else if (!_cache.TryOpenRead(path, out _, out stream!)) throw new InvalidDataException("Cache open missed");
                        stream.Dispose();
                        continue;
                    }
                    bool exists = mode == "FileExists" ? File.Exists(Path.Combine(_root, path))
                        : mode == "CacheHeader" ? _cache.TryGetEntry(path, out _) : _vfs!.TryLookup(path, out _);
                    if (!exists) throw new InvalidDataException("Microbench lookup missed");
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            ready.Wait();
            var watch = System.Diagnostics.Stopwatch.StartNew(); go.Set(); Task.WaitAll(tasks);
            int operations = workers * (mode.EndsWith("Open") ? 200 : 1000);
            Console.WriteLine($"  Microbench {mode}: workers={workers} operations={operations} seconds={watch.Elapsed.TotalSeconds:0.000} ops/s={operations / watch.Elapsed.TotalSeconds:0.0}");
        }
    }
    public void Load(int worker, int workers, int rounds, Action<Action> measure)
    {
        for (int round = 0; round < rounds; round++)
        for (int d = worker; d < _directories; d += workers)
        {
            int directory = d;
            string folder = "d" + directory;
            Measure("Directory", measure, () =>
            {
                int count = _vfs != null ? _vfs.List(folder).Count : Directory.EnumerateFileSystemEntries(Path.Combine(_root, folder)).Count();
                if (count != _files) throw new InvalidDataException("Directory entry count mismatch");
            });
            for (int f = 0; f < _files; f++)
            {
                int file = f;
                string path = folder + "/f" + file;
                Measure("Metadata", measure, () =>
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
                Measure("OpenAndRead", measure, () =>
                {
                    long openStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    Stream stream = _vfs != null ? _vfs.OpenRead(path, out _) : File.OpenRead(Path.Combine(_root, path));
                    RecordTiming("Open", openStart);
                    using (stream)
                    {
                    long readStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    // Read a header and a non-sequential page from each resource.
                    byte[] buffer = new byte[4096];
                    stream.ReadExactly(buffer);
                    if (buffer.Any(b => b != (byte)((directory * 17 + file) % 251))) throw new InvalidDataException("Header mismatch");
                    stream.Position = 8192;
                    stream.ReadExactly(buffer);
                    if (buffer.Any(b => b != (byte)((directory * 17 + file) % 251))) throw new InvalidDataException("Random page mismatch");
                    RecordTiming("ReadAndVerify", readStart);
                    }
                });
            }
        }
    }
    private void RecordTiming(string kind, long start)
    {
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _timings.AddOrUpdate(kind, (1, ms), (_, old) => (old.Count + 1, old.Ms + ms));
    }
    public void WaitForPersistence()
    {
        // Warm phase starts after fills have settled; this wait is outside timing.
        _cache?.WaitForPendingFillsAsync().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
    }
    public void Dispose() => _cache?.Dispose();

    private sealed class DelayedSource(ILayer inner, int delayMs) : ILayer
    {
        public long Metadata, Listings, Opens;
        public string Name => "source";
        public string Generation => "fixed";
        public bool Writable => false;
        public bool TryGetEntry(string path, out VfsEntry entry) { Interlocked.Increment(ref Metadata); return inner.TryGetEntry(path, out entry); }
        public IReadOnlyList<VfsEntry> ListOwn(string path)
        { Interlocked.Increment(ref Listings); if (delayMs > 0) Thread.Sleep(delayMs); return inner.ListOwn(path); }
        public bool TryOpenRead(string path, out VfsEntry entry, out Stream stream)
        { Interlocked.Increment(ref Opens); if (delayMs > 0) Thread.Sleep(delayMs); return inner.TryOpenRead(path, out entry, out stream); }
    }
}
