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
}
