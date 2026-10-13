using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class LayerChainTests
{
    [TestMethod]
    public void Empty_plugin_configuration_and_disabled_missing_dll_do_not_load_anything()
    {
        Assert.AreEqual(0, PluginLoader.LoadConfigured(Array.Empty<PluginConfig>(), AppContext.BaseDirectory).Count);
        Assert.AreEqual(0, PluginLoader.LoadConfigured(new[] { new PluginConfig { AssemblyPath = "missing.dll", Enabled = false } }, AppContext.BaseDirectory).Count);
    }

    [TestMethod]
    public void Disabled_layers_are_not_constructed_and_empty_chain_is_valid()
    {
        using var temp = new TempDir();
        var profile = new MountProfile { StoreRoot = temp.Sub("store"), Layers = new() { new() { Id = "disabled", Type = "cache", Enabled = false, CacheCapacityBytes = -1 } } };
        using var mount = new MountInstance(profile);
        Assert.AreEqual(0, mount.Vfs.Layers.Count);
        Assert.AreEqual(0, mount.Vfs.List("").Count);
        Assert.IsTrue(mount.Vfs.TryLookup("", out _));
    }

    [TestMethod]
    public void Metrics_include_idle_layers_without_assuming_cache()
    {
        using var temp = new TempDir();
        var profile = new MountProfile { StoreRoot = temp.Sub("store"), Layers = new() { new() { Id = "local", Name = "Changes", Type = "overlay" } } };
        using var mount = new MountInstance(profile);
        var metrics = mount.GetMetrics(60);
        Assert.AreEqual("Changes", metrics.Layers.Single().DisplayName);
        Assert.AreEqual(0L, metrics.Layers.Single().Hits);
    }

    [TestMethod]
    public void Explicit_storage_path_and_compression_roundtrip()
    {
        var config = new AppConfig { Mounts = new() { new() { Layers = new() { new() { Id = "local", Type = "overlay", Path = "D:\\objects", Compression = true } } } } };
        var restored = AppConfig.FromJson(config.ToJson()).Mounts[0];
        Assert.IsTrue(restored.Layers![0].Compression);
        Assert.AreEqual("D:\\objects", restored.GetLayerStorage(restored.Layers[0]));
    }
}
