using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

/// <summary>What a mount has to tell Windows after a write: which names appeared, left, moved or
/// changed, so watchers above the mount (Explorer, and a PolyMount instance whose source lives
/// inside another mount) see the change instead of a cached answer.</summary>
[TestClass]
public class ChangeNotificationTests
{
    private static List<FileChange> Collect(Action<VirtualFileSystem> act, Func<List<FileChange>, bool> until)
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.Sub("root"));
        var vfs = new VirtualFileSystem(new ILayer[] { new DirectoryLayer(t.Sub("root"), writable: true) });
        var seen = new List<FileChange>();
        vfs.Changed += changes => { lock (seen) seen.AddRange(changes); };
        act(vfs);
        // The notification leaves the writer's thread on purpose: poll until the one being
        // asserted on has landed, and fail with everything that did arrive when it does not.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (seen) { if (until(seen.ToList())) return seen.ToList(); }
            Thread.Sleep(25);
        }
        lock (seen) return seen.ToList();
    }

    [TestMethod]
    public void Creating_a_file_says_it_was_added()
    {
        var changes = Collect(vfs =>
        {
            using var stream = vfs.OpenWrite("a.txt", truncate: true, out _);
            stream.Write(new byte[] { 1, 2, 3 });
        }, list => list.Any(c => c.Path == "a.txt" && c.Kind == FileChangeKind.Added));
        Assert.IsTrue(changes.Any(c => c.Path == "a.txt" && c.Kind == FileChangeKind.Added),
            "expected an Added change for a.txt, got " + string.Join(", ", changes));
    }

    [TestMethod]
    public void Creating_a_directory_says_it_was_added()
    {
        var changes = Collect(vfs => vfs.CreateDirectory("d"),
            list => list.Any(c => c.Path == "d" && c.Kind == FileChangeKind.Added));
        Assert.IsTrue(changes.Any(c => c.Path == "d" && c.Kind == FileChangeKind.Added),
            "expected an Added change for d, got " + string.Join(", ", changes));
    }

    [TestMethod]
    public void Deleting_says_the_name_was_removed()
    {
        var changes = Collect(vfs =>
        {
            vfs.CreateDirectory("d");
            vfs.Delete("d");
        }, list => list.Any(c => c.Path == "d" && c.Kind == FileChangeKind.Removed));
        Assert.IsTrue(changes.Any(c => c.Path == "d" && c.Kind == FileChangeKind.Removed),
            "expected a Removed change for d, got " + string.Join(", ", changes));
    }

    [TestMethod]
    public void Renaming_names_both_ends()
    {
        var changes = Collect(vfs =>
        {
            vfs.CreateDirectory("old");
            vfs.Rename("old", "new", replace: false);
        }, list => list.Any(c => c.Path == "old" && c.Kind == FileChangeKind.RenamedOld)
                && list.Any(c => c.Path == "new" && c.Kind == FileChangeKind.RenamedNew));
        Assert.IsTrue(changes.Any(c => c.Path == "old" && c.Kind == FileChangeKind.RenamedOld),
            "expected the old name, got " + string.Join(", ", changes));
        Assert.IsTrue(changes.Any(c => c.Path == "new" && c.Kind == FileChangeKind.RenamedNew),
            "expected the new name, got " + string.Join(", ", changes));
    }

    [TestMethod]
    public void A_source_does_not_create_its_root_before_something_is_written()
    {
        using var t = new TempDir();
        string root = t.Sub("root");
        var layer = new DirectoryLayer(root);
        Assert.IsFalse(Directory.Exists(root), "constructing the layer created its root");
        Assert.IsFalse(layer.TryGetEntry("anything", out _), "an absent root has no entries");
        Directory.CreateDirectory(t.Sub("root"));
        Assert.IsTrue(Directory.Exists(root));
    }

    [TestMethod]
    public void Watching_the_source_moves_the_generation_on_a_deep_change()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.Sub("root", "deep", "deeper"));
        var layer = new DirectoryLayer(t.Sub("root"), watch: true);
        string before = layer.Generation;
        File.WriteAllText(t.Sub("root", "deep", "deeper", "x.txt"), "x");
        // The watcher reports on its own thread; poll rather than sleep a fixed time.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (layer.Generation == before && DateTime.UtcNow < deadline) Thread.Sleep(50);
        Assert.AreNotEqual(before, layer.Generation, "a deep write did not move the generation");
        layer.Dispose();
    }
}
