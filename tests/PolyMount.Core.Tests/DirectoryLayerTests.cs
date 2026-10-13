using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public class DirectoryLayerTests
{
    private static string ReadAll(Stream s)
    {
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    [TestMethod]
    public void Maps_existing_directory_contents()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.Sub("root", "sub"));
        File.WriteAllText(t.Sub("root", "a.txt"), "AAA");
        File.WriteAllText(t.Sub("root", "sub", "b.txt"), "BBB");

        var layer = new DirectoryLayer(t.Sub("root"));

        var top = layer.ListOwn("").Select(e => e.Name).OrderBy(x => x).ToArray();
        CollectionAssert.AreEqual(new[] { "a.txt", "sub" }, top);

        Assert.IsTrue(layer.TryGetEntry("a.txt", out var e));
        Assert.AreEqual(3, e.Size);

        Assert.IsTrue(layer.TryOpenRead("sub/b.txt", out _, out var s));
        using (s) Assert.AreEqual("BBB", ReadAll(s));
    }

    [TestMethod]
    public void Is_local_source_marker()
    {
        using var t = new TempDir();
        var layer = new DirectoryLayer(t.Sub("root"));
        Assert.IsInstanceOfType(layer, typeof(ILocalSource));
    }

    [TestMethod]
    public void Readonly_by_default_rejects_writes()
    {
        using var t = new TempDir();
        var layer = new DirectoryLayer(t.Sub("root"));
        Assert.IsFalse(layer.Writable);
        Assert.ThrowsException<NotSupportedException>(() => layer.Create("x.txt", out _));
        Assert.ThrowsException<NotSupportedException>(() => layer.CreateDirectory("d"));
    }

    [TestMethod]
    public void Writable_mode_allows_passthrough_writes()
    {
        using var t = new TempDir();
        var layer = new DirectoryLayer(t.Sub("root"), writable: true);
        Assert.IsTrue(layer.Writable);

        using (var s = layer.Create("x.txt", out _))
        {
            var b = System.Text.Encoding.UTF8.GetBytes("hi");
            s.Write(b, 0, b.Length);
        }
        Assert.IsTrue(File.Exists(t.Sub("root", "x.txt")));
        Assert.AreEqual("hi", File.ReadAllText(t.Sub("root", "x.txt")));

        layer.Delete("x.txt", shadowedBelow: false);
        Assert.IsFalse(File.Exists(t.Sub("root", "x.txt")));
    }

    [TestMethod]
    public void Generation_changes_when_root_mutated()
    {
        using var t = new TempDir();
        var layer = new DirectoryLayer(t.Sub("root"));
        var g1 = layer.Generation;

        // Force a later timestamp on the root directory.
        Directory.SetLastWriteTimeUtc(t.Sub("root"), DateTime.UtcNow.AddMinutes(5));
        var g2 = layer.Generation;

        Assert.AreNotEqual(g1, g2);
    }
}
