using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class WriteCommitTests
{
    [TestMethod]
    public void Small_dirty_commit_uses_memory_snapshot_and_persists_content()
    {
        var file = new SharedObjectFile(new MemoryStream(), "test", _ => { }, snapshot =>
        {
            Assert.IsInstanceOfType<MemoryStream>(snapshot);
            using var reader = new StreamReader(snapshot, leaveOpen: true);
            Assert.AreEqual("payload", reader.ReadToEnd());
        }, () => { });
        file.TryAcquire(true, out var handle);
        using (handle) { handle.Write("payload"u8.ToArray(), 0, 7); handle.Flush(); }
    }

    [TestMethod]
    public void Clean_flush_close_and_noop_writes_do_not_allocate_or_commit_snapshots()
    {
        int snapshots = 0, commits = 0;
        var file = new SharedObjectFile(new MemoryStream(), "test", _ => { }, snapshot =>
        {
            commits++;
            Assert.AreEqual(3L, snapshot.Length);
        }, () => { }, () => { snapshots++; return new MemoryStream(); });
        Assert.IsTrue(file.TryAcquire(true, out var handle));
        handle.Flush();
        handle.SetLength(0);
        handle.Write(Array.Empty<byte>(), 0, 0);
        handle.Flush();
        Assert.AreEqual(0, snapshots);
        handle.Write("abc"u8.ToArray(), 0, 3);
        handle.Flush();
        Assert.AreEqual(1, snapshots);
        handle.SetLength(3);
        handle.Flush();
        handle.Dispose();
        Assert.AreEqual(1, snapshots);
        Assert.AreEqual(1, commits);
    }

    [TestMethod]
    public void Failed_commit_remains_dirty_and_can_be_retried()
    {
        int attempts = 0;
        var file = new SharedObjectFile(new MemoryStream(), "test", _ => { }, snapshot =>
        {
            if (++attempts == 1) throw new IOException("failure");
            Assert.AreEqual(3L, snapshot.Length);
        }, () => { }, () => new MemoryStream());
        file.TryAcquire(true, out var handle);
        handle.Write("abc"u8.ToArray(), 0, 3);
        Assert.ThrowsException<IOException>(() => handle.Flush());
        handle.Flush();
        handle.Dispose();
        Assert.AreEqual(2, attempts);
    }
}
