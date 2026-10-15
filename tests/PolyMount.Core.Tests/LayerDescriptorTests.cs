using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core.Ipc;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class LayerDescriptorTests
{
    [TestMethod]
    public void Metadata_roundtrips_through_ipc_without_frontend_types()
    {
        var descriptor = new LayerDescriptor
        {
            Type = "remote-test", Title = new LocalizedText { Values = { ["en"] = "Remote", ["fr"] = "À distance" } },
            Fields = new() { new() { Path = "Parameters.Mode", Kind = LayerFieldKind.Choice,
                Choices = new() { new() { Value = "fast",
                    Title = new LocalizedText { Values = { ["en"] = "Fast", ["fr"] = "Rapide" } } } } } }
        };
        var response = JsonSerializer.Deserialize<ControlResponse>(JsonSerializer.Serialize(new ControlResponse { LayerDescriptors = new() { descriptor } }))!;
        var restored = response.LayerDescriptors.Single();
        Assert.AreEqual("À distance", restored.Title.Resolve("fr"));
        Assert.AreEqual("Remote", restored.Title.Resolve("de"));
        Assert.AreEqual(LayerFieldKind.Choice, restored.Fields[0].Kind);
        Assert.AreEqual("fast", restored.Fields[0].Choices[0].Value);
    }

    [TestMethod]
    public void Plugin_parameters_and_legacy_fields_bind_without_losing_unedited_values()
    {
        var layer = new LayerConfig { Source = new() { Url = "keep-url", Password = "old" },
            Parameters = new() { ["Unedited"] = JsonSerializer.SerializeToElement(123) } };
        var endpoint = new LayerFieldDescriptor { Path = "Source.Parameters.Endpoint" };
        var mode = new LayerFieldDescriptor { Path = "Parameters.Mode", Kind = LayerFieldKind.Choice,
            Choices = new() { new() { Value = "fast" } } };
        var secret = new LayerFieldDescriptor { Path = "Source.Password", Kind = LayerFieldKind.Secret, Trim = false, EmptyIsNull = true };
        LayerProperties.Apply(layer, new[] { (endpoint, " https://example.invalid "), (mode, "fast"), (secret, " secret with spaces ") });
        var restored = AppConfig.FromJson(new AppConfig { Mounts = new() { new() { Layers = new() { layer } } } }.ToJson()).Mounts[0].Layers![0];
        Assert.AreEqual("https://example.invalid", LayerProperties.Read(restored, endpoint));
        Assert.AreEqual("fast", LayerProperties.Read(restored, mode));
        Assert.AreEqual(" secret with spaces ", restored.Source.Password);
        Assert.AreEqual("keep-url", restored.Source.Url);
        Assert.AreEqual(123, restored.Parameters["Unedited"].GetInt32());
        LayerProperties.Apply(layer, new[] { (secret, "") });
        Assert.IsNull(layer.Source.Password);
    }

    [TestMethod]
    public void Integer_validation_is_atomic_and_rejects_overflow()
    {
        var layer = new LayerConfig { Path = "original", CacheCapacityBytes = 1048576 };
        var path = new LayerFieldDescriptor { Path = "Path" };
        var capacity = LayerRegistry.Describe("cache").Fields.Single(f => f.Path == "CacheCapacityBytes");
        Assert.ThrowsException<ArgumentException>(() => LayerProperties.Apply(layer, new[] { (path, "changed"), (capacity, long.MaxValue.ToString()) }));
        Assert.AreEqual("original", layer.Path);
        Assert.AreEqual(1048576L, layer.CacheCapacityBytes);
        LayerProperties.Apply(layer, new[] { (capacity, "32") });
        Assert.AreEqual(32L * 1048576, layer.CacheCapacityBytes);
        Assert.AreEqual("32", LayerProperties.Read(layer, capacity));
        Assert.ThrowsException<ArgumentException>(() => LayerProperties.Apply(layer, new[] { (capacity, "0") }));
        layer.Enabled = false;
        LayerProperties.Apply(layer, new[] { (capacity, "invalid") });
        Assert.AreEqual(32L * 1048576, layer.CacheCapacityBytes);
    }

    [TestMethod]
    public void Required_boolean_and_choice_fields_validate_generically()
    {
        var layer = new LayerConfig();
        var path = new LayerFieldDescriptor { Path = "Source.Path", Required = true };
        Assert.ThrowsException<ArgumentException>(() => LayerProperties.Apply(layer, new[] { (path, " ") }));
        var flag = new LayerFieldDescriptor { Path = "Source.Writable", Kind = LayerFieldKind.Boolean };
        LayerProperties.Apply(layer, new[] { (flag, "True") });
        Assert.IsTrue(layer.Source.Writable);
        var choice = new LayerFieldDescriptor { Path = "Parameters.Mode", Kind = LayerFieldKind.Choice,
            Choices = new() { new() { Value = "fast" } } };
        Assert.ThrowsException<ArgumentException>(() => LayerProperties.Apply(layer, new[] { (choice, "unknown") }));
    }
}
