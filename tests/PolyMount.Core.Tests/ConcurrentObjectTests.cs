using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class ConcurrentObjectTests
{
    [TestMethod]
    public void Parallel_handles_on_same_file_merge_disjoint_writes()
    {
        using var temp = new TempDir();
        var vfs = new VirtualFileSystem(new[] { new OverlayLayer(temp.Sub("overlay"), temp.Sub("state")) });
        using (var create = vfs.OpenWrite("shared", true, out _)) create.SetLength(32 * 4096);
        Parallel.For(0, 32, index =>
        {
            using var file = vfs.OpenWrite("shared", false, out _);
            file.Position = index * 4096;
            file.Write(Enumerable.Repeat((byte)index, 4096).ToArray());
        });
        using var read = vfs.OpenRead("shared", out _);
        var bytes = new byte[32 * 4096]; read.ReadExactly(bytes);
        for (int index = 0; index < 32; index++) Assert.IsTrue(bytes.Skip(index * 4096).Take(4096).All(b => b == index));
    }

    [TestMethod]
    public void Separate_read_handle_observes_unflushed_shared_write()
    {
        using var temp = new TempDir();
        var vfs = new VirtualFileSystem(new[] { new OverlayLayer(temp.Sub("overlay"), temp.Sub("state")) });
        using var writer = vfs.OpenWrite("file", true, out _);
        writer.Write("latest"u8);
        using var reader = vfs.OpenRead("file", out _);
        var bytes = new byte[6]; reader.ReadExactly(bytes);
        Assert.AreEqual("latest", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [TestMethod]
    public async Task Deleted_cache_directory_is_a_miss_and_is_recreated()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer(); source.AddFile("folder/file", "source");
        using var cache = new LruCacheLayer(temp.Sub("cache"), () => source.Generation, 1048576);
        var vfs = new VirtualFileSystem(new ILayer[] { cache, source });
        using (var read = vfs.OpenRead("folder/file", out _)) { }
        await cache.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(temp.Sub("cache"), true);
        cache.ValidateStorage();
        Assert.AreEqual("fake", vfs.Probe("folder").Single(p => p.Effective).Name);
        using var reader = new StreamReader(vfs.OpenRead("folder/file", out _));
        Assert.AreEqual("source", reader.ReadToEnd());
        await cache.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(Directory.Exists(temp.Sub("cache")));
        Assert.AreEqual("cache", vfs.Probe("folder/file").Single(p => p.Effective).Name);
    }

    [TestMethod]
    public void Release_boundary_skips_source_and_drops_only_selected_records()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Sub("source"));
        File.WriteAllText(temp.Sub("source", "file"), "source");
        var profile = new MountProfile { Name = "test", StoreRoot = temp.Sub("store"), Layers = new()
        {
            new() { Id = "overlay", Type = "overlay" }, new() { Id = "cache", Type = "cache" },
            new() { Id = "dir", Type = "dir", Source = new() { Path = temp.Sub("source") } }
        } };
        using var mount = new MountInstance(profile);
        using (var read = mount.Vfs.OpenRead("file", out _)) { }
        using (var writer = mount.Vfs.OpenWrite("file", true, out _)) writer.Write("local"u8);
        var result = mount.ReleaseAbove("file", 3, new[] { "overlay", "cache", "dir" });
        Assert.AreEqual("dir", result.EffectiveLayer);
        using var reader = new StreamReader(mount.Vfs.OpenRead("file", out _));
        Assert.AreEqual("source", reader.ReadToEnd());
    }

    [TestMethod]
    public void Usage_and_access_metrics_include_reads_writes_and_directory_changes()
    {
        using var temp = new TempDir();
        var profile = new MountProfile { Name = "test", StoreRoot = temp.Sub("store"), Layers = new() { new() { Id = "overlay", Type = "overlay" } } };
        using var mount = new MountInstance(profile);
        using (var writer = mount.Vfs.OpenWrite("file", true, out _)) writer.Write("content"u8);
        using (var reader = mount.Vfs.OpenRead("file", out _)) { }
        var metrics = mount.GetMetrics(60);
        Assert.IsTrue(metrics.TotalWrites >= 2);
        Assert.AreEqual(1L, metrics.TotalReads);
        Assert.AreEqual(100.0, metrics.Layers.Single().AccessSharePercent);
        var usage = mount.GetStatus().Providers.Single().StorageUsage!;
        Assert.AreEqual(1, usage.FileObjects);
        Assert.AreEqual(1, usage.DirectoryObjects);
        Assert.AreEqual(2, usage.DurableObjects);
        Assert.IsTrue(usage.DiskBytes >= usage.ObjectBytes);
    }

    [TestMethod]
    public void A_metadata_lookup_is_a_read_but_a_mutation_check_is_not()
    {
        using var temp = new TempDir();
        var profile = new MountProfile { Name = "test", StoreRoot = temp.Sub("store"), Layers = new() { new() { Id = "overlay", Type = "overlay" } } };
        using var mount = new MountInstance(profile);
        using (var writer = mount.Vfs.OpenWrite("file", true, out _)) writer.Write("content"u8);
        // Creating a file looks up its parent for its own reasons; that is not a request.
        Assert.AreEqual(0L, mount.GetMetrics(60).TotalReads);
        // A metadata query names a file or a folder, and is the read a listing and an open were
        // already counted as: it reaches the source and pays for it.
        Assert.IsTrue(mount.Vfs.TryLookup("file", out _, recordAccess: true));
        Assert.AreEqual(1L, mount.GetMetrics(60).TotalReads);
        Assert.IsTrue(mount.Vfs.TryLookup("file", out _));
        Assert.AreEqual(1L, mount.GetMetrics(60).TotalReads);
    }
}
