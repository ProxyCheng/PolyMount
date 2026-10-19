using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

/// <summary>
/// A store accepts writes immediately and runs their transaction a short window later, so a
/// burst of small files costs a handful of FULL commits instead of one per Apply. These tests
/// pin both halves: this process reads what it wrote, and another reader sees it only after a
/// commit, a clear, a batch bound, or the window.
/// </summary>
[TestClass]
public sealed class WriteCoalescingTests
{
    // Longer than any test: only the explicit durability points may commit.
    private const int NeverWithinATest = 10 * 60 * 1000;

    private static ObjectChange FolderChange(string path) =>
        new(path, VirtualObject.Directory(path, Array.Empty<VfsEntry>()));

    private static ObjectChange FileChange(string path, int size = 4) =>
        new(path, new VirtualObject(new VfsEntry { Name = path, IsDir = false, Size = size }),
            new MemoryStream(new byte[size]));

    private static string BlobDirectory(string root) => Path.Combine(root, "objects-v2");

    [TestMethod]
    public void Accepted_writes_are_readable_before_the_batch_runs()
    {
        using var temp = new TempDir();
        var db = new ObjectDatabase(temp.Sub("db"), false, commitWindowMs: NeverWithinATest);
        db.Apply(new[] { FileChange("file", 4) });

        Assert.AreEqual(0, db.Commits);
        Assert.AreEqual(1, db.PendingChanges);
        Assert.IsTrue(db.TryGet("file", out var value), "the writer must read its own write");
        Assert.AreEqual(4L, value.Header.Size);
        Assert.IsTrue(db.TryOpen("file", out _, out var stream));
        using (stream) Assert.AreEqual(4L, stream.Length);

        // A second reader goes through the table, which the window has not reached yet.
        var other = new ObjectDatabase(temp.Sub("db"), false);
        Assert.IsFalse(other.TryGet("file", out _), "durability is what the window defers");

        db.Commit();
        var reopened = new ObjectDatabase(temp.Sub("db"), false);
        Assert.IsTrue(reopened.TryGet("file", out _));
    }

    [TestMethod]
    public void A_burst_shares_one_transaction()
    {
        using var temp = new TempDir();
        var db = new ObjectDatabase(temp.Sub("db"), false, commitWindowMs: NeverWithinATest);
        for (int i = 0; i < 20; i++) db.Apply(new[] { FolderChange("dir" + i) });
        Assert.AreEqual(0, db.Commits);
        Assert.AreEqual(20, db.PendingChanges);

        db.Commit();
        Assert.AreEqual(1, db.Commits, "twenty Apply calls must not become twenty fsyncs");

        var reopened = new ObjectDatabase(temp.Sub("db"), false);
        for (int i = 0; i < 20; i++) Assert.IsTrue(reopened.TryGet("dir" + i, out _));
    }

    [TestMethod]
    public void The_batch_bound_commits_without_waiting_for_the_window()
    {
        using var temp = new TempDir();
        var db = new ObjectDatabase(temp.Sub("db"), false, commitWindowMs: NeverWithinATest, commitBatchChanges: 3);
        db.Apply(new[] { FolderChange("a") });
        db.Apply(new[] { FolderChange("b") });
        Assert.AreEqual(0, db.Commits);
        db.Apply(new[] { FolderChange("c") });
        Assert.AreEqual(1, db.Commits, "the bound submits the batch it completes");
        Assert.AreEqual(0, db.PendingChanges);
    }

    [TestMethod]
    public async Task The_window_commits_without_an_explicit_flush()
    {
        using var temp = new TempDir();
        var db = new ObjectDatabase(temp.Sub("db"), false, commitWindowMs: 50);
        db.Apply(new[] { FolderChange("dir") });
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (db.Commits == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.AreEqual(1, db.Commits);

        var reopened = new ObjectDatabase(temp.Sub("db"), false);
        Assert.IsTrue(reopened.TryGet("dir", out _));
    }

    [TestMethod]
    public void Dispose_is_a_durability_point()
    {
        using var temp = new TempDir();
        var db = new ObjectDatabase(temp.Sub("db"), false, commitWindowMs: NeverWithinATest);
        db.Apply(new[] { FolderChange("dir") });
        db.Dispose();

        var reopened = new ObjectDatabase(temp.Sub("db"), false);
        Assert.IsTrue(reopened.TryGet("dir", out _));
    }

    [TestMethod]
    public void Clear_includes_the_batch_that_is_still_pending()
    {
        using var temp = new TempDir();
        var db = new ObjectDatabase(temp.Sub("db"), false, commitWindowMs: NeverWithinATest);
        db.Apply(new[] { FolderChange("dir") });
        db.Clear();

        var reopened = new ObjectDatabase(temp.Sub("db"), false);
        Assert.IsFalse(reopened.TryGet("dir", out _), "a pending row must not reappear after the clear");
    }

    [TestMethod]
    public void A_pending_delete_is_not_resurrected_from_the_table()
    {
        using var temp = new TempDir();
        string root = temp.Sub("db");
        var seed = new ObjectDatabase(root, false, recoverable: true, metadataBudget: 1024, commitWindowMs: NeverWithinATest);
        seed.Apply(new[] { FolderChange("keep") });
        seed.Commit();

        // An index that holds nothing forces a reader through the table, where the old row
        // is still waiting for the delete's transaction.
        var bounded = new ObjectDatabase(root, false, recoverable: true, metadataBudget: 1, commitWindowMs: NeverWithinATest);
        Assert.AreEqual(0, bounded.Records.Count);
        bounded.Apply(new[] { new ObjectChange("keep", null) });
        Assert.IsFalse(bounded.TryGet("keep", out _), "the pending deletion wins over the row on disk");

        bounded.Commit();
        var reopened = new ObjectDatabase(root, false, recoverable: true, metadataBudget: 1024);
        Assert.IsFalse(reopened.TryGet("keep", out _));
    }

    [TestMethod]
    public void A_pending_update_survives_index_eviction()
    {
        using var temp = new TempDir();
        string root = temp.Sub("db");
        var seed = new ObjectDatabase(root, false, recoverable: true, metadataBudget: 1024, commitWindowMs: NeverWithinATest);
        seed.Apply(new[] { FileChange("file", 1) });
        seed.Commit();

        var bounded = new ObjectDatabase(root, false, recoverable: true, metadataBudget: 1, commitWindowMs: NeverWithinATest);
        bounded.Apply(new[] { FileChange("file", 9) });
        Assert.AreEqual(0, bounded.Count, "the budget evicts the accepted row from the index");
        Assert.IsTrue(bounded.TryGet("file", out var value));
        Assert.AreEqual(9L, value.Header.Size);

        bounded.Commit();
        var reopened = new ObjectDatabase(root, false, recoverable: true, metadataBudget: 1024);
        Assert.IsTrue(reopened.TryGet("file", out value));
        Assert.AreEqual(9L, value.Header.Size);
    }

    [TestMethod]
    public void A_deleted_name_takes_its_reparse_row_with_the_batch()
    {
        using var temp = new TempDir();
        string root = temp.Sub("db");
        var db = new ObjectDatabase(root, false, commitWindowMs: NeverWithinATest);
        db.Apply(new[] { FolderChange("link") });
        db.SetReparse("link", BitConverter.GetBytes(0xA000000Cu));
        db.Commit();
        Assert.IsTrue(db.TryGetReparseTag("link", out _));

        db.Apply(new[] { new ObjectChange("link", null) });
        // A delete that has been accepted already hides both the object and its reparse
        // data, even though the row is only dropped when the batch's transaction runs.
        Assert.IsFalse(db.TryGet("link", out _));
        Assert.IsFalse(db.TryGetReparse("link", out _));
        Assert.IsFalse(db.TryGetReparseTag("link", out _));

        db.Commit();
        var reopened = new ObjectDatabase(root, false);
        Assert.IsFalse(reopened.TryGet("link", out _));
        Assert.IsFalse(reopened.TryGetReparse("link", out _), "the reparse row leaves with its object's transaction");
    }

    [TestMethod]
    public void Replaced_blob_is_reclaimed_only_after_the_batch_commits()
    {
        using var temp = new TempDir();
        string root = temp.Sub("db");
        var db = new ObjectDatabase(root, false, commitWindowMs: NeverWithinATest);
        db.Apply(new[] { FileChange("file", 64) });
        db.Commit();
        string old = Directory.GetFiles(BlobDirectory(root), "*.blob").Single();

        db.Apply(new[] { FileChange("file", 32) });
        Assert.IsTrue(File.Exists(old), "the row still on disk names this blob until the transaction runs");
        db.Commit();
        Assert.IsFalse(File.Exists(old), "the replaced blob is reclaimed once the row is replaced");
        Assert.AreEqual(1, Directory.GetFiles(BlobDirectory(root), "*.blob").Length);
    }
}
