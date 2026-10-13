using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class CacheBudgetTests
{
    [TestMethod]
    public void Restart_prioritizes_current_generation_within_metadata_budget()
    {
        using var temp = new TempDir();
        string root = temp.Sub("cache");
        var database = new ObjectDatabase(Path.Combine(root, "reads"), false);
        var children = Enumerable.Range(0, 1100).Select(i => new VfsEntry { Name = new string('x', 5000) + i }).ToArray();
        var directory = VirtualObject.Directory("folder", children);
        database.Apply(new[] { new ObjectChange("old|folder", directory), new ObjectChange("current|folder", directory) });
        using var cache = new LruCacheLayer(root, () => "current", 1024L * 1024 * 1024);
        Assert.IsTrue(cache.TryGetEntry("folder", out _), "Older generations must not crowd out current cached content on restart");
        Assert.IsTrue(cache.EstimatedMetadataBytes <= CacheBudgets.MetadataBytes);
    }

    [TestMethod]
    public async Task Metadata_budget_evicts_recoverable_items_while_writer_is_blocked()
    {
        using var temp = new TempDir();
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => "1", 1024L * 1024 * 1024);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        cache.BeforePersist = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        var children = Enumerable.Range(0, 1000).Select(i => new VfsEntry { Name = new string('x', 5000) + i }).ToArray();
        try
        {
            cache.StoreObject("first", VirtualObject.Directory("first", children));
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            for (int i = 0; i < 4; i++) cache.StoreObject("folder" + i, VirtualObject.Directory("folder" + i, children));
            Assert.IsTrue(cache.EstimatedMetadataBytes <= CacheBudgets.MetadataBytes);
            Assert.IsTrue(cache.PendingBytes <= 256L * 1024 * 1024);
            Assert.IsFalse(cache.TryGetEntry("first", out _));
            Assert.IsTrue(cache.TryGetEntry("folder3", out _));
        }
        finally { release.Set(); }
        await cache.WaitForPendingFillsAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.IsTrue(cache.EstimatedMetadataBytes <= CacheBudgets.MetadataBytes);
    }
}
