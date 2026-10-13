using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public class OverlayLayerTests
{
    private static OverlayLayer New(TempDir t) =>
        new(t.Sub("overlay"), t.Sub("state", "tombstones"));

    private static string ReadAll(Stream s)
    {
        s.Seek(0, SeekOrigin.Begin);
        using var r = new StreamReader(s, System.Text.Encoding.UTF8, leaveOpen: true);
        return r.ReadToEnd();
    }

    [TestMethod]
    public void Create_write_then_read_roundtrips()
    {
        using var t = new TempDir();
        var o = New(t);

        using (var s = o.Create("dir/file.txt", out var e))
        {
            Assert.IsFalse(e.IsDir);
            var bytes = System.Text.Encoding.UTF8.GetBytes("hello");
            s.Write(bytes, 0, bytes.Length);
        }

        Assert.IsTrue(o.TryGetEntry("dir/file.txt", out var entry));
        Assert.AreEqual(5, entry.Size);

        Assert.IsTrue(o.TryOpenRead("dir/file.txt", out _, out var read));
        using (read) Assert.AreEqual("hello", ReadAll(read));
    }

    [TestMethod]
    public void ListOwn_returns_dirs_and_files()
    {
        using var t = new TempDir();
        var o = New(t);
        o.CreateDirectory("sub");
        using (o.Create("a.txt", out _)) { }

        var names = o.ListOwn("").Select(e => e.Name).OrderBy(x => x).ToArray();
        CollectionAssert.AreEqual(new[] { "a.txt", "sub" }, names);
    }

    [TestMethod]
    public void Delete_with_shadow_writes_tombstone_that_persists()
    {
        using var t = new TempDir();
        var o = New(t);
        using (o.Create("gone.txt", out _)) { }

        o.Delete("gone.txt", shadowedBelow: true);
        Assert.IsTrue(o.IsDeleted("gone.txt"));

        // A freshly constructed overlay must reload the tombstone from disk.
        var reopened = New(t);
        Assert.IsTrue(reopened.IsDeleted("gone.txt"));
    }

    [TestMethod]
    public void IsDeleted_is_hierarchical()
    {
        using var t = new TempDir();
        var o = New(t);
        o.CreateDirectory("d");
        o.Delete("d", shadowedBelow: true);

        Assert.IsTrue(o.IsDeleted("d"));
        Assert.IsTrue(o.IsDeleted("d/child.txt"));
        Assert.IsFalse(o.IsDeleted("other"));
    }

    [TestMethod]
    public void Create_after_delete_removes_tombstone()
    {
        using var t = new TempDir();
        var o = New(t);
        using (o.Create("f.txt", out _)) { }
        o.Delete("f.txt", shadowedBelow: true);
        Assert.IsTrue(o.IsDeleted("f.txt"));

        using (o.Create("f.txt", out _)) { }
        Assert.IsFalse(o.IsDeleted("f.txt"));
    }

    [TestMethod]
    public void Rename_moves_file()
    {
        using var t = new TempDir();
        var o = New(t);
        using (var s = o.Create("a.txt", out _))
        {
            var b = System.Text.Encoding.UTF8.GetBytes("data");
            s.Write(b, 0, b.Length);
        }

        o.Rename("a.txt", "b.txt", replace: false, shadowedBelow: false);

        Assert.IsFalse(o.TryGetEntry("a.txt", out _));
        Assert.IsTrue(o.TryOpenRead("b.txt", out _, out var r));
        using (r) Assert.AreEqual("data", ReadAll(r));
    }
}
