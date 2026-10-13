using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class WinFspMetadataTests
{
    [TestMethod]
    public void Repeated_directory_queries_return_stable_metadata()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        source.AddDir("folder");
        var mount = new WinFspMount(new VirtualFileSystem(new ILayer[] { source }), temp.Sub("mount.log"));
        Assert.AreEqual(0, mount.Open("folder", 0, 0, out var node, out var descriptor, out var first, out _));
        Thread.Sleep(20);
        Assert.AreEqual(0, mount.GetFileInfo(node, descriptor, out var second));
        Assert.AreEqual(first.LastWriteTime, second.LastWriteTime);
        Assert.AreEqual(first.ChangeTime, second.ChangeTime);
        Assert.AreEqual(first.IndexNumber, second.IndexNumber);
    }

    [TestMethod]
    public void Directory_entry_identity_matches_opened_child()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer(); source.AddFile("folder/file", "source");
        var mount = new WinFspMount(new VirtualFileSystem(new ILayer[] { source }), temp.Sub("mount.log"));
        Assert.AreEqual(0, mount.Open("folder", 0, 0, out var node, out var descriptor, out _, out _));
        object context = null!;
        Assert.IsTrue(mount.ReadDirectoryEntry(node, descriptor, "", "", ref context, out var name, out var listed));
        Assert.AreEqual("file", name);
        Assert.AreEqual(0, mount.Open("folder/file", 0, 0, out var child, out var handle, out var opened, out _));
        try
        {
            Assert.AreEqual(listed.IndexNumber, opened.IndexNumber);
            Assert.AreEqual(listed.LastWriteTime, opened.LastWriteTime);
            Assert.AreEqual(0, mount.GetFileInfo(child, handle, out var queried));
            Assert.AreEqual(opened.LastWriteTime, queried.LastWriteTime);
        }
        finally { mount.Close(child, handle); }
    }
}
