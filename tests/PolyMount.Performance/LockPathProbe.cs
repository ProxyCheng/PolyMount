using System.Diagnostics;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Performance;

/// <summary>
/// What the read path's locks cost and what they serialise. Three shapes:
/// an overlay lookup (resident index plus a blob stat), a durable cache hit (the index
/// monitor alone), and a resolver lookup (the per-call key construction). Each reports
/// single-thread cost, concurrent aggregate cost, and whichever runtime counters say why.
/// Run it before and after touching the locking or the resolution key.
/// </summary>
internal static class LockPathProbe
{
    public static void Run(int files, int lookups)
    {
        Console.WriteLine($"files={files:N0} lookups={lookups:N0} GC: server={System.Runtime.GCSettings.IsServerGC}");
        RunOverlay(files, lookups);
        RunCache(files, lookups);
        RunResolve(200_000);
    }

    /// <summary>
    /// A resident overlay record for a file used to be confirmed with File.Exists while
    /// holding the store's metadata gate. Sequential cost barely moves when that check goes
    /// outside; what moves is whether other threads can look anything up meanwhile, so the
    /// number to read here is the concurrent column and the contention delta beside it.
    /// </summary>
    private static void RunOverlay(int files, int lookups)
    {
        string root = Path.Combine(Path.GetTempPath(), "PolyMount-lock-overlay-" + Guid.NewGuid().ToString("N"));
        try
        {
            var layer = new OverlayLayer(root);
            var changes = new List<ObjectChange>();
            var paths = new string[files];
            for (int i = 0; i < files; i++)
            {
                paths[i] = "f" + i;
                var entry = new VfsEntry { Name = paths[i], IsDir = false, Size = 64 };
                changes.Add(new ObjectChange(paths[i], new VirtualObject(entry), new MemoryStream(new byte[64])));
            }
            layer.TryApply(changes);

            Console.WriteLine($"overlay lookup, resident file record ({files:N0} files)");
            Measure("seq ", paths, lookups, 1, (string path, out Eval e) =>
            {
                bool found = layer.TryGetEntry(path, out var entry);
                e = new Eval(found ? entry.Size : 0);
                return found;
            });
            foreach (int threads in new[] { 4, 8 })
                Measure($"x{threads,-3}", paths, lookups, threads, (string path, out Eval e) =>
                {
                    bool found = layer.TryGetEntry(path, out var entry);
                    e = new Eval(found ? entry.Size : 0);
                    return found;
                });
        }
        finally { Delete(root); }
    }

    /// <summary>The durable cache's resident index: one monitor, a dictionary lookup and a
    /// recency touch, with no filesystem call in it. This is the lock's own floor.</summary>
    private static void RunCache(int files, int lookups)
    {
        string root = Path.Combine(Path.GetTempPath(), "PolyMount-lock-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            // A source revision, not "v1": the cache key used to carry it into a string on
            // every lookup, so how long it is is part of what the key costs.
            using var cache = new LruCacheLayer(Path.Combine(root, "cache"), () => "123456", 64L * 1024 * 1024);
            var paths = new string[files];
            for (int i = 0; i < files; i++)
            {
                paths[i] = "d" + i;
                cache.StoreObject(paths[i], VirtualObject.Directory(paths[i], Array.Empty<VfsEntry>()));
            }
            cache.WaitForPendingFillsAsync().GetAwaiter().GetResult();

            Console.WriteLine($"cache hit, resident index only ({files:N0} directories)");
            Measure("seq ", paths, lookups, 1, (string path, out Eval e) =>
            {
                bool found = cache.TryGetEntry(path, out var entry);
                e = new Eval(found ? 1 : 0);
                return found;
            });
            foreach (int threads in new[] { 4, 8 })
                Measure($"x{threads,-3}", paths, lookups, threads, (string path, out Eval e) =>
                {
                    bool found = cache.TryGetEntry(path, out var entry);
                    e = new Eval(found ? 1 : 0);
                    return found;
                });
        }
        finally { Delete(root); }
    }

    private readonly record struct Eval(long Checksum);

    private delegate bool Lookup(string path, out Eval result);

    private static void Measure(string label, string[] paths, int lookups, int threads, Lookup lookup)
    {
        int per = Math.Max(1, lookups / threads);
        long checksum = 0;
        long allocatedBytes = 0;
        void Body(int seed)
        {
            long local = 0;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < per; i++)
            {
                lookup(paths[(seed + i) % paths.Length], out var result);
                local += result.Checksum;
            }
            Interlocked.Add(ref allocatedBytes, GC.GetAllocatedBytesForCurrentThread() - allocated);
            Interlocked.Add(ref checksum, local);
        }

        // Warm the JIT and the dictionaries before either clock starts. One pass, not a
        // loop of passes: Body already runs a full phase on its own.
        Body(0);
        Interlocked.Exchange(ref allocatedBytes, 0);

        long contentionBefore = System.Threading.Monitor.LockContentionCount;
        long started = Stopwatch.GetTimestamp();
        if (threads == 1) Body(0);
        else
        {
            using var start = new ManualResetEventSlim();
            var running = new Thread[threads];
            for (int t = 0; t < threads; t++)
            {
                int seed = t * 7919;
                running[t] = new Thread(() => { start.Wait(); Body(seed); }) { IsBackground = true };
                running[t].Start();
            }
            start.Set();
            foreach (var thread in running) thread.Join();
        }
        double ns = Stopwatch.GetElapsedTime(started).TotalNanoseconds / (double)(per * threads);
        long contention = System.Threading.Monitor.LockContentionCount - contentionBefore;
        Console.WriteLine($"  {label}: {ns,9:0.0} ns/lookup  {allocatedBytes / (double)(per * threads),6:0.0} B/lookup  contention +{contention:N0}");
        GC.KeepAlive(checksum);
    }

    /// <summary>
    /// The resolver builds a key on every lookup. It used to upper-case the path to make
    /// the key case-insensitive; a comparer does the same without the allocation, and the
    /// bytes column is what that is worth. The rest of the allocation is the deferred
    /// resolution cell itself, which is the same in both builds.
    /// </summary>
    private static void RunResolve(int lookups)
    {
        var layer = new StaticLayer();
        var vfs = new VirtualFileSystem(new ILayer[] { layer });
        const string path = "dir/sub/file.txt";
        if (!vfs.TryLookup(path, out _)) throw new InvalidOperationException("fixture is missing " + path);

        for (int i = 0; i < 100_000; i++) vfs.TryLookup(path, out _);
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int i = 0; i < lookups; i++) vfs.TryLookup(path, out _);
        double ns = Stopwatch.GetElapsedTime(started).TotalNanoseconds / lookups;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"resolver lookup, cached answer ('{path}')");
        Console.WriteLine($"  seq : {ns,9:0.0} ns/lookup  {bytes / (double)lookups,7:0.0} B/lookup");
    }

    /// <summary>Prebuilt objects and no allocation of its own, so what the resolver allocates
    /// is the resolver's.</summary>
    private sealed class StaticLayer : ILayer
    {
        private readonly Dictionary<string, VirtualObject> _objects = new(StringComparer.OrdinalIgnoreCase);
        public StaticLayer()
        {
            var leaf = new VfsEntry { Name = "file.txt", IsDir = false, Size = 10 };
            var sub = VirtualObject.Directory("dir/sub", new[] { leaf });
            var dir = VirtualObject.Directory("dir", new[] { new VfsEntry { Name = "sub", IsDir = true } });
            var root = VirtualObject.Directory("", new[] { new VfsEntry { Name = "dir", IsDir = true } });
            _objects[""] = root;
            _objects["dir"] = dir;
            _objects["dir/sub"] = sub;
            _objects["dir/sub/file.txt"] = new VirtualObject(leaf);
        }
        public string Name => "static";
        public string Generation => "1";
        public bool Writable => false;
        public bool TryGetEntry(string rel, out VfsEntry entry)
        {
            if (_objects.TryGetValue(rel, out var value)) { entry = value.Header; return true; }
            entry = null!; return false;
        }
        public IReadOnlyList<VfsEntry> ListOwn(string rel) =>
            _objects.TryGetValue(rel, out var value) ? value.Children ?? Array.Empty<VfsEntry>() : Array.Empty<VfsEntry>();
        public bool TryOpenRead(string rel, out VfsEntry entry, out Stream stream)
        {
            entry = null!; stream = null!; return false;
        }
        bool IObjectLayer.TryReadObject(string path, out VirtualObject value) =>
            _objects.TryGetValue(path, out value!);
    }

    private static void Delete(string root)
    {
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
