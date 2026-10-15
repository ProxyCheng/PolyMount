using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;

namespace PolyMount.Core.Tests;

/// <summary>A read is scheduled by the lane it travels in, and the lane is decided where the
/// work is created — not by each layer guessing from what it was asked for.</summary>
[TestClass]
public sealed class ReadLaneTests
{
    [TestMethod]
    public void ASpeculativeLoadReachesTheSourceInTheSpeculativeClass()
    {
        var source = new FakeSourceLayer();
        source.AddFile("dir/file.bin", "payload");
        var vfs = new VirtualFileSystem(new ILayer[] { source });

        vfs.LoadObject("dir/file.bin", speculative: true);

        Assert.IsTrue(source.Lanes.Count > 0, "the source was never reached");
        CollectionAssert.AreEqual(
            new[] { ReadPriority.Speculative },
            source.Lanes.Select(call => call.Priority).Distinct().ToArray(),
            "a layer the load reached was not told the read was read-ahead: " +
            string.Join(", ", source.Lanes.Select(call => call.Operation + "=" + call.Priority)));
        Assert.IsTrue(source.Lanes.Any(call => call.Operation == "entry"), "the header was not resolved");
        Assert.IsTrue(source.Lanes.Any(call => call.Operation == "read"), "the content was not opened");
    }

    [TestMethod]
    public void ADemandLoadReachesTheSourceInTheReaderClass()
    {
        var source = new FakeSourceLayer();
        source.AddFile("dir/file.bin", "payload");
        var vfs = new VirtualFileSystem(new ILayer[] { source });

        vfs.LoadObject("dir/file.bin");

        CollectionAssert.AreEqual(
            new[] { ReadPriority.Demand },
            source.Lanes.Select(call => call.Priority).Distinct().ToArray(),
            "a read nobody marked came through as read-ahead: " +
            string.Join(", ", source.Lanes.Select(call => call.Operation + "=" + call.Priority)));
    }

    [TestMethod]
    public void ASpeculativeListingIsMarkedToo()
    {
        // A directory listing is the request read-ahead issues most of, and it reaches the
        // source through the resolver rather than through a load, so it needs its own check.
        var source = new FakeSourceLayer();
        source.AddDir("dir");
        var vfs = new VirtualFileSystem(new ILayer[] { source });

        vfs.List("dir", speculative: true);
        CollectionAssert.AreEqual(new[] { ReadPriority.Speculative },
            source.Lanes.Select(call => call.Priority).Distinct().ToArray());

        source.Lanes.Clear();
        vfs.List("dir");
        CollectionAssert.AreEqual(new[] { ReadPriority.Demand },
            source.Lanes.Select(call => call.Priority).Distinct().ToArray());
    }

    [TestMethod]
    public void MarkingSpeculativeWorkTwiceUnwindsOnce()
    {
        Assert.AreEqual(ReadPriority.Demand, ReadPriority.Ambient);
        using (ReadPriority.Enter(ReadPriority.Speculative))
        {
            Assert.AreEqual(ReadPriority.Speculative, ReadPriority.Ambient);
            // A boundary nested in another is already marked, and must not clear what the outer
            // one is still holding when it finishes.
            using (ReadPriority.Enter(ReadPriority.Speculative)) Assert.AreEqual(ReadPriority.Speculative, ReadPriority.Ambient);
            Assert.AreEqual(ReadPriority.Speculative, ReadPriority.Ambient);
        }
        Assert.AreEqual(ReadPriority.Demand, ReadPriority.Ambient);
    }
}
