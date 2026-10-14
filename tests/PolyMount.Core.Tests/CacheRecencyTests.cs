using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

/// <summary>
/// Layers are consulted top down and the first hit wins, so an object kept in an upper
/// cache is invisible to the caches underneath it. Those entries must still record the
/// read, otherwise a recency-ordered eviction discards the objects that are in use —
/// which is expensive for directories, where losing one invalidates every path below it.
/// </summary>
[TestClass]
public sealed class CacheRecencyTests
{
    /// <summary>Never serves anything; records every recency notification it receives.</summary>
    private sealed class SpyCache : ILayer, IObjectCache
    {
        public readonly List<string> Touched = new();
        public string Name => "spy";
        public string Generation => "v1";
        public bool Writable => false;
        public bool TryGetEntry(string path, out VfsEntry entry) { entry = null!; return false; }
        public IReadOnlyList<VfsEntry> ListOwn(string path) => Array.Empty<VfsEntry>();
        public bool TryOpenRead(string path, out VfsEntry entry, out Stream stream) { entry = null!; stream = null!; return false; }
        public void StoreObject(string path, VirtualObject value, Stream? content = null) { }
        public void TouchObject(string path) => Touched.Add(path);
    }

    [TestMethod]
    public void Read_served_by_an_upper_cache_notifies_the_caches_below()
    {
        var source = new FakeSourceLayer();
        source.AddFile("dir/first", "one");
        using var memory = new MemoryCacheLayer();
        var spy = new SpyCache();
        var vfs = new VirtualFileSystem(new ILayer[] { new NamedCacheLayer("ram", memory), spy, source });

        Assert.AreEqual("first", vfs.List("dir").Single().Name);   // resolved from the source and filled upward
        spy.Touched.Clear();
        Assert.AreEqual("first", vfs.List("dir").Single().Name);   // now served by the memory cache

        CollectionAssert.Contains(spy.Touched, "dir");
        CollectionAssert.Contains(spy.Touched, "");
    }

    [TestMethod]
    public void Touched_entry_survives_an_eviction_that_would_otherwise_claim_it()
    {
        using var temp = new TempDir();
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => "v1", capacityBytes: 1200);
        // Hold the persistence worker so the byte accounting stays on its reservations
        // and the eviction decision is deterministic.
        using var hold = new ManualResetEventSlim();
        cache.BeforePersist = () => hold.Wait(TimeSpan.FromSeconds(10));

        var children = new[] { new VfsEntry { Name = "x" } };
        cache.StoreObject("a", VirtualObject.Directory("a", children));
        cache.StoreObject("b", VirtualObject.Directory("b", children));
        cache.TouchObject("a");                                    // a read a higher cache answered
        cache.StoreObject("c", VirtualObject.Directory("c", children));
        hold.Set();

        Assert.IsTrue(cache.TryReadObject("a", out _), "the touched entry was evicted");
        Assert.IsFalse(cache.TryReadObject("b", out _), "the least recently used entry should have gone");
    }

    [TestMethod]
    public void Touching_an_unknown_path_is_harmless()
    {
        using var temp = new TempDir();
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => "v1", 1024 * 1024);
        cache.TouchObject("never-stored");
        Assert.AreEqual(0, cache.Count);

        using var memory = new MemoryCacheLayer();
        memory.TouchObject("never-stored");
        Assert.AreEqual(0L, memory.MemoryBytes);
    }
}
