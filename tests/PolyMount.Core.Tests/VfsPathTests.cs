using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;

namespace PolyMount.Core.Tests;

[TestClass]
public class VfsPathTests
{
    [TestMethod]
    public void Normalize_root_variants_become_empty()
    {
        Assert.AreEqual("", VfsPath.Normalize("\\"));
        Assert.AreEqual("", VfsPath.Normalize("/"));
        Assert.AreEqual("", VfsPath.Normalize(""));
        Assert.AreEqual("", VfsPath.Normalize(null!));
    }

    [TestMethod]
    public void Normalize_strips_slashes_and_converts_separators()
    {
        Assert.AreEqual("a/b/c", VfsPath.Normalize("\\a\\b\\c"));
        Assert.AreEqual("a/b", VfsPath.Normalize("/a/b/"));
        Assert.AreEqual("a", VfsPath.Normalize("a"));
    }

    [TestMethod]
    public void Parent_and_Name()
    {
        Assert.AreEqual("a/b", VfsPath.Parent("a/b/c"));
        Assert.AreEqual("c", VfsPath.Name("a/b/c"));
        Assert.AreEqual("", VfsPath.Parent("top"));
        Assert.AreEqual("top", VfsPath.Name("top"));
    }

    [TestMethod]
    public void Child_joins()
    {
        Assert.AreEqual("x", VfsPath.Child("", "x"));
        Assert.AreEqual("a/x", VfsPath.Child("a", "x"));
    }
}
