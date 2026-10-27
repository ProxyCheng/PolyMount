using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

/// <summary>One name, asked about several times while nothing could have changed, is resolved
/// once. Windows asks the file system about a name more than once for one operation — the
/// security lookup, the open, and the parent-directory open the create resolves its path
/// through — and each ask used to walk every ancestor again.</summary>
[TestClass]
public sealed class MetadataReuseTests
{
    private sealed class Fixture : IDisposable
    {
        public TempDir Temp = new();
        public OverlayLayer Overlay = null!;
        public FakeSourceLayer Source = null!;
        public VirtualFileSystem Vfs = null!;

        public static Fixture Build()
        {
            var f = new Fixture();
            f.Overlay = new OverlayLayer(f.Temp.Sub("overlay"));
            f.Source = new FakeSourceLayer();
            f.Vfs = new VirtualFileSystem(new ILayer[] { f.Overlay, f.Source });
            return f;
        }

        public void Dispose() { Temp.Dispose(); }
    }

    [TestMethod]
    public void Consecutive_lookups_of_one_name_resolve_once()
    {
        using var f = Fixture.Build();
        f.Source.AddFile("a.txt", "one");

        Assert.IsTrue(f.Vfs.TryLookup("a.txt", out var first, recordAccess: true));
        Assert.IsTrue(f.Vfs.TryLookup("a.txt", out var second, recordAccess: true));

        Assert.AreEqual(first.Size, second.Size);
        Assert.AreEqual("a.txt", second.Name);
        // The root's listing is what answers a name at the root, so counting its resolutions
        // counts them: the second ask was answered by the first answer.
        Assert.AreEqual(1, f.Source.ListedDirectories.Count(l => l == ""));
    }

    [TestMethod]
    public void Another_name_in_between_resolves_both()
    {
        using var f = Fixture.Build();
        f.Source.AddFile("a.txt", "one");
        f.Source.AddFile("b.txt", "two");

        Assert.IsTrue(f.Vfs.TryLookup("a.txt", out _));
        Assert.IsTrue(f.Vfs.TryLookup("b.txt", out var b));
        Assert.IsTrue(f.Vfs.TryLookup("a.txt", out var a));

        Assert.AreEqual(3, b.Size);
        Assert.AreEqual(3, a.Size);
        Assert.AreEqual(3, f.Source.ListedDirectories.Count(l => l == ""),
            "each ask about a different name resolves the root again");
    }

    [TestMethod]
    public void A_write_between_lookups_is_not_answered_by_the_reused_one()
    {
        using var f = Fixture.Build();
        f.Source.AddFile("a.txt", "one");
        Assert.IsTrue(f.Vfs.TryLookup("a.txt", out var before, recordAccess: true));
        Assert.AreEqual(3, before.Size);

        using (var write = f.Vfs.OpenWrite("a.txt", truncate: false, out _))
        {
            write.Seek(0, SeekOrigin.End);
            write.Write(new byte[] { (byte)'!' });
        }

        Assert.IsTrue(f.Vfs.TryLookup("a.txt", out var after, recordAccess: true));
        Assert.AreEqual(4, after.Size);
    }

    [TestMethod]
    public void A_moved_source_is_not_answered_by_the_reused_one()
    {
        using var f = Fixture.Build();
        f.Source.AddFile("a.txt", "one");
        Assert.IsTrue(f.Vfs.TryLookup("a.txt", out _, recordAccess: true));

        // The source moves under the mount: same name, different object.
        f.Source.BumpGeneration("g1");
        f.Source.AddFile("a.txt", "one and a half");

        Assert.IsTrue(f.Vfs.TryLookup("a.txt", out var after, recordAccess: true));
        Assert.AreEqual(14, after.Size);
    }

    [TestMethod]
    public void A_deleted_name_is_not_found_again()
    {
        using var f = Fixture.Build();
        f.Source.AddFile("a.txt", "one");
        Assert.IsTrue(f.Vfs.TryLookup("a.txt", out _, recordAccess: true));

        f.Vfs.Delete("a.txt");

        Assert.IsFalse(f.Vfs.TryLookup("a.txt", out _));
    }

    [TestMethod]
    public void An_open_write_handle_answers_with_its_live_size()
    {
        using var f = Fixture.Build();
        f.Source.AddFile("a.txt", "one");
        Assert.IsTrue(f.Vfs.TryLookup("a.txt", out _, recordAccess: true));

        using var write = f.Vfs.OpenWrite("a.txt", truncate: false, out _);
        write.Seek(0, SeekOrigin.End);
        write.Write(new byte[] { 1, 2, 3 });

        // Not committed, so nothing has moved the epoch: the staging handle is what has the
        // live size, and a lookup must read that rather than the answer it reused.
        Assert.IsTrue(f.Vfs.TryLookup("a.txt", out var live, recordAccess: true));
        Assert.AreEqual(6, live.Size);
    }

    [TestMethod]
    public void A_layer_generation_follows_the_chain_below_it()
    {
        var source = new CountingLayer("source");
        var chain = new ILayer[] { new CountingLayer("above"), new CountingLayer("middle"), source };
        var generation = new MountInstance.LowerChainGeneration(chain, 0);

        string first = generation.Read();
        Assert.AreEqual(first, generation.Read(), "an unchanged chain reads the same value");
        Assert.AreEqual(first, generation.Read());
        Assert.AreNotEqual("g0", first, "more than one layer below is summarised, not passed through");

        source.Bump("g7");
        string moved = generation.Read();
        Assert.AreNotEqual(first, moved);

        // One layer below is the single-source chain: its own generation is the whole key, which
        // is the revision-based folder those stores were built with.
        var single = new MountInstance.LowerChainGeneration(new ILayer[] { chain[0], source }, 0);
        Assert.AreEqual("g7", single.Read());
        source.Bump("g8");
        Assert.AreEqual("g8", single.Read());
    }

    private sealed class CountingLayer(string name) : ILayer
    {
        private string _generation = "g0";
        public string Name => name;
        public string Generation => _generation;
        public bool Writable => false;
        public void Bump(string generation) => _generation = generation;
        public bool TryGetEntry(string path, out VfsEntry entry)
        {
            entry = new VfsEntry { Name = VfsPath.Name(path), IsDir = true };
            return true;
        }
        public IReadOnlyList<VfsEntry> ListOwn(string path) => Array.Empty<VfsEntry>();
        public bool TryOpenRead(string path, out VfsEntry entry, out Stream stream)
        {
            entry = null!; stream = null!; return false;
        }
    }
}
