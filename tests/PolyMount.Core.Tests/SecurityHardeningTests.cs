using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount;
using PolyMount.Core;
using PolyMount.Core.Ipc;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

/// <summary>What the security review of the public tree found, pinned so it cannot come back
/// quietly: the names a path may have, the secrets a configuration may carry out of the service,
/// where a plugin may be loaded from, and what an instance identity may contain.</summary>
[TestClass]
public class SecurityHardeningTests
{
    // ---- #15: a path is a name, not a way out of the namespace ------------------------------

    [TestMethod]
    public void A_path_component_cannot_walk_out_of_the_namespace()
    {
        foreach (string bad in new[] { "..", "a/../b", ".", "a/./b", "a//b", "../a", "a/.." })
        {
            try { VfsPath.Normalize(bad); }
            catch (ArgumentException) { continue; }
            Assert.Fail("accepted: " + bad);
        }
    }

    [TestMethod]
    public void A_path_component_cannot_be_a_stream_or_a_device()
    {
        foreach (string bad in new[] { "a:b", "file.txt::$DATA", "dir/NUL", "CON", "con.txt", "COM1", "lpt9.log", "aux/file" })
        {
            try { VfsPath.Normalize(bad); }
            catch (ArgumentException) { continue; }
            Assert.Fail("accepted: " + bad);
        }
    }

    [TestMethod]
    public void An_ordinary_name_is_unchanged()
    {
        Assert.AreEqual("a/b/c.txt", VfsPath.Normalize("\\a\\b\\c.txt"));
        Assert.AreEqual("20260924", VfsPath.Normalize("/20260924/"));
        Assert.AreEqual("Engine/Tools_tracy", VfsPath.Normalize("Engine/Tools_tracy"));
        Assert.AreEqual("", VfsPath.Normalize("\\"));
        // "console" and "com10" are ordinary names: only the reserved spellings are devices.
        Assert.AreEqual("console.log", VfsPath.Normalize("console.log"));
        Assert.AreEqual("com10/x.txt", VfsPath.Normalize("com10/x.txt"));
    }

    // ---- #12: a configuration leaves the service with its secrets taken out -------------------

    [TestMethod]
    public void A_stored_password_does_not_travel_with_the_configuration()
    {
        string json = JsonSerializer.Serialize(Config(password: "hunter2", layerPassword: "layer-secret"));
        var redacted = JsonNode.Parse(ConfigSecrets.RedactJson(json))!;

        Assert.AreEqual("[redacted]", (string?)redacted["Mounts"]![0]!["Source"]!["Password"]);
        Assert.AreEqual("[redacted]", (string?)redacted["Mounts"]![0]!["Layers"]![0]!["Source"]!["Password"]);
        Assert.AreEqual("someone", (string?)redacted["Mounts"]![0]!["Source"]!["Username"], "only secrets are taken out");
    }

    [TestMethod]
    public void A_save_that_still_says_redacted_keeps_the_stored_password()
    {
        var current = JsonNode.Parse(JsonSerializer.Serialize(Config(password: "hunter2", layerPassword: "layer-secret")));
        // What a front-end sends back: the document it read, with one unrelated field changed.
        var incoming = JsonNode.Parse(ConfigSecrets.RedactJson(JsonSerializer.Serialize(Config(password: "hunter2", layerPassword: "layer-secret"))))!;
        incoming["Mounts"]![0]!["Name"] = "renamed";

        ConfigSecrets.RestoreRedacted(incoming, current);

        Assert.AreEqual("hunter2", (string?)incoming["Mounts"]![0]!["Source"]!["Password"]);
        Assert.AreEqual("layer-secret", (string?)incoming["Mounts"]![0]!["Layers"]![0]!["Source"]!["Password"]);
    }

    [TestMethod]
    public void A_redacted_password_with_nothing_behind_it_clears_the_field()
    {
        var incoming = JsonNode.Parse("""{"Mounts":[{"Id":"abc","Source":{"Password":"[redacted]"}}]}""")!;
        ConfigSecrets.RestoreRedacted(incoming, JsonNode.Parse("""{"Mounts":[]}"""));
        Assert.IsNull(incoming["Mounts"]![0]!["Source"]!["Password"]);
    }

    private static AppConfig Config(string password, string layerPassword) => new()
    {
        Mounts = new()
        {
            new MountProfile
            {
                Id = "abc123", Name = "m", MountPoint = @"D:\m",
                Source = new SourceConfig { Type = "svn", Url = "https://example.invalid/r", Username = "someone", Password = password },
                Layers = new()
                {
                    new LayerConfig
                    {
                        Id = "svn", Type = "svn",
                        Source = new SourceConfig { Type = "svn", Url = "https://example.invalid/r", Password = layerPassword }
                    }
                }
            }
        }
    };

    // ---- #2: a plugin is code from the installation, and from nowhere else -------------------

    [TestMethod]
    public void A_plugin_path_inside_the_host_is_accepted()
    {
        string host = Path.Combine(Path.GetTempPath(), "polymount-host");
        string? resolved = PluginLoader.ResolvePath("plugins/x.dll", host, out string error);
        Assert.IsNotNull(resolved, error);
        Assert.AreEqual("", error);
        // Inside the host as written, and outside it once canonicalised: this is the shape an
        // escape takes.
        Assert.IsNull(PluginLoader.ResolvePath("plugins/../../x.dll", host, out _));
    }

    [TestMethod]
    public void A_plugin_path_outside_the_host_is_refused()
    {
        string host = Path.Combine(Path.GetTempPath(), "polymount-host");
        foreach (string outside in new[] { @"C:\Windows\System32\payload.dll", @"..\payload.dll", @"\\server\share\payload.dll" })
        {
            Assert.IsNull(PluginLoader.ResolvePath(outside, host, out string error), "accepted: " + outside);
            Assert.IsTrue(error.Length > 0);
        }
        var errors = PluginLoader.CheckPaths(new[] { new PluginConfig { AssemblyPath = @"C:\payload.dll" } }, host);
        Assert.AreEqual(1, errors.Count);
    }

    // ---- #3: an instance identity is a token, because three names are derived from it --------

    [TestMethod]
    public void An_instance_identity_cannot_carry_a_path()
    {
        foreach (string bad in new[] { @"..\..\evil", "a/b", @"a\b", "C:", "id with space" })
        {
            var config = new AppConfig { Mounts = new() { new MountProfile { Id = bad, Name = "m", MountPoint = @"D:\m" } } };
            try { config.ValidateStructure(); }
            catch (ArgumentException) { continue; }
            Assert.Fail("accepted id: " + bad);
        }
    }

    [TestMethod]
    public void Two_instances_cannot_share_an_identity()
    {
        var config = new AppConfig
        {
            Mounts = new()
            {
                new MountProfile { Id = "same", Name = "one", MountPoint = @"D:\one" },
                new MountProfile { Id = "same", Name = "two", MountPoint = @"D:\two", StoreRoot = @"D:\two.store" }
            }
        };
        try { config.ValidateStructure(); }
        catch (ArgumentException) { return; }
        Assert.Fail("a duplicate identity was accepted");
    }

    [TestMethod]
    public void An_account_list_must_name_security_identifiers()
    {
        var config = new AppConfig
        {
            ControlUsers = new() { new ControlUser { Sid = "somebody", Name = "somebody" } }
        };
        try { config.ValidateStructure(); }
        catch (ArgumentException) { return; }
        Assert.Fail("a name was accepted where a SID belongs");

        config.ControlUsers[0].Sid = "S-1-5-21-1-2-3-1001";
        config.ValidateStructure();
    }

    // ---- #9: a directory source bounds where it reads -----------------------------------------

    [TestMethod]
    public void A_directory_source_refuses_a_path_that_leaves_its_root()
    {
        using var temp = new TempDir();
        string root = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(temp.Path, "outside.txt"), "not yours");
        var layer = new DirectoryLayer(root);

        foreach (string bad in new[] { "../outside.txt", @"..\outside.txt", "a/../../outside.txt" })
        {
            Assert.IsFalse(layer.TryGetEntry(bad, out _), "resolved: " + bad);
            try { layer.LocalPath(bad); Assert.Fail("canonicalised: " + bad); }
            catch (VfsNotFoundException) { }
        }
        Assert.IsFalse(layer.TryOpenRead("../outside.txt", out _, out _));
    }
}
