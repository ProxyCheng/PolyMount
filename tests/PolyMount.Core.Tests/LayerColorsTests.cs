using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount;
using PolyMount.Core;

namespace PolyMount.Core.Tests;

[TestClass]
public class LayerColorsTests
{
    [TestMethod]
    public void Auto_is_deterministic_and_valid_hex()
    {
        var a = LayerColors.Auto("mylayer");
        var b = LayerColors.Auto("mylayer");
        Assert.AreEqual(a, b, "auto color must be stable across calls/processes");
        Assert.IsTrue(LayerColors.TryNormalizeHex(a, out _), "auto color must be valid #RRGGBB");
    }

    [TestMethod]
    public void Builtin_layers_have_distinct_preset_colors()
    {
        var colors = new[] { "overlay", "cache", "svn", "dir", "memory-cache", "prediction-cache" }.Select(LayerColors.Auto).ToArray();
        CollectionAssert.AllItemsAreUnique(colors);
        foreach (string color in colors) CollectionAssert.Contains(LayerColors.OriginPalette.ToArray(), color);
    }

    [TestMethod]
    public void Unknown_layer_defaults_are_exact_palette_colors_and_case_insensitive()
    {
        for (int i = 0; i < 100; i++)
        {
            string type = "plugin-layer-" + i;
            CollectionAssert.Contains(LayerColors.OriginPalette.ToArray(), LayerColors.Auto(type));
            Assert.AreEqual(LayerColors.Auto(type), LayerColors.Auto(type.ToUpperInvariant()));
        }
    }

    [TestMethod]
    public void Builtin_layer_descriptors_use_distinct_semantic_icons()
    {
        var icons = new[] { "overlay", "cache", "dir", "memory-cache", "prediction-cache" }
            .Select(type => LayerRegistry.Describe(type).Icon).ToArray();
        CollectionAssert.AllItemsAreUnique(icons);
        Assert.IsFalse(icons.Contains("layers"));
    }

    [TestMethod]
    public void Override_wins_when_valid()
    {
        var overrides = new Dictionary<string, string> { ["svn"] = "#abcdef" };
        Assert.AreEqual("#ABCDEF", LayerColors.Resolve("svn", overrides));
    }

    [TestMethod]
    public void Invalid_override_falls_back_to_auto()
    {
        var overrides = new Dictionary<string, string> { ["svn"] = "not-a-color" };
        Assert.AreEqual(LayerColors.Auto("svn"), LayerColors.Resolve("svn", overrides));
    }

    [TestMethod]
    public void TryNormalizeHex_accepts_with_or_without_hash()
    {
        Assert.IsTrue(LayerColors.TryNormalizeHex("1a2b3c", out var a));
        Assert.AreEqual("#1A2B3C", a);
        Assert.IsTrue(LayerColors.TryNormalizeHex("#1a2b3c", out var b));
        Assert.AreEqual("#1A2B3C", b);
        Assert.IsFalse(LayerColors.TryNormalizeHex("#123", out _));
        Assert.IsFalse(LayerColors.TryNormalizeHex("zzzzzz", out _));
    }
}

[TestClass]
public class AppConfigTests
{
    [TestMethod]
    public void Json_roundtrip_preserves_mounts_and_colors()
    {
        var cfg = new AppConfig
        {
            Mounts =
            {
                new MountProfile
                {
                    Name = "M", MountPoint = @"D:\M", StoreRoot = @"D:\M.store",
                    Source = new SourceConfig { Type = "dir", Path = @"D:\src" }
                }
            },
            LayerColors = { ["svn"] = "#112233" }
        };

        var back = AppConfig.FromJson(cfg.ToJson());

        Assert.AreEqual(1, back.Mounts.Count);
        Assert.AreEqual("M", back.Mounts[0].Name);
        Assert.AreEqual("dir", back.Mounts[0].Source.Type);
        Assert.AreEqual("#112233", back.LayerColors["svn"]);
    }

    [TestMethod]
    public void Validate_rejects_duplicate_names()
    {
        var cfg = new AppConfig
        {
            Mounts =
            {
                new MountProfile { Name = "X", MountPoint = @"D:\a", StoreRoot = @"D:\a.s", Source = new SourceConfig { Type = "dir", Path = @"D:\s" } },
                new MountProfile { Name = "X", MountPoint = @"D:\b", StoreRoot = @"D:\b.s", Source = new SourceConfig { Type = "dir", Path = @"D:\s" } }
            }
        };
        Assert.ThrowsException<ArgumentException>(() => cfg.Validate());
    }

    [TestMethod]
    public void Validate_rejects_unknown_source_type()
    {
        var cfg = new AppConfig
        {
            Mounts = { new MountProfile { Name = "X", MountPoint = @"D:\a", Layers = new() { new() { Id = "unknown", Type = "nope" } } } }
        };
        Assert.ThrowsException<ArgumentException>(() => cfg.Validate());
    }

    [TestMethod]
    public void Validate_rejects_bad_color()
    {
        var cfg = new AppConfig { LayerColors = { ["svn"] = "####" } };
        Assert.ThrowsException<ArgumentException>(() => cfg.Validate());
    }
}
