using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;

namespace PolyMount.Core.Tests;

/// <summary A plugin is one assembly for several processes. These check that a declaration of
/// where its code belongs is read, refused when it makes no sense, and acted on — a front-end
/// must not load a source it can never reach, and a worker must not load a plugin that only
/// offers tools.
[TestClass]
public class PluginRoleTests
{
    [TestMethod]
    public void A_manifest_that_says_nothing_means_both_halves()
    {
        Assert.IsTrue(PluginRoleNames.TryParse(null, out var absent, out _));
        Assert.AreEqual(PluginRoles.Both, absent, "every plugin written before roles existed says nothing");

        Assert.IsTrue(PluginRoleNames.TryParse(Array.Empty<string>(), out var empty, out _));
        Assert.AreEqual(PluginRoles.Both, empty);

        Assert.IsTrue(PluginRoleNames.TryParse(new[] { "backend" }, out var backend, out _));
        Assert.AreEqual(PluginRoles.Backend, backend);
        Assert.IsTrue(PluginRoleNames.TryParse(new[] { "Backend", " FRONTEND " }, out var both, out _));
        Assert.AreEqual(PluginRoles.Both, both);
    }

    [TestMethod]
    public void A_role_the_host_does_not_know_is_refused_rather_than_ignored()
    {
        Assert.IsFalse(PluginRoleNames.TryParse(new[] { "backend", "desktop" }, out _, out string error));
        StringAssert.Contains(error, "desktop");
        StringAssert.Contains(error, "backend");
    }

    [TestMethod]
    public void A_manifest_with_an_unknown_role_fails_to_read()
    {
        using var temp = new TempDir();
        string assembly = Path.Combine(temp.Path, "PolyMount.Plugin.Odd.dll");
        File.WriteAllText(assembly, "");
        File.WriteAllText(PluginManifest.PathFor(assembly),
            """{ "schemaVersion": 1, "id": "odd", "roles": ["elsewhere"] }""");

        Assert.IsNull(PluginManifest.Read(assembly, out string error));
        StringAssert.Contains(error, "elsewhere");
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PolyMount.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("the repository root was not found");
    }

    [TestMethod]
    public void A_process_loads_only_the_plugins_that_declare_its_half()
    {
        using var temp = new TempDir();
        string directory = Path.Combine(temp.Path, "plugins");
        Directory.CreateDirectory(directory);
        // The puppet plugin: a real plugin, built for the tests, that registers a layer type and
        // declares backend only (tests/plugins/PolyMount.Plugin.Puppet).
        string source = typeof(PolyMount.Plugin.Puppet.PuppetPlugin).Assembly.Location;
        string assembly = Path.Combine(directory, Path.GetFileName(source));
        File.Copy(source, assembly);
        // The manifest is copied beside the plugin in the plugin's own build, not into this test's
        // output: a project that references a plugin gets its assembly, not the files a deployment
        // puts next to it. So it is taken from the fixture's source, which is what ships.
        File.Copy(Path.Combine(RepoRoot(), "tests", "plugins", "PolyMount.Plugin.Puppet", "plugin.json"),
            PluginManifest.PathFor(assembly));
        var config = new[] { new PluginConfig { AssemblyPath = "plugins/" + Path.GetFileName(assembly), Enabled = true } };

        var worker = PluginLoader.LoadConfiguredDetailed(config, temp.Path, PluginRoles.Backend);
        Assert.AreEqual(0, worker.Errors.Count, string.Join("; ", worker.Errors));
        Assert.IsTrue(worker.Plugins[0].Loaded, "a worker needs it");
        Assert.IsFalse(worker.Plugins[0].Skipped);
        Assert.AreEqual(PluginRoles.Backend, worker.Plugins[0].Roles);
        Assert.IsTrue(PolyMount.Core.LayerRegistry.Contains("puppet"));

        var frontEnd = PluginLoader.LoadConfiguredDetailed(config, temp.Path, PluginRoles.Frontend);
        Assert.AreEqual(0, frontEnd.Errors.Count, "not needing it is not a failure");
        Assert.IsFalse(frontEnd.Plugins[0].Loaded);
        Assert.IsTrue(frontEnd.Plugins[0].Skipped, "and the row says why, instead of looking broken");
        Assert.IsFalse(PolyMount.Core.LayerRegistry.Contains("puppet"), "its layer type is gone with it");

        // The supervisor asks for both, so the list it reports describes everything installed.
        var supervisor = PluginLoader.LoadConfiguredDetailed(config, temp.Path, PluginRoles.Both);
        Assert.IsTrue(supervisor.Plugins[0].Loaded);
        Assert.IsFalse(supervisor.Plugins[0].Skipped);
    }
}
