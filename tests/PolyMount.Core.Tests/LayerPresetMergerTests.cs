using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class LayerPresetMergerTests
{
    [TestMethod]
    public void Existing_settings_and_extra_layers_are_preserved()
    {
        var source = new LayerConfig { Id = "repo", Type = "svn", Name = "My repository", Enabled = false,
            Color = "#123456", Path = "storage", Compression = true,
            Source = new() { Url = "https://example.invalid", Password = "secret", PinnedRevision = 42 },
            Parameters = new() { ["custom"] = JsonSerializer.SerializeToElement("keep") } };
        var overlay = new LayerConfig { Id = "writes", Type = "overlay", Path = "local-store" };
        var extra = new LayerConfig { Id = "extra", Type = "dir" };
        string before = JsonSerializer.Serialize(source);
        var preset = new LayerPreset { Layers = new() { new() { Id = "overlay", Type = "overlay" }, new() { Id = "svn", Type = "svn" } } };
        var result = LayerPresetMerger.Merge(new[] { source, extra, overlay }, preset);
        Assert.AreSame(overlay, result[0]);
        Assert.AreSame(source, result[1]);
        Assert.AreSame(extra, result[2]);
        Assert.AreEqual(before, JsonSerializer.Serialize(source));
    }

    [TestMethod]
    public void Exact_matches_are_reserved_before_same_type_fallback()
    {
        var exact = new LayerConfig { Id = "cache-b", Type = "CACHE", CacheCapacityBytes = 123 };
        var other = new LayerConfig { Id = "custom", Type = "cache", CacheCapacityBytes = 456 };
        var preset = new LayerPreset { Layers = new() { new() { Id = "cache-a", Type = "cache" }, new() { Id = "CACHE-B", Type = "cache" } } };
        var result = LayerPresetMerger.Merge(new[] { exact, other }, preset);
        Assert.AreSame(other, result[0]);
        Assert.AreSame(exact, result[1]);
    }

    [TestMethod]
    public void Missing_layers_are_cloned_with_unique_ids_and_reapplying_is_idempotent()
    {
        var existing = new LayerConfig { Id = "cache", Type = "overlay" };
        var template = new LayerConfig { Id = "cache", Type = "cache", Parameters = new() { ["value"] = JsonSerializer.SerializeToElement(1) } };
        var preset = new LayerPreset { Layers = new() { template, new() { Id = "cache", Type = "cache" } } };
        var result = LayerPresetMerger.Merge(new[] { existing }, preset);
        CollectionAssert.AreEqual(new[] { "cache-2", "cache-3", "cache" }, result.Select(l => l.Id).ToArray());
        Assert.AreNotSame(template, result[0]);
        result[0].Parameters["value"] = JsonSerializer.SerializeToElement(2);
        Assert.AreEqual(1, template.Parameters["value"].GetInt32());
        var repeated = LayerPresetMerger.Merge(result, preset);
        CollectionAssert.AreEqual(result.ToArray(), repeated.ToArray());
    }

    [TestMethod]
    public void Empty_preset_keeps_existing_chain_and_empty_chain_copies_preset()
    {
        var existing = new LayerConfig { Id = "one" };
        Assert.AreSame(existing, LayerPresetMerger.Merge(new[] { existing }, new()).Single());
        var preset = new LayerPreset { Layers = new() { existing } };
        var result = LayerPresetMerger.Merge(Array.Empty<LayerConfig>(), preset);
        Assert.AreEqual("one", result.Single().Id);
        Assert.AreNotSame(existing, result.Single());
    }
}
