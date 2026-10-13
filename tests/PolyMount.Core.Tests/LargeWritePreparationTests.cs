using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class LargeWritePreparationTests
{
    [TestMethod]
    public void Reopening_published_file_without_changes_does_not_replace_blob()
    {
        using var temp = new TempDir();
        string root = temp.Sub("overlay");
        var vfs = new VirtualFileSystem(new ILayer[] { new OverlayLayer(root, temp.Sub("state")) });
        using (var file = vfs.OpenWrite("large", true, out _)) { file.SetLength(1048576); file.Flush(); }
        var before = Directory.GetFiles(Path.Combine(root, "objects-v2"), "*.blob");
        using (vfs.OpenWrite("large", false, out _)) { }
        CollectionAssert.AreEqual(before, Directory.GetFiles(Path.Combine(root, "objects-v2"), "*.blob"));
    }
    [TestMethod]
    public void Large_commit_snapshot_is_published_and_random_update_survives_reopen()
    {
        using var temp = new TempDir();
        string root = temp.Sub("overlay");
        var vfs = new VirtualFileSystem(new ILayer[] { new OverlayLayer(root, temp.Sub("state")) });
        using (var file = vfs.OpenWrite("large", true, out _))
        {
            file.SetLength(2 * 1048576);
            file.Position = 1048576;
            file.Write("payload"u8);
            file.Flush();
        }
        var reopened = new VirtualFileSystem(new ILayer[] { new OverlayLayer(root, temp.Sub("state")) });
        using var read = reopened.OpenRead("large", out _);
        Assert.AreEqual(2L * 1048576, read.Length);
        read.Position = 1048576;
        byte[] bytes = new byte[7]; read.ReadExactly(bytes);
        CollectionAssert.AreEqual("payload"u8.ToArray(), bytes);
    }

    private sealed class BlockedContent(ManualResetEventSlim entered, ManualResetEventSlim release) : MemoryStream("new"u8.ToArray())
    {
        public override void CopyTo(Stream destination, int bufferSize)
        { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); base.CopyTo(destination, bufferSize); }
    }
    [TestMethod]
    public async Task Blob_preparation_does_not_block_published_metadata_or_reads()
    {
        using var temp = new TempDir();
        var db = new ObjectDatabase(temp.Sub("db"), false);
        using (var old = new MemoryStream("old"u8.ToArray()))
            db.Apply(new[] { new ObjectChange("file", new(new VfsEntry { Name = "file", Size = 3 }), old) });
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var content = new BlockedContent(entered, release);
        var writing = Task.Run(() => db.Apply(new[] { new ObjectChange("file", new(new VfsEntry { Name = "file", Size = 3 }), content) }));
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            await Task.Run(() =>
            {
                Assert.IsTrue(db.TryGet("file", out _));
                Assert.IsTrue(db.TryOpen("file", out _, out var stream));
                using var reader = new StreamReader(stream); Assert.AreEqual("old", reader.ReadToEnd());
            }).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { release.Set(); await writing.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.IsTrue(db.TryOpen("file", out _, out var updated));
        using var result = new StreamReader(updated); Assert.AreEqual("new", result.ReadToEnd());
    }
}
