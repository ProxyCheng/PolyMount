using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core.Update;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class ReleaseFeedTests
{
    [TestMethod]
    public void A_github_release_names_its_version_and_page()
    {
        var release = ReleaseFeed.Parse("""
            { "tag_name": "v1.4.0", "html_url": "https://example.invalid/releases/1.4.0", "draft": false }
            """);

        Assert.IsNotNull(release);
        Assert.AreEqual("v1.4.0", release.Version);
        Assert.AreEqual("https://example.invalid/releases/1.4.0", release.Url);
    }

    [TestMethod]
    public void A_plain_feed_may_say_version_and_url()
    {
        var release = ReleaseFeed.Parse("""
            { "version": "2.0.0", "url": "https://example.invalid/download" }
            """);

        Assert.IsNotNull(release);
        Assert.AreEqual("2.0.0", release.Version);
        Assert.AreEqual("https://example.invalid/download", release.Url);
    }

    [TestMethod]
    public void A_feed_without_a_version_is_not_a_release()
    {
        Assert.IsNull(ReleaseFeed.Parse("{ \"message\": \"Not Found\" }"));
        Assert.IsNull(ReleaseFeed.Parse("[]"));
    }

    [DataTestMethod]
    [DataRow("1.2.3", "1.2.3")]
    [DataRow("v1.2.3", "1.2.3")]
    [DataRow("V1.2.3", "1.2.3")]
    [DataRow("1.2.3+abcdef", "1.2.3")]
    [DataRow("1.2", "1.2")]
    [DataRow("10.0.0.1", "10.0.0.1")]
    public void A_tag_reads_as_the_version_it_spells(string tag, string expected) =>
        Assert.AreEqual(Version.Parse(expected), ReleaseFeed.ParseVersion(tag));

    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("latest")]
    [DataRow("release-notes")]
    public void Anything_else_is_not_a_version(string tag) => Assert.IsNull(ReleaseFeed.ParseVersion(tag));

    [DataTestMethod]
    [DataRow("1.2.4", true)]
    [DataRow("v1.2.4", true)]
    [DataRow("1.2.3", false)]
    [DataRow("1.2.2", false)]
    [DataRow("1.2.10", true)]
    [DataRow("1.10.0", true)]
    [DataRow("garbage", false)]
    [DataRow("", false)]
    public void A_release_is_newer_only_when_it_reads_as_later(string tag, bool newer) =>
        Assert.AreEqual(newer, ReleaseFeed.IsNewer(tag, new Version(1, 2, 3)));

    [TestMethod]
    public void Versions_compare_as_numbers_not_as_text()
    {
        // "1.2.10" is ahead of "1.2.9"; a string comparison would put it behind.
        Assert.IsTrue(ReleaseFeed.IsNewer("1.2.10", new Version(1, 2, 9)));
        Assert.IsFalse(ReleaseFeed.IsNewer("1.2.9", new Version(1, 2, 10)));
    }
}
