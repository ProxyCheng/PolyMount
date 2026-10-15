using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class ObjectContentTests
{
    [TestMethod]
    public void Directory_payload_snapshots_members_and_keeps_storage_json_compatible()
    {
        var child = new VfsEntry { Name = "Child", IsDir = true };
        var children = new[] { child };
        var value = VirtualObject.Directory("folder", children);
        var payload = (DirectoryObjectContent)value.Content;
        children[0] = new VfsEntry { Name = "changed" };
        Assert.IsTrue(payload.Contains("child"));
        Assert.IsFalse(payload.Contains("changed"));
        string json = JsonSerializer.Serialize(value);
        Assert.IsFalse(json.Contains("\"Content\""));
        var restored = JsonSerializer.Deserialize<VirtualObject>(json)!;
        Assert.IsInstanceOfType<DirectoryObjectContent>(restored.Content);
        Assert.IsTrue(((DirectoryObjectContent)restored.Content).Contains("child"));
        var copy = value.Snapshot();
        Assert.IsTrue(((DirectoryObjectContent)copy.Content).Contains("child"));
    }

    [TestMethod]
    public void File_payload_read_factory_returns_independent_seekable_streams()
    {
        ObjectContent payload = new FileObjectContent(3, () => new MemoryStream("abc"u8.ToArray(), false));
        using var first = payload.OpenRead(); using var second = payload.OpenRead();
        first.Position = 2;
        Assert.AreEqual((int)'c', first.ReadByte());
        Assert.AreEqual((int)'a', second.ReadByte());
        Assert.AreEqual(3L, payload.LogicalBytes);
        Assert.IsNull(payload.Entries);
    }

    [TestMethod]
    public void A_header_only_payload_is_neither_a_listing_nor_bytes()
    {
        var value = VirtualObject.HeaderOnly(new VfsEntry { Name = "file.bin", IsDir = false, Size = 4096 });
        var payload = value.Content;
        Assert.IsInstanceOfType<HeaderObjectContent>(payload);
        Assert.AreEqual(0L, payload.LogicalBytes);
        Assert.AreEqual(128L, payload.EstimatedMemoryBytes);
        Assert.AreEqual(0L, payload.PersistenceEstimate, "a header must never consume content capacity");
        Assert.IsNull(payload.Entries, "it is not a directory listing");
        Assert.ThrowsException<NotSupportedException>(() => payload.OpenRead());
        Assert.AreSame(payload, payload.Snapshot());
        // Both shapes must survive being asked, including a directory with no children.
        Assert.IsNotNull(VirtualObject.HeaderOnly(new VfsEntry { Name = "dir", IsDir = true }).Content);
        var copy = value.Snapshot();
        Assert.IsInstanceOfType<HeaderObjectContent>(copy.Content);
        Assert.AreEqual(4096L, copy.Header.Size);
    }
}
