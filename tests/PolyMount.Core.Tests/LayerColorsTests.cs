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
        var colors = new[] { "overlay", "cache", "svn", "dir", "memory-cache", "prediction-cache", "warmup" }.Select(LayerColors.Auto).ToArray();
        CollectionAssert.AllItemsAreUnique(colors);
        foreach (string color in colors) CollectionAssert.Contains(LayerColors.OriginPalette.ToArray(), color);
    }

    [TestMethod]
    public void Builtin_layers_cover_the_whole_palette_at_most_once()
    {
        // Explorer can only draw the palette's colours exactly, and every entry costs one of the
        // machine-wide overlay slots, so the palette is bounded (see the next test). Entries left
        // over are not waste: they are what an unknown layer type hashes into, which is how a
        // plugin layer gets a dot of its own instead of borrowing a built-in layer's.
        var colors = new[] { "overlay", "cache", "svn", "dir", "memory-cache", "prediction-cache", "warmup" }
            .Select(LayerColors.Auto).ToArray();
        CollectionAssert.AllItemsAreUnique(colors);
        foreach (string color in colors) CollectionAssert.Contains(LayerColors.OriginPalette.ToArray(), color);
    }

    [TestMethod]
    public void Palette_stays_within_the_overlay_slots_explorer_can_take()
    {
        // One palette entry = one COM handler = one Windows overlay slot, and the system shares
        // about fifteen of them with every other application (OneDrive and TortoiseSVN are in the
        // same list on a typical machine). Ten also happens to be where the installer's class id
        // runs out: it appends the decimal slot index to 0x7d619ac, which stays a valid GUID only
        // while the index is a single digit. Raising this means changing both.
        Assert.IsTrue(LayerColors.OriginPalette.Count is > 0 and <= 10,
            $"palette has {LayerColors.OriginPalette.Count} entries; the Explorer overlay budget stops at ten");
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
        var icons = new[] { "overlay", "cache", "dir", "memory-cache", "prediction-cache", "warmup" }
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

/// <summary>The Explorer badge's vocabulary: one glyph per palette colour, in slot order.</summary>
[TestClass]
public class OverlayGlyphsTests
{
    [TestMethod]
    public void Every_slot_has_one_glyph_and_one_colour()
    {
        // A badge is a glyph drawn in a colour, and both are decided by the slot index: the icon
        // file for slot n is the nth glyph painted in the nth palette colour.
        Assert.AreEqual(LayerColors.OriginPalette.Count, OverlayGlyphs.Names.Count,
            "OriginPalette.inc and OverlayGlyphs.inc must name one entry per slot");
    }

    [TestMethod]
    public void Each_builtin_layer_wears_its_glyph_in_its_own_colour()
    {
        // svn is not in this list: its descriptor arrives with the plugin, which a unit test host
        // does not load (the next test pins its glyph by name instead).
        var types = new[] { "overlay", "cache", "dir", "prediction-cache", "memory-cache", "warmup" };
        foreach (string type in types)
        {
            int slot = OverlayGlyphs.SlotOf(LayerRegistry.Describe(type).Icon);
            Assert.AreEqual(LayerColors.Auto(type), LayerColors.OriginPalette[slot],
                $"{type} maps to badge slot {slot}, whose colour belongs to another layer");
        }
    }

    [TestMethod]
    public void The_plugin_sources_keep_a_slot_of_their_own()
    {
        // The svn source's icon is declared by its plugin; the badge list must still name it, or
        // versioned files would fall back to the generic mark.
        int slot = OverlayGlyphs.SlotOf("repository");
        Assert.AreEqual("repository", OverlayGlyphs.Names[slot], "svn has no badge of its own");
        Assert.AreEqual(LayerColors.Auto("svn"), LayerColors.OriginPalette[slot]);
    }

    [TestMethod]
    public void A_layer_with_no_glyph_wears_the_generic_badge()
    {
        int generic = OverlayGlyphs.Names.Count - 1;
        Assert.AreEqual(generic, OverlayGlyphs.SlotOf(""), "a descriptor that names no icon");
        Assert.AreEqual(generic, OverlayGlyphs.SlotOf(null), "no descriptor at all");
        Assert.AreEqual(generic, OverlayGlyphs.SlotOf("something-else"), "an icon the badges do not draw");
        Assert.AreEqual(OverlayGlyphs.Generic, OverlayGlyphs.Names[generic]);
    }

    [TestMethod]
    public void Glyph_names_are_case_insensitive_and_unique()
    {
        CollectionAssert.AllItemsAreUnique(OverlayGlyphs.Names.ToArray());
        Assert.AreEqual(OverlayGlyphs.SlotOf("folder"), OverlayGlyphs.SlotOf("Folder"));
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
