using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class ListingWarmupTests
{
    private sealed class Chain : IDisposable
    {
        public TempDir Temp = null!;
        public FakeSourceLayer Source = null!;
        public LruCacheLayer Cache = null!;
        public MetricsCollector Metrics = new();
        public VirtualFileSystem Vfs = null!;

        public static Chain Build()
        {
            var chain = new Chain();
            chain.Temp = new TempDir();
            chain.Source = new FakeSourceLayer();
            chain.Source.AddFile("a/b/file", "x");
            chain.Source.AddFile("a/other", "y");
            chain.Source.AddDir("a/empty");
            chain.Cache = new LruCacheLayer(chain.Temp.Sub("cache"), () => chain.Source.Generation, 1024 * 1024);
            chain.Vfs = new VirtualFileSystem(new ILayer[] { chain.Cache, chain.Source }) { Metrics = chain.Metrics };
            return chain;
        }

        public void Dispose() { Cache.Dispose(); Temp.Dispose(); }
    }

    [TestMethod]
    public void Warmup_stores_listings_so_the_first_walk_stays_off_the_source()
    {
        using var chain = Chain.Build();
        using (var warmup = new ListingWarmup(chain.Vfs, () => chain.Metrics.IdleFor, limit: 16, quietMs: 0))
        {
            Assert.IsTrue(warmup.WaitForCompletion(TimeSpan.FromSeconds(10)), "the warm-up did not finish");
            // "", "a", "a/b" and "a/empty" — every directory the mount has.
            Assert.AreEqual(4, warmup.WarmedDirectories);
        }
        Assert.IsTrue(chain.Cache.TryReadObject("a", out _), "the listing was not stored in the cache");
        Assert.IsTrue(chain.Cache.TryReadObject("a/b", out _), "a nested listing was not stored in the cache");
        Assert.IsTrue(chain.Cache.TryReadObject("a/empty", out _), "an empty listing was not stored in the cache");

        int listings = chain.Source.ListedDirectories.Count;
        var children = chain.Vfs.List("a");
        Assert.AreEqual(3, children.Count);
        Assert.AreEqual(listings, chain.Source.ListedDirectories.Count,
            "a walk after the warm-up still went back to the source");
    }

    [TestMethod]
    public void Warmup_stops_at_its_budget()
    {
        using var chain = Chain.Build();
        using var warmup = new ListingWarmup(chain.Vfs, () => chain.Metrics.IdleFor, limit: 1, quietMs: 0);
        Assert.IsTrue(warmup.WaitForCompletion(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(1, warmup.WarmedDirectories);
        Assert.IsFalse(chain.Cache.TryReadObject("a", out _), "the walk went past its budget");
    }

    [TestMethod]
    public void Warmup_stops_at_its_deadline()
    {
        using var chain = Chain.Build();
        using var warmup = new ListingWarmup(chain.Vfs, () => chain.Metrics.IdleFor, limit: 64, quietMs: 0, seconds: 0);
        Assert.IsTrue(warmup.WaitForCompletion(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(0, warmup.WarmedDirectories, "a pass outlived its deadline");
    }

    [TestMethod]
    public void Warmup_waits_for_a_busy_mount()
    {
        using var chain = Chain.Build();
        chain.Vfs.List("");                       // a client read, so the mount is in use
        using var warmup = new ListingWarmup(chain.Vfs, () => chain.Metrics.IdleFor, limit: 8, quietMs: 30000);
        Assert.IsFalse(warmup.WaitForCompletion(TimeSpan.FromMilliseconds(300)));
        Assert.AreEqual(0, warmup.WarmedDirectories, "the warm-up ran while the mount was being read");
    }

    [TestMethod]
    public void A_warmed_version_is_dropped_when_the_source_moves()
    {
        using var chain = Chain.Build();
        using (var warmup = new ListingWarmup(chain.Vfs, () => chain.Metrics.IdleFor, limit: 16, quietMs: 0))
            warmup.WaitForCompletion(TimeSpan.FromSeconds(10));
        Assert.IsTrue(chain.Cache.TryReadObject("a", out _));

        chain.Source.AddFile("a/added", "z");
        chain.Source.BumpGeneration("g1");
        Assert.IsFalse(chain.Cache.TryReadObject("a", out _), "a listing outlived the version it was fetched at");
    }
}
