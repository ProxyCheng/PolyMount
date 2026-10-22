using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

/// <summary>
/// The walk itself: fetch a directory, let the resolver's fill path keep the listing, move on.
/// Where a listing is kept is the chain's business, so these tests pin only what the walk
/// promises — the order it visits, and that it stops at its budgets and out of a reader's way.
/// </summary>
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
    public void The_walk_fills_the_chain_it_reads_through()
    {
        using var chain = Chain.Build();
        int listed = chain.Source.ListedDirectories.Count;
        using (var warmup = new ListingWarmup(chain.Vfs, () => chain.Metrics.IdleFor, limit: 16, quietMs: 0))
        {
            Assert.IsTrue(warmup.WaitForCompletion(TimeSpan.FromSeconds(10)), "the warm-up did not finish");
            // "", "a", "a/b" and "a/empty" — every directory the mount has.
            Assert.AreEqual(4, warmup.WarmedDirectories);
        }
        Assert.IsTrue(chain.Cache.TryReadObject("a", out var listing), "the listing the walk fetched was not kept");
        Assert.AreEqual(3, listing.Children!.Count);
        Assert.IsTrue(chain.Cache.TryReadObject("a/empty", out var empty));
        Assert.AreEqual(0, empty.Children!.Count, "an empty directory is a listing too");

        // A second walk of the same tree is answered by what the first one kept.
        int after = chain.Source.ListedDirectories.Count;
        using (var again = new ListingWarmup(chain.Vfs, () => chain.Metrics.IdleFor, limit: 16, quietMs: 0))
            again.WaitForCompletion(TimeSpan.FromSeconds(10));
        Assert.AreEqual(after, chain.Source.ListedDirectories.Count, "a warm walk went back to the source");
        Assert.IsTrue(listed < after);
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
}
