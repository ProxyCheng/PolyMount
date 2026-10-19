using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

/// <summary>
/// The staging budget scales with the cache instead of staying at a fixed 256 MiB. A package
/// larger than that floor used to be refused outright, so it was downloaded again on every
/// start even though the cache had room to keep it.
/// </summary>
[TestClass]
public sealed class LargeContentAdmissionTests
{
    [TestMethod]
    public void The_staging_budget_scales_with_the_cache_and_keeps_a_floor()
    {
        using var temp = new TempDir();
        using var large = new LruCacheLayer(temp.Sub("large"), () => "g1", 16L * 1024 * 1024 * 1024);
        Assert.AreEqual(4L * 1024 * 1024 * 1024, large.PendingBudget,
            "a 16 GiB cache stages up to 4 GiB, which is the cap");

        using var medium = new LruCacheLayer(temp.Sub("medium"), () => "g1", 4L * 1024 * 1024 * 1024);
        Assert.AreEqual(1L * 1024 * 1024 * 1024, medium.PendingBudget);

        using var small = new LruCacheLayer(temp.Sub("small"), () => "g1", 1024L * 1024 * 1024);
        Assert.AreEqual(256L * 1024 * 1024, small.PendingBudget, "small caches keep the old floor");
    }
}
