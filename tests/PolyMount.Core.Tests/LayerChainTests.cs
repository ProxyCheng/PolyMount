using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core.Ipc;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class LayerChainTests
{
    [TestMethod]
    public void Legacy_local_profile_keeps_overlay_and_skips_cache()
    {
        var profile = new MountProfile { Source = new() { Type = "dir", Path = "C:\\source" } };
        CollectionAssert.AreEqual(new[] { "overlay", "dir" }, profile.GetLayers().Select(l => l.Type).ToArray());
    }

    [TestMethod]
    public void Ordered_sources_resolve_by_priority_and_writes_stay_in_overlay()
    {
        using var temp = new TempDir();
        string first = temp.Sub("first"), second = temp.Sub("second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "shared.txt"), "first");
        File.WriteAllText(Path.Combine(second, "shared.txt"), "second");
        var profile = Profile(temp, new() { Id = "dir-a", Type = "dir", Source = new() { Type = "dir", Path = first } },
            new() { Id = "dir-b", Type = "dir", Source = new() { Type = "dir", Path = second } });
        using (var mount = new MountInstance(profile))
        {
            using (var read = new StreamReader(mount.Vfs.OpenRead("shared.txt", out _))) Assert.AreEqual("first", read.ReadToEnd());
            using (var write = mount.Vfs.OpenWrite("shared.txt", true, out _)) write.Write("edited"u8);
            Assert.AreEqual("first", File.ReadAllText(Path.Combine(first, "shared.txt")));
            Assert.AreEqual("second", File.ReadAllText(Path.Combine(second, "shared.txt")));
            Assert.AreEqual("overlay", mount.Probe("shared.txt").EffectiveLayer);
        }
        profile.Layers!.Reverse(1, 2);
        profile.StoreRoot = temp.Sub("reordered");
        using var reordered = new MountInstance(profile);
        using var reader = new StreamReader(reordered.Vfs.OpenRead("shared.txt", out _));
        Assert.AreEqual("second", reader.ReadToEnd());
    }

    [TestMethod]
    public void Named_overlay_actions_report_direct_tombstone_and_restore()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Sub("source"));
        File.WriteAllText(temp.Sub("source", "file"), "remote");
        var profile = Profile(temp, new LayerConfig { Id = "dir", Type = "dir", Source = new() { Type = "dir", Path = temp.Sub("source") } });
        profile.Layers![0].Id = "overlay-2";
        using var mount = new MountInstance(profile);
        var hidden = mount.ApplyLayerAction("overlay-2", "file", LayerActions.Hide);
        Assert.IsFalse(hidden.Found);
        Assert.IsTrue(hidden.Layers[0].DirectTombstone);
        Assert.IsTrue(mount.ApplyLayerAction("overlay-2", "file", LayerActions.Restore).Found);
    }

    [TestMethod]
    public void Cache_fill_uses_combined_generation_and_is_reused()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Sub("a"));
        Directory.CreateDirectory(temp.Sub("b"));
        File.WriteAllText(temp.Sub("b", "file"), "cached");
        var profile = Profile(temp,
            new() { Id = "cache", Type = "cache", CacheCapacityBytes = 1048576 },
            new() { Id = "dir-a", Type = "dir", Source = new() { Type = "dir", Path = temp.Sub("a") } },
            new() { Id = "dir-b", Type = "dir", Source = new() { Type = "dir", Path = temp.Sub("b") } });
        using var mount = new MountInstance(profile);
        using (var first = new StreamReader(mount.Vfs.OpenRead("file", out _))) Assert.AreEqual("cached", first.ReadToEnd());
        Assert.AreEqual("cache", mount.Probe("file").EffectiveLayer);
        Assert.AreEqual(6L, mount.GetStatus().CacheBytes);
        using (var second = new StreamReader(mount.Vfs.OpenRead("file", out _))) Assert.AreEqual("cached", second.ReadToEnd());
        Assert.AreEqual(50.0, mount.GetMetrics(60).CacheHitPercent);
        mount.ClearCache();
        Assert.AreEqual("dir-b", mount.Probe("file").EffectiveLayer);
    }

    [TestMethod]
    public void Arbitrary_cache_position_is_allowed_but_duplicate_ids_are_rejected()
    {
        var profile = new MountProfile { Layers = new()
        {
            new() { Id = "cache", Type = "cache" }, new() { Id = "overlay", Type = "overlay" },
            new() { Id = "dir", Type = "dir", Source = new() { Type = "dir", Path = "C:\\source" } }
        } };
        profile.ValidateLayers();
        profile.Layers.RemoveAt(0);
        profile.Layers[1].Id = "overlay";
        Assert.ThrowsException<ArgumentException>(profile.ValidateLayers);
    }

    [TestMethod]
    public void Layer_configuration_roundtrips_credentials_capacity_and_order()
    {
        var config = new AppConfig { Mounts = new() { new() { Layers = new()
        {
            new() { Id = "overlay", Type = "overlay", Color = "#2563EB" },
            new() { Id = "cache", Type = "cache", CacheCapacityBytes = 123456 },
            new() { Id = "svn", Type = "svn", Source = new() { Url = "https://example.com/repo", Username = "user", Password = "test-password" } }
        } } } };
        var layers = AppConfig.FromJson(config.ToJson()).Mounts[0].Layers!;
        Assert.AreEqual(123456L, layers[1].CacheCapacityBytes);
        Assert.AreEqual("test-password", layers[2].Source.Password);
        CollectionAssert.AreEqual(new[] { "overlay", "cache", "svn" }, layers.Select(l => l.Id).ToArray());
    }

    [TestMethod]
    public void Lower_tombstone_does_not_hide_an_upper_overlay_file()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Sub("source"));
        File.WriteAllText(temp.Sub("source", "file"), "source");
        var profile = Profile(temp,
            new() { Id = "overlay-lower", Type = "overlay" },
            new() { Id = "dir", Type = "dir", Source = new() { Type = "dir", Path = temp.Sub("source") } });
        using var mount = new MountInstance(profile);
        mount.ApplyLayerAction("overlay-lower", "file", LayerActions.Hide);
        using (var write = mount.Vfs.OpenWrite("file", true, out _)) write.Write("upper"u8);
        Assert.IsFalse(mount.Vfs.IsDeleted("file"));
        Assert.IsTrue(mount.Vfs.List("").Any(e => e.Name == "file"));
        using var reader = new StreamReader(mount.Vfs.OpenRead("file", out _));
        Assert.AreEqual("upper", reader.ReadToEnd());
    }

    [TestMethod]
    public void Disabled_source_is_not_constructed_and_its_parameters_are_retained()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Sub("source"));
        File.WriteAllText(temp.Sub("source", "file"), "active");
        var profile = Profile(temp,
            new() { Id = "svn-disabled", Type = "svn", Enabled = false, Name = "Archived repository", Source = new() { Url = "" } },
            new() { Id = "dir", Type = "dir", Source = new() { Type = "dir", Path = temp.Sub("source") } });
        using var mount = new MountInstance(profile);
        CollectionAssert.AreEqual(new[] { "overlay", "dir" }, mount.Vfs.Layers.Select(l => l.Name).ToArray());
        using var reader = new StreamReader(mount.Vfs.OpenRead("file", out _));
        Assert.AreEqual("active", reader.ReadToEnd());
        var stored = AppConfig.FromJson(new AppConfig { Mounts = new() { profile } }.ToJson()).Mounts[0].Layers![1];
        Assert.IsFalse(stored.Enabled);
        Assert.AreEqual("Archived repository", stored.Name);
    }

    [TestMethod]
    public void Disabled_writable_layers_are_skipped_and_active_chain_is_validated()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Sub("source"));
        var profile = Profile(temp,
            new() { Id = "overlay-active", Type = "overlay", Name = "Working changes" },
            new() { Id = "dir", Type = "dir", Source = new() { Type = "dir", Path = temp.Sub("source") } });
        profile.Layers![0].Enabled = false;
        using (var mount = new MountInstance(profile))
        {
            using (var write = mount.Vfs.OpenWrite("file", true, out _)) write.Write("active"u8);
            Assert.AreEqual("overlay-active", mount.Probe("file").EffectiveLayer);
            Assert.AreEqual("Working changes", mount.GetStatus().Providers[0].DisplayName);
        }
        profile.Layers[1].Enabled = false;
        profile.ValidateLayers();
        using var readOnly = new MountInstance(profile);
        Assert.IsFalse(readOnly.Vfs.Writable);
        Assert.ThrowsException<UnauthorizedAccessException>(() => readOnly.Vfs.OpenWrite("file", true, out _));
    }

    [TestMethod]
    public void Empty_and_overlay_only_chains_are_valid()
    {
        using var temp = new TempDir();
        var profile = new MountProfile { Name = "empty", StoreRoot = temp.Sub("empty"), Layers = new() };
        profile.ValidateLayers();
        using (var empty = new MountInstance(profile))
        {
            Assert.IsTrue(empty.Vfs.TryLookup("", out var root));
            Assert.IsTrue(root.IsDir);
            Assert.AreEqual(0, empty.Vfs.List("").Count);
            Assert.IsFalse(empty.Vfs.Writable);
            Assert.IsNull(empty.Source);
        }
        profile.Layers.Add(new() { Id = "only-local", Type = "overlay" });
        using var local = new MountInstance(profile);
        using (var write = local.Vfs.OpenWrite("file", true, out _)) write.Write("local"u8);
        using var reader = new StreamReader(local.Vfs.OpenRead("file", out _));
        Assert.AreEqual("local", reader.ReadToEnd());
    }

    [TestMethod]
    public void Readonly_source_above_writable_layer_keeps_configured_priority()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Sub("source"));
        File.WriteAllText(temp.Sub("source", "file"), "top");
        var profile = new MountProfile { StoreRoot = temp.Sub("store"), Layers = new()
        {
            new() { Id = "top", Type = "dir", Source = new() { Path = temp.Sub("source") } },
            new() { Id = "bottom", Type = "overlay" }
        } };
        using var mount = new MountInstance(profile);
        using (var write = mount.Vfs.OpenWrite("file", true, out _)) write.Write("lower"u8);
        using var reader = new StreamReader(mount.Vfs.OpenRead("file", out _));
        Assert.AreEqual("top", reader.ReadToEnd());
        Assert.AreEqual("top", mount.Probe("file").EffectiveLayer);
    }

    [TestMethod]
    public void Cache_above_mutable_layer_does_not_retain_stale_content()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Sub("source"));
        File.WriteAllText(temp.Sub("source", "file"), "source");
        var profile = new MountProfile { StoreRoot = temp.Sub("store"), Layers = new()
        {
            new() { Id = "cache", Type = "cache" }, new() { Id = "overlay", Type = "overlay" },
            new() { Id = "dir", Type = "dir", Source = new() { Path = temp.Sub("source") } }
        } };
        using var mount = new MountInstance(profile);
        using (var read = mount.Vfs.OpenRead("file", out _)) { }
        Assert.AreEqual(0L, mount.GetStatus().CacheBytes);
        using (var write = mount.Vfs.OpenWrite("file", true, out _)) write.Write("changed"u8);
        using (var reader = new StreamReader(mount.Vfs.OpenRead("file", out _))) Assert.AreEqual("changed", reader.ReadToEnd());
        mount.Vfs.Delete("file");
        Assert.IsFalse(mount.Vfs.TryLookup("file", out _));
    }

    [TestMethod]
    public void Registered_custom_layer_can_be_the_entire_chain()
    {
        using var temp = new TempDir();
        LayerRegistry.Register("custom-test", (_, _) => { var layer = new FakeSourceLayer(); layer.AddFile("file", "custom"); return layer; });
        var profile = new MountProfile { StoreRoot = temp.Sub("store"), Layers = new() { new() { Id = "custom", Type = "custom-test" } } };
        using var mount = new MountInstance(profile);
        using var reader = new StreamReader(mount.Vfs.OpenRead("file", out _));
        Assert.AreEqual("custom", reader.ReadToEnd());
    }

    [TestMethod]
    public void Metrics_include_idle_layers_and_compute_share_without_cache()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Sub("a"));
        Directory.CreateDirectory(temp.Sub("b"));
        File.WriteAllText(temp.Sub("a", "first"), "a");
        File.WriteAllText(temp.Sub("b", "second"), "b");
        var profile = new MountProfile { StoreRoot = temp.Sub("store"), Layers = new()
        {
            new() { Id = "first", Name = "First source", Type = "dir", Source = new() { Path = temp.Sub("a") } },
            new() { Id = "second", Type = "dir", Source = new() { Path = temp.Sub("b") } }
        } };
        using var mount = new MountInstance(profile);
        var idle = mount.GetMetrics(60);
        Assert.AreEqual(2, idle.Layers.Count);
        Assert.IsTrue(idle.Layers.All(l => l.Hits == 0 && l.SharePercent == 0));
        using (mount.Vfs.OpenRead("first", out _)) { }
        using (mount.Vfs.OpenRead("second", out _)) { }
        var metrics = mount.GetMetrics(60);
        Assert.IsTrue(metrics.Layers.All(l => l.Hits == 1 && l.SharePercent == 50));
        Assert.AreEqual("First source", metrics.Layers[0].DisplayName);
    }

    private static MountProfile Profile(TempDir temp, params LayerConfig[] sources) => new()
    {
        Name = "test", MountPoint = temp.Sub("mount"), StoreRoot = temp.Sub("store"),
        Layers = new[] { new LayerConfig { Id = "overlay", Type = "overlay" } }.Concat(sources).ToList()
    };
}
