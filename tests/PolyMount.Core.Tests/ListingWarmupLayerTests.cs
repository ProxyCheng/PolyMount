using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class ListingWarmupLayerTests
{
    private sealed class Chain : IDisposable
    {
        public TempDir Temp = null!;
        public FakeSourceLayer Source = null!;
        public LruCacheLayer Upper = null!;
        public LruCacheLayer Lower = null!;
        public ListingWarmupLayer Warmup = null!;
        public VirtualFileSystem Vfs = null!;

        public static Chain Build(bool withUpper = true)
        {
            var chain = new Chain();
            chain.Temp = new TempDir();
            chain.Source = new FakeSourceLayer();
            chain.Source.AddFile("a/b/file", "x");
            chain.Source.AddFile("a/other", "y");
            chain.Source.AddDir("a/empty");
            chain.Upper = new LruCacheLayer(chain.Temp.Sub("upper"), () => chain.Source.Generation, 1024 * 1024);
            chain.Lower = new LruCacheLayer(chain.Temp.Sub("lower"), () => chain.Source.Generation, 1024 * 1024);
            chain.Warmup = new ListingWarmupLayer(() => chain.Source.Generation, limit: 16, seconds: 10, quietMs: 0);
            var layers = withUpper
                ? new ILayer[] { chain.Upper, chain.Warmup, chain.Lower, chain.Source }
                : new ILayer[] { chain.Warmup, chain.Source };
            chain.Vfs = new VirtualFileSystem(layers);
            return chain;
        }

        public void Dispose() { Upper.Dispose(); Lower.Dispose(); Temp.Dispose(); }
    }

    [TestMethod]
    public void The_layer_serves_no_read_of_its_own()
    {
        using var chain = Chain.Build();
        Assert.IsFalse(chain.Warmup.TryGetEntry("a", out _));
        Assert.IsFalse(chain.Warmup.TryOpenRead("a", out _, out _));
        Assert.AreEqual(0, chain.Warmup.ListOwn("a").Count);
        Assert.IsFalse(chain.Warmup.Writable);
        // The resolver still answers the same thing through the chain.
        Assert.AreEqual(3, chain.Vfs.List("a").Count);
    }

    [TestMethod]
    public void It_warms_the_caches_below_it_and_only_those()
    {
        using var chain = Chain.Build();
        // The walk resolves through the lower chain, whose top layer is `Lower`.
        chain.Warmup.Attach(new VirtualFileSystem(new ILayer[] { chain.Lower, chain.Source }), () => TimeSpan.Zero);
        chain.Warmup.Start();
        Assert.IsTrue(chain.Warmup.WaitForCompletion(TimeSpan.FromSeconds(10)), "the warm-up did not finish");
        Assert.AreEqual(4, chain.Warmup.WarmedDirectories);

        Assert.IsTrue(chain.Lower.TryReadObject("a", out _), "the cache below the layer was not warmed");
        Assert.IsTrue(chain.Lower.TryReadObject("a/b", out _));
        // Nothing above the warm-up layer was touched: it was not in the walk's chain.
        Assert.IsFalse(chain.Upper.TryReadObject("a", out _), "a cache above the layer was warmed");
    }

    [TestMethod]
    public void A_chain_with_nothing_to_store_into_does_not_walk()
    {
        using var chain = Chain.Build(withUpper: false);
        chain.Warmup.Attach(new VirtualFileSystem(new ILayer[] { chain.Source }), () => TimeSpan.Zero);
        chain.Warmup.Start();
        Assert.IsTrue(chain.Warmup.WaitForCompletion(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(0, chain.Warmup.WarmedDirectories);
        Assert.AreEqual(0, chain.Source.ListedDirectories.Count, "the walk ran with no cache to keep its listings");
    }

    [TestMethod]
    public void Stopping_ends_the_walk_and_keeps_the_count_readable()
    {
        using var chain = Chain.Build();
        chain.Warmup.Attach(new VirtualFileSystem(new ILayer[] { chain.Lower, chain.Source }), () => TimeSpan.Zero);
        chain.Warmup.Start();
        Assert.IsTrue(chain.Warmup.WaitForCompletion(TimeSpan.FromSeconds(10)));
        long warmed = chain.Warmup.WarmedDirectories;
        chain.Warmup.Stop();
        Assert.AreEqual(warmed, chain.Warmup.WarmedDirectories);
    }

    [TestMethod]
    public void An_old_warm_flag_becomes_a_layer_on_top_and_stays_one_layer()
    {
        var profile = new MountProfile
        {
            Name = "legacy",
            WarmListings = true,
            WarmListingsLimit = 123,
            WarmListingsSeconds = 45,
            Layers = new()
            {
                new() { Id = "cache", Type = "cache" },
                new() { Id = "svn", Type = "svn" }
            }
        };

        var layers = profile.GetLayers();
        Assert.AreEqual(3, layers.Count);
        Assert.AreEqual(ListingWarmupLayer.Type, layers[0].Type, "the flag's layer must go above the chain it warms");
        Assert.AreEqual(123, layers[0].Parameters["Limit"].GetInt32());
        Assert.AreEqual(45, layers[0].Parameters["Seconds"].GetInt32());

        // Once the editor has stored that layer, the still-set flag must not add another.
        profile.Layers = layers;
        Assert.AreEqual(3, profile.GetLayers().Count);
    }

    [TestMethod]
    public void An_explicit_warmup_layer_suppresses_the_legacy_one()
    {
        var profile = new MountProfile
        {
            Name = "explicit",
            WarmListings = true,
            WarmListingsLimit = 2000,
            Layers = new()
            {
                new() { Id = "cache", Type = "cache" },
                new() { Id = "mine", Type = ListingWarmupLayer.Type },
                new() { Id = "svn", Type = "svn" }
            }
        };
        var layers = profile.GetLayers();
        Assert.AreEqual(3, layers.Count);
        Assert.AreEqual("mine", layers[1].Id, "the chain was rewritten around the explicit layer");
    }
}
