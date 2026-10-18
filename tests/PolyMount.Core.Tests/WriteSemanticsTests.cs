using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

/// <summary>Namespace and handle rules a Windows caller relies on: what a rename, a delete and a
/// racing read are allowed to leave behind.</summary>
[TestClass]
public class WriteSemanticsTests
{
    /// <summary>A source that runs something in the middle of an open, which is where a write
    /// racing a read lands.</summary>
    private sealed class InterruptingSource(FakeSourceLayer inner) : ILayer
    {
        public Action? DuringOpen;
        public string Name => inner.Name;
        public string Generation => inner.Generation;
        public bool Writable => false;
        public bool TryGetEntry(string rel, out VfsEntry entry) => inner.TryGetEntry(rel, out entry);
        public IReadOnlyList<VfsEntry> ListOwn(string rel) => inner.ListOwn(rel);
        public bool TryOpenRead(string rel, out VfsEntry entry, out Stream stream)
        {
            bool opened = inner.TryOpenRead(rel, out entry, out stream);
            var during = DuringOpen; DuringOpen = null;
            during?.Invoke();
            return opened;
        }
    }

    private static string Read(VirtualFileSystem vfs, string path)
    {
        using var reader = new StreamReader(vfs.OpenRead(path, out _));
        return reader.ReadToEnd();
    }

    [TestMethod]
    public void A_read_that_raced_a_write_does_not_leave_its_older_bytes_in_the_cache()
    {
        using var temp = new TempDir();
        var fake = new FakeSourceLayer(); fake.AddFile("file", "old");
        var source = new InterruptingSource(fake);
        var overlay = new OverlayLayer(temp.Sub("overlay"));
        var cache = new LruCacheLayer(temp.Sub("cache"), () => source.Generation, 1 << 20, writable: false);
        var vfs = new VirtualFileSystem(new ILayer[] { cache, overlay, source });
        source.DuringOpen = () => { using var write = vfs.OpenWrite("file", true, out _); write.Write("new"u8); };

        Assert.AreEqual("old", Read(vfs, "file"), "the racing read itself sees what it opened");
        Assert.AreEqual("new", Read(vfs, "file"), "the next read must see the write, not the racing read's copy");
    }

    [TestMethod]
    public void A_read_that_raced_a_source_move_does_not_publish_under_the_new_generation()
    {
        using var temp = new TempDir();
        var fake = new FakeSourceLayer(); fake.AddFile("file", "old");
        var source = new InterruptingSource(fake);
        var cache = new LruCacheLayer(temp.Sub("cache"), () => source.Generation, 1 << 20, writable: false);
        var vfs = new VirtualFileSystem(new ILayer[] { cache, source });
        source.DuringOpen = () => { fake.AddFile("file", "new!"); fake.BumpGeneration("g1"); };

        Assert.AreEqual("old", Read(vfs, "file"));
        Assert.AreEqual("new!", Read(vfs, "file"));
    }

    [TestMethod]
    public void Rename_onto_an_ancestor_is_refused_and_leaves_the_siblings()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer(); source.AddFile("a/b/file", "x"); source.AddFile("a/sibling", "keep");
        var vfs = new VirtualFileSystem(new ILayer[] { new OverlayLayer(temp.Sub("overlay")), source });
        Assert.ThrowsException<IOException>(() => vfs.Rename("a/b", "a", true));
        CollectionAssert.AreEquivalent(new[] { "b", "sibling" }, vfs.List("a").Select(c => c.Name).ToArray());
    }

    [TestMethod]
    public void Rename_never_replaces_a_directory()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer(); source.AddFile("file", "x"); source.AddFile("dir/inner", "y");
        var vfs = new VirtualFileSystem(new ILayer[] { new OverlayLayer(temp.Sub("overlay")), source });
        Assert.ThrowsException<UnauthorizedAccessException>(() => vfs.Rename("file", "dir", true));
        Assert.ThrowsException<VfsNameCollisionException>(() => vfs.Rename("file", "dir", false));
        Assert.AreEqual("y", Read(vfs, "dir/inner"));
    }

    [TestMethod]
    public void Writes_through_a_renamed_handle_land_under_the_new_name()
    {
        using var temp = new TempDir();
        var vfs = new VirtualFileSystem(new ILayer[] { new OverlayLayer(temp.Sub("overlay")) });
        var handle = vfs.OpenWrite("draft.tmp", true, out _);
        handle.Write("first"u8);
        vfs.Rename("draft.tmp", "final.txt", false);
        handle.Write(" second"u8);
        handle.Dispose();

        Assert.IsFalse(vfs.TryLookup("draft.tmp", out _));
        Assert.AreEqual("first second", Read(vfs, "final.txt"));
    }

    [TestMethod]
    public void A_deleted_file_is_not_put_back_by_its_open_handle()
    {
        using var temp = new TempDir();
        var vfs = new VirtualFileSystem(new ILayer[] { new OverlayLayer(temp.Sub("overlay")) });
        var handle = vfs.OpenWrite("gone", true, out _);
        handle.Write("data"u8);
        vfs.Delete("gone");
        handle.Write("more"u8);
        handle.Dispose();
        Assert.IsFalse(vfs.TryLookup("gone", out _));
        Assert.AreEqual(0, vfs.List("").Count);
    }

    [TestMethod]
    public void Delete_on_close_refuses_a_directory_that_still_lists_something()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer(); source.AddFile("dir/file", "x"); source.AddDir("empty");
        var vfs = new VirtualFileSystem(new ILayer[] { new OverlayLayer(temp.Sub("overlay")), source });
        Assert.ThrowsException<VfsDirectoryNotEmptyException>(() => vfs.CheckDelete("dir"));
        vfs.CheckDelete("empty");
        vfs.CheckDelete("dir/file");
        Assert.ThrowsException<VfsNotFoundException>(() => vfs.CheckDelete("missing"));
    }

    [TestMethod]
    public void Delete_on_close_is_refused_on_a_read_only_chain()
    {
        var source = new FakeSourceLayer(); source.AddFile("file", "x");
        var vfs = new VirtualFileSystem(new ILayer[] { source });
        Assert.ThrowsException<UnauthorizedAccessException>(() => vfs.CheckDelete("file"));
    }

    [TestMethod]
    public void A_writable_directory_source_keeps_files_another_process_created()
    {
        using var temp = new TempDir();
        var root = temp.Sub("tree");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        var memory = new MemoryCacheLayer();
        var vfs = new VirtualFileSystem(new ILayer[] { memory, new DirectoryLayer(root, writable: true) });
        Assert.AreEqual(0, vfs.List("sub").Count, "the listing is now cached above the source");
        File.WriteAllText(Path.Combine(root, "sub", "external.txt"), "theirs");
        using (var write = vfs.OpenWrite("sub/mine.txt", true, out _)) write.Write("mine"u8);
        Assert.IsTrue(File.Exists(Path.Combine(root, "sub", "external.txt")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "sub", "mine.txt")));
    }

    [TestMethod]
    public void Releasing_a_local_record_leaves_the_listing_to_what_is_underneath()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer(); source.AddFile("edited", "src");
        var overlay = new OverlayLayer(temp.Sub("overlay"));
        var vfs = new VirtualFileSystem(new ILayer[] { new NamedLayer("overlay", overlay), source });
        using (var write = vfs.OpenWrite("edited", true, out _)) write.Write("a longer local edit"u8);
        using (var write = vfs.OpenWrite("local", true, out _)) write.Write("only here"u8);
        vfs.CreateDirectory("dir");
        using (var write = vfs.OpenWrite("dir/inner", true, out _)) write.Write("x"u8);

        foreach (var path in new[] { "edited", "local", "dir" })
        {
            overlay.DropObject(path);
            vfs.ReconcileAfterRelease(path, "overlay");
        }
        vfs.InvalidateCaches();

        var listed = vfs.List("");
        CollectionAssert.AreEqual(new[] { "edited" }, listed.Select(c => c.Name).ToArray());
        Assert.AreEqual(3, listed[0].Size, "the listing reports the source's size again");
        Assert.AreEqual("src", Read(vfs, "edited"));
        Assert.IsFalse(overlay.TryGetEntry("dir/inner", out _), "a released directory takes its children's records");
    }

    [TestMethod]
    public void Failures_reach_the_caller_as_the_status_they_mean()
    {
        Assert.AreEqual(NtStatus.ObjectNameCollision, WinFspMount.StatusOf(new VfsNameCollisionException("x")));
        Assert.AreEqual(NtStatus.DirectoryNotEmpty, WinFspMount.StatusOf(new VfsDirectoryNotEmptyException("x")));
        Assert.AreEqual(NtStatus.ObjectNameNotFound, WinFspMount.StatusOf(new VfsNotFoundException("x")));
        Assert.AreEqual(NtStatus.AccessDenied, WinFspMount.StatusOf(new UnauthorizedAccessException()));
        Assert.AreEqual(NtStatus.DiskFull, WinFspMount.StatusOf(new IOException("full", unchecked((int)0x80070070))));
        Assert.AreEqual(NtStatus.SharingViolation, WinFspMount.StatusOf(new IOException("busy", unchecked((int)0x80070020))));
        Assert.AreEqual(NtStatus.UnexpectedIoError, WinFspMount.StatusOf(new InvalidOperationException()));
    }
}
