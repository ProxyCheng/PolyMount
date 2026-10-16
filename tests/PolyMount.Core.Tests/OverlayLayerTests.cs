using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public class OverlayLayerTests
{
    [TestMethod]
    public void Compressed_objects_and_directory_lists_survive_reopen()
    {
        using var temp = new TempDir();
        var overlay = new OverlayLayer(temp.Sub("overlay"), true);
        var vfs = new VirtualFileSystem(new[] { overlay });
        vfs.CreateDirectory("dir");
        string text = new('x', 100000);
        using (var write = vfs.OpenWrite("dir/file", true, out _)) write.Write(System.Text.Encoding.UTF8.GetBytes(text));
        var reopened = new VirtualFileSystem(new[] { new OverlayLayer(temp.Sub("overlay"), true) });
        Assert.AreEqual("file", reopened.List("dir").Single().Name);
        using var reader = new StreamReader(reopened.OpenRead("dir/file", out var header));
        Assert.AreEqual(100000L, header.Size);
        Assert.AreEqual(text, reader.ReadToEnd());
        var store = new ObjectDatabase(temp.Sub("overlay"), true);
        Assert.IsTrue(store.Records.Sum(r => r.StoredBytes) < 10000);
    }

    [TestMethod]
    public void Batch_failure_does_not_publish_partial_directory_change()
    {
        using var temp = new TempDir();
        var store = new ObjectDatabase(temp.Sub("objects"), false);
        store.Apply(new[] { new ObjectChange("", VirtualObject.Directory("", Array.Empty<VfsEntry>())) });
        Assert.ThrowsException<ArgumentException>(() => store.Apply(new ObjectChange[]
        {
            new("", VirtualObject.Directory("", new[] { new VfsEntry { Name = "bad" } })),
            new("bad", new VirtualObject(new VfsEntry { Name = "bad" }))
        }));
        Assert.IsTrue(store.TryGet("", out var root));
        Assert.AreEqual(0, root.Children!.Count);
    }
}
