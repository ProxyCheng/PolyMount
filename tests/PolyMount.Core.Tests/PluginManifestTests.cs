using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Core.Ipc;
namespace PolyMount.Core.Tests;

/// <summary>The two things the host checks before it trusts a plugin: what the manifest says,
/// and which contract version the code was built against.
///
/// Both exist to turn a failure that would happen somewhere deep — a type that will not load, a
/// row with no name in a list — into something a person can act on before any of the plugin's code
/// runs.
/// </summary>
[TestClass]
public class PluginManifestTests
{
    [TestMethod]
    public void A_plugin_records_the_contract_it_was_built_against()
    {
        // What the SDK stamps into every plugin assembly, read back the way the host reads it.
        var declared = typeof(PolyMount.Plugins.IPolyMountPlugin).Assembly
            .GetCustomAttributes(typeof(PolyMount.Plugins.PluginApiAttribute), false)
            .Cast<PolyMount.Plugins.PluginApiAttribute>()
            .ToArray();
        Assert.AreEqual(1, declared.Length, "the contract assembly declares its own version, once");
        Assert.AreEqual(PolyMount.Plugins.PluginApi.Version, declared[0].Version);
    }

    [TestMethod]
    public void Compatibility_is_the_major_and_a_minor_this_host_has_reached()
    {
        Assert.IsTrue(PluginApi.IsCompatible(PluginApi.Major + ".0"), "an older minor is a subset of this contract");
        Assert.IsTrue(PluginApi.IsCompatible(PluginApi.Version), "a plugin built against this host's own contract");
        // Another major is another contract. Relative too, for the same reason: a bump must not
        // have to remember this line.
        Assert.IsFalse(PluginApi.IsCompatible((PluginApi.Major + 1) + ".0"), "another major is another contract");
        // A minor this host has not reached: its members may not be here. Relative to the version
        // the contract declares, so raising the minor does not have to remember this line.
        string newer = PluginApi.Major + "." + (PluginApi.Minor + 1);
        Assert.IsFalse(PluginApi.IsCompatible(newer),
            "a plugin built against a newer minor uses members this host has not got");
        Assert.IsFalse(PluginApi.IsCompatible(""));
        Assert.IsFalse(PluginApi.IsCompatible("nonsense"));

        var (major, minor) = PluginApi.Parse("3.4.9");
        Assert.AreEqual(3, major);
        Assert.AreEqual(4, minor, "only the first two numbers are the contract's version");
    }

    [TestMethod]
    public void The_contract_version_is_read_from_the_file_without_loading_it()
    {
        // What the plugin page needs for a plugin the service will not load — disabled, or built
        // against another major. The contract assembly carries the same attribute a plugin gets.
        string contract = typeof(PolyMount.Plugins.IPolyMountPlugin).Assembly.Location;
        Assert.AreEqual(PolyMount.Plugins.PluginApi.Version, PluginLoader.ReadApiVersion(contract),
            "the file's recorded contract version is not read back");

        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");
        Assert.AreEqual("", PluginLoader.ReadApiVersion(missing), "a file that is not there is not an error");
        Assert.AreEqual("", PluginLoader.ReadApiVersion(contract + ".not-an-assembly"),
            "a file that is not an assembly is not an error");
    }

    [TestMethod]
    public void A_mismatch_is_explained_rather_than_left_to_the_type_loader()
    {
        string other = (PluginApi.Major + 1) + ".0";
        string? mismatch = PluginLoader.ApiMismatch(other);
        Assert.IsNotNull(mismatch);
        StringAssert.Contains(mismatch, "contract " + other);
        StringAssert.Contains(mismatch, PluginApi.Version);
        StringAssert.Contains(mismatch, "PolyMount.Plugin.Abstractions");

        Assert.IsNull(PluginLoader.ApiMismatch(PluginApi.Version));
        Assert.IsNull(PluginLoader.ApiMismatch(""), "a plugin that records no version is left to the type-load message");
    }

    [TestMethod]
    public void A_manifest_is_read_from_beside_the_assembly_and_named_after_it()
    {
        using var temp = new TempDir();
        string assembly = Path.Combine(temp.Path, "PolyMount.Plugin.Example.dll");
        File.WriteAllText(assembly, "");
        Assert.IsNull(PluginManifest.Read(assembly, out string missing));
        Assert.AreEqual("", missing, "no manifest is not an error: that is what a legacy plugin looks like");

        Assert.AreEqual(Path.Combine(temp.Path, "PolyMount.Plugin.Example.plugin.json"), PluginManifest.PathFor(assembly));
        File.WriteAllText(PluginManifest.PathFor(assembly), """
            {
              "schemaVersion": 1,
              "id": "example.plugin",
              "name": { "en": "Example", "zh-CN": "示例" },
              "description": { "en": "Does a thing.", "zh-CN": "做一件事。" },
              "icon": "folder",
              "permissions": ["network", "registry"],
              "host": ">=0.9.5"
            }
            """);

        var manifest = PluginManifest.Read(assembly, out string error);
        Assert.AreEqual("", error);
        Assert.IsNotNull(manifest);
        Assert.AreEqual("example.plugin", manifest.Id);
        Assert.AreEqual("Example", manifest.Title.Resolve("en"));
        Assert.AreEqual("示例", manifest.Title.Resolve("zh-CN"));
        Assert.AreEqual("folder", manifest.Icon);
        CollectionAssert.AreEqual(new[] { "network", "registry" }, manifest.Permissions);
        Assert.AreEqual(">=0.9.5", manifest.HostRange);
    }

    [TestMethod]
    public void A_manifest_that_cannot_be_used_is_a_refusal_not_a_half_load()
    {
        using var temp = new TempDir();
        string assembly = Path.Combine(temp.Path, "PolyMount.Plugin.Broken.dll");
        File.WriteAllText(assembly, "");
        string manifestPath = PluginManifest.PathFor(assembly);

        File.WriteAllText(manifestPath, """{ "schemaVersion": 1, "id": "" }""");
        Assert.IsNull(PluginManifest.Read(assembly, out string noId));
        StringAssert.Contains(noId, "no id");

        File.WriteAllText(manifestPath, """{ "schemaVersion": 9, "id": "future" }""");
        Assert.IsNull(PluginManifest.Read(assembly, out string schema));
        StringAssert.Contains(schema, "schemaVersion 9");

        File.WriteAllText(manifestPath, "{ this is not json");
        Assert.IsNull(PluginManifest.Read(assembly, out string broken));
        StringAssert.Contains(broken, "plugin.json");

        // Reported through the loader as an error row, before the assembly is touched at all.
        var report = PluginLoader.LoadConfiguredDetailed(
            new[] { new PluginConfig { AssemblyPath = "PolyMount.Plugin.Broken.dll", Enabled = true } }, temp.Path);
        Assert.AreEqual(1, report.Errors.Count, string.Join("; ", report.Errors));
        Assert.IsFalse(report.Plugins[0].Loaded);
        Assert.IsNotNull(report.Plugins[0].ManifestError);
    }

    [TestMethod]
    public void A_disabled_plugin_is_described_by_its_manifest_without_being_loaded()
    {
        using var temp = new TempDir();
        string directory = Path.Combine(temp.Path, "plugins");
        Directory.CreateDirectory(directory);
        // A copy of a real assembly that is not a plugin: it registers nothing, but its manifest
        // is what the row is built from, and a disabled entry is never loaded at all.
        string assembly = Path.Combine(directory, "PolyMount.Plugin.Quiet.dll");
        File.Copy(typeof(PluginLoader).Assembly.Location, assembly);
        File.WriteAllText(PluginManifest.PathFor(assembly), """
            { "schemaVersion": 1, "id": "quiet", "name": { "en": "Quiet" }, "permissions": ["filesystem"] }
            """);

        var report = PluginLoader.LoadConfiguredDetailed(
            new[] { new PluginConfig { AssemblyPath = "plugins/PolyMount.Plugin.Quiet.dll", Enabled = false } }, temp.Path);

        Assert.AreEqual(0, report.Errors.Count);
        var row = report.Plugins[0];
        Assert.IsFalse(row.Loaded);
        Assert.IsNotNull(row.Manifest);
        Assert.AreEqual("quiet", row.Manifest.Id);
        Assert.AreEqual("Quiet", row.Title?.Resolve("en"), "the list can name it while it is off");
        CollectionAssert.AreEqual(new[] { "filesystem" }, row.Manifest.Permissions);
    }
}
