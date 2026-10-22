using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

/// <summary>
/// The namespace store. A listing is the ancestor chain of everything beneath it, so the layer
/// keeps them and never evicts one; what it must never do is answer for content, because it has
/// none. The walk fills it, the resolver's own fills keep it current, and a moved source is
/// answered by fetching rather than by these rows.
/// </summary>
[TestClass]
public sealed class ListingWarmupLayerTests
{
    private sealed class Chain : IDisposable
    {
        public TempDir Temp = null!;
        public FakeSourceLayer Source = null!;
        public LruCacheLayer Lower = null!;
        public ListingWarmupLayer Warmup = null!;
        public VirtualFileSystem Vfs = null!;
        public string Storage = null!;

        public static Chain Build()
        {
            var chain = new Chain();
            chain.Temp = new TempDir();
            chain.Source = new FakeSourceLayer();
            chain.Source.AddFile("a/b/file", "x");
            chain.Source.AddFile("a/other", "y");
            chain.Source.AddDir("a/empty");
            chain.Storage = chain.Temp.Sub("warmup");
            chain.Lower = new LruCacheLayer(chain.Temp.Sub("lower"), () => chain.Source.Generation, 1024 * 1024);
            chain.Warmup = new ListingWarmupLayer(chain.Storage, () => chain.Source.Generation, limit: 16, seconds: 10, quietMs: 0);
            chain.Vfs = new VirtualFileSystem(new ILayer[] { chain.Warmup, chain.Lower, chain.Source });
            return chain;
        }

        /// <summary>The chain below the layer, which is what the mount hands the walk.</summary>
        public VirtualFileSystem LowerChain() => new(new ILayer[] { Lower, Source });

        public void Warm()
        {
            Warmup.Attach(LowerChain(), () => TimeSpan.Zero);
            Warmup.Start();
            Assert.IsTrue(Warmup.WaitForCompletion(TimeSpan.FromSeconds(10)), "the warm-up did not finish");
        }

        public void Dispose() { Warmup.Dispose(); Lower.Dispose(); Temp.Dispose(); }
    }

    [TestMethod]
    public void The_walk_stores_listings_in_this_layer_and_not_in_the_caches_below()
    {
        using var chain = Chain.Build();
        chain.Warm();

        Assert.AreEqual(4, chain.Warmup.WarmedDirectories, "root, a, a/b and a/empty");
        Assert.IsTrue(chain.Warmup.TryReadObject("a", out var listing), "the walk did not store the listing here");
        Assert.AreEqual(3, listing.Children!.Count);
        Assert.IsTrue(chain.Warmup.TryReadObject("a/b", out _));
        Assert.IsFalse(chain.Lower.TryReadObject("a", out _),
            "the walk stored a second copy in the cache below, where the namespace rule does not apply");
    }

    [TestMethod]
    public void A_listing_kept_here_keeps_the_source_out_of_a_later_walk()
    {
        using var chain = Chain.Build();
        chain.Warm();
        int listed = chain.Source.ListedDirectories.Count;

        var children = chain.Vfs.List("a");

        Assert.AreEqual(3, children.Count);
        Assert.AreEqual(listed, chain.Source.ListedDirectories.Count, "the listing was answered by the source again");
    }

    [TestMethod]
    public void It_answers_for_directories_and_never_for_file_content()
    {
        using var chain = Chain.Build();
        chain.Warm();

        Assert.IsTrue(chain.Warmup.TryGetEntry("a", out var entry));
        Assert.IsTrue(entry.IsDir);
        Assert.IsFalse(chain.Warmup.TryGetEntry("a/other", out _), "a path under the listing is not this layer's to answer");
        Assert.IsFalse(chain.Warmup.TryOpenRead("a/other", out _, out _), "this layer holds no bytes");
        Assert.IsFalse(chain.Warmup.Writable);

        using var stream = chain.Vfs.OpenRead("a/other", out var file);
        using var reader = new StreamReader(stream);
        Assert.AreEqual("y", reader.ReadToEnd());
        Assert.IsTrue(chain.Source.OpenReadCount > 0, "the content did not come from the source");
    }

    [TestMethod]
    public void A_listing_read_through_the_chain_is_kept_by_the_store_only()
    {
        using var chain = Chain.Build();
        var children = chain.Vfs.List("a");

        Assert.AreEqual(3, children.Count);
        Assert.IsTrue(chain.Warmup.TryReadObject("a", out _), "the store did not take the listing it was offered");
        Assert.IsFalse(chain.Lower.TryReadObject("a", out _),
            "a content cache below the namespace store kept a copy its capacity can never reclaim");
    }

    [TestMethod]
    public void A_write_replaces_the_listing_it_carried_and_drops_the_one_it_removed()
    {
        using var chain = Chain.Build();
        chain.Warm();

        // A directory change arrives with the new complete listing, so it replaces the old one.
        chain.Warmup.UpdateAfterWrite(new[]
        {
            new ObjectChange("a", VirtualObject.Directory("a", Array.Empty<VfsEntry>()))
        });
        Assert.IsTrue(chain.Warmup.TryReadObject("a", out var replaced));
        Assert.AreEqual(0, replaced.Children!.Count, "the old listing outlived the write");

        chain.Warmup.UpdateAfterWrite(new[] { new ObjectChange("a", null) });
        Assert.IsFalse(chain.Warmup.TryReadObject("a", out _), "a deleted path kept its listing");
    }

    [TestMethod]
    public void A_listing_survives_a_restart_at_the_same_generation()
    {
        using var chain = Chain.Build();
        chain.Warm();
        int listed = chain.Source.ListedDirectories.Count;
        chain.Warmup.Dispose();

        using var reopened = new ListingWarmupLayer(chain.Storage, () => chain.Source.Generation, limit: 0, seconds: 0, quietMs: 0);
        Assert.IsTrue(reopened.TryReadObject("a/b", out var listing), "the store lost what the last run warmed");
        Assert.AreEqual(1, listing.Children!.Count);
        Assert.AreEqual(listed, chain.Source.ListedDirectories.Count, "the listing was fetched instead of read back");
    }

    [TestMethod]
    public void A_moved_source_is_a_miss_rather_than_a_stale_listing()
    {
        using var chain = Chain.Build();
        chain.Warm();
        Assert.IsTrue(chain.Warmup.TryReadObject("a", out _));

        chain.Source.BumpGeneration("g1");
        chain.Warmup.RefreshGeneration();

        Assert.IsFalse(chain.Warmup.TryReadObject("a", out _), "a listing outlived the generation it was fetched at");
    }

    [TestMethod]
    public void Invalidating_drops_every_listing()
    {
        using var chain = Chain.Build();
        chain.Warm();
        chain.Warmup.Invalidate();
        Assert.IsFalse(chain.Warmup.TryReadObject("a", out _));
        Assert.IsFalse(chain.Warmup.TryReadObject("", out _));
    }

    [TestMethod]
    public void An_empty_directory_is_a_listing_and_is_kept()
    {
        using var chain = Chain.Build();
        chain.Warm();
        Assert.IsTrue(chain.Warmup.TryReadObject("a/empty", out var empty));
        Assert.AreEqual(0, empty.Children!.Count);
    }

    [TestMethod]
    public void A_store_from_an_older_generation_is_emptied_before_the_next_pass()
    {
        using var chain = Chain.Build();
        chain.Warm();
        Assert.IsTrue(chain.Warmup.Listings > 0);
        chain.Warmup.Dispose();

        // A row names its own generation, so nothing in the store would ever be read again.
        chain.Source.BumpGeneration("g1");
        using var moved = new ListingWarmupLayer(chain.Storage, () => chain.Source.Generation, limit: 16, seconds: 0, quietMs: 0);
        moved.Attach(chain.LowerChain(), () => TimeSpan.Zero);
        moved.Start();
        moved.WaitForCompletion(TimeSpan.FromSeconds(10));

        Assert.AreEqual(0, moved.Listings, "rows of the generation that was left behind stayed in the store");
    }

    [TestMethod]
    public void A_store_at_the_same_generation_is_not_emptied_by_a_restart()
    {
        using var chain = Chain.Build();
        chain.Warm();
        int listings = chain.Warmup.Listings;
        chain.Warmup.Dispose();

        using var reopened = new ListingWarmupLayer(chain.Storage, () => chain.Source.Generation, limit: 16, seconds: 0, quietMs: 0);
        reopened.Attach(chain.LowerChain(), () => TimeSpan.Zero);
        reopened.Start();
        reopened.WaitForCompletion(TimeSpan.FromSeconds(10));

        Assert.AreEqual(listings, reopened.Listings, "a restart at the same version threw away the namespace");
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
