using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class NewMountDraftTests
{
    private static LayerDescriptor SvmDescriptor() => new()
    {
        Type = "svn",
        Title = new("SVN", "SVN"),
        Fields = new()
        {
            new() { Path = "Source.Url", Title = new("Address", "Address"), Required = true },
            new() { Path = "Source.CredentialId", Kind = LayerFieldKind.Credential,
                Title = new("Credential", "Credential"), EmptyIsNull = true },
            new() { Path = "Source.Parameters.PrefetchConcurrency", Kind = LayerFieldKind.Integer,
                Minimum = 0, Maximum = 8, DefaultValue = "2", Title = new("Read-ahead", "Read-ahead") }
        }
    };

    private static LayerPreset Preset() => new()
    {
        Id = "svn-workspace",
        Layers = new()
        {
            new() { Id = "memory-cache", Type = "memory-cache" },
            new() { Id = "cache", Type = "cache", CacheCapacityBytes = 1234 },
            new() { Id = "overlay", Type = "overlay" },
            new() { Id = "svn", Type = "svn", Source = new() { Type = "svn" } }
        }
    };

    [TestMethod]
    public void A_repository_address_names_the_instance_after_its_last_component()
    {
        Assert.AreEqual("demo1", NewMountDraft.NameFromSource(
            "https://svn.example.com/svn/demo/branches/demo-Messiah/demo1"));
        Assert.AreEqual("demo1", NewMountDraft.NameFromSource(
            "https://svn.example.com/svn/demo/branches/demo-Messiah/demo1/"));
        Assert.AreEqual("share", NewMountDraft.NameFromSource(@"\\server\share"));
        Assert.AreEqual("folder", NewMountDraft.NameFromSource(@"\\server\share\folder\"));
        Assert.AreEqual("repo", NewMountDraft.NameFromSource(@"D:\work\repo"));
        Assert.AreEqual("repo", NewMountDraft.NameFromSource(@"D:\work\repo\"));
        // Nothing to take a component from: the site's own name is the next best answer, and a
        // bare drive leaves the neutral one.
        Assert.AreEqual("svn", NewMountDraft.NameFromSource("https://svn.example.com"));
        Assert.AreEqual("Mount", NewMountDraft.NameFromSource("D:"));
        Assert.AreEqual("Mount", NewMountDraft.NameFromSource(""));
    }

    [TestMethod]
    public void A_name_holds_only_what_a_folder_name_can()
    {
        // The name is written into a log folder, so a component carrying a character Windows
        // refuses there is repaired rather than passed on.
        Assert.AreEqual("a-b", NewMountDraft.NameFromSource("https://host/a%3Cb"));
        Assert.AreEqual("a", NewMountDraft.NameFromSource("https://host/a?b=1"));
        Assert.AreEqual("host", NewMountDraft.NameFromSource("https://host/"));
        Assert.IsTrue(NewMountDraft.NameFromSource("https://host/" + new string('x', 200)).Length <= 64);
    }

    [TestMethod]
    public void A_taken_name_gets_the_next_free_suffix()
    {
        var taken = new[] { "demo", "demo-2" };
        Assert.AreEqual("demo", NewMountDraft.UniqueName("demo", Array.Empty<string>()));
        Assert.AreEqual("demo-3", NewMountDraft.UniqueName("demo", taken));
        Assert.AreEqual("Trimmed", NewMountDraft.UniqueName("  Trimmed  ", Array.Empty<string>()));
        Assert.AreEqual("Mount", NewMountDraft.UniqueName("", Array.Empty<string>()));
    }

    [TestMethod]
    public void A_mount_point_follows_the_root_and_avoids_a_path_already_in_use()
    {
        string root = @"D:\Mounts";
        Assert.AreEqual(@"D:\Mounts\demo", NewMountDraft.SuggestMountPoint(root, "demo", Array.Empty<string>()));
        // The same place spelled with a trailing separator is still the same place.
        Assert.AreEqual(@"D:\Mounts\demo-2",
            NewMountDraft.SuggestMountPoint(root, "demo", new[] { @"d:\mounts\demo\" }));
    }

    [TestMethod]
    public void The_root_prefers_what_was_remembered_then_the_convention_of_this_machine()
    {
        using var temp = new TempDir();
        string mounted = temp.Sub("Instances", "demo");
        Directory.CreateDirectory(mounted);
        Assert.AreEqual(@"D:\Chosen", NewMountDraft.SuggestRoot(@"D:\Chosen", new[] { mounted }));
        Assert.AreEqual(temp.Sub("Instances"), NewMountDraft.SuggestRoot(null, new[] { mounted }));
        // A mount point whose parent is gone says nothing about where this machine keeps them.
        Assert.AreEqual(NewMountDraft.DefaultRoot,
            NewMountDraft.SuggestRoot(null, new[] { temp.Sub("gone", "demo") }));
    }

    [TestMethod]
    public void The_essentials_are_the_sources_required_values_and_its_account()
    {
        var fields = NewMountDraft.Essentials(SvmDescriptor());
        CollectionAssert.AreEqual(new[] { "Source.Url", "Source.CredentialId" }, fields.Select(f => f.Path).ToArray());
        Assert.AreEqual(0, NewMountDraft.Essentials(null).Count);
    }

    [TestMethod]
    public void Creating_from_a_preset_writes_the_asked_values_into_the_source_layer()
    {
        var preset = Preset();
        var descriptors = new[] { SvmDescriptor() };
        var profile = NewMountDraft.Create(preset, "  demo  ", @"D:\Mounts\demo", new[]
        {
            (SvmDescriptor().Fields[0], "https://svn.example.com/repo"),
            (SvmDescriptor().Fields[1], "cred-1")
        }, descriptors, new[] { "svn" });

        Assert.AreEqual("demo", profile.Name);
        Assert.AreEqual(@"D:\Mounts\demo", profile.MountPoint);
        Assert.IsTrue(profile.Enabled);
        Assert.IsTrue(profile.Id.Length > 0, "the instance was given no identity, so it would get a new store every save");
        Assert.AreEqual("https://svn.example.com/repo", profile.Source.Url);
        CollectionAssert.AreEqual(new[] { "memory-cache", "cache", "overlay", "svn" },
            profile.Layers!.Select(layer => layer.Id).ToArray());
        var source = profile.Layers!.Single(layer => layer.Type == "svn");
        Assert.AreEqual("https://svn.example.com/repo", source.Source.Url);
        Assert.AreEqual("cred-1", source.Source.CredentialId);
        // The compatibility fields the editor keeps in step with the chain.
        Assert.AreEqual(1234L, profile.CacheCapacityBytes);
        // The preset is a template: composing from it must leave it untouched for the next instance.
        Assert.AreEqual("", preset.Layers[3].Source.Url);
        Assert.AreNotSame(preset.Layers[3], source);
    }

    [TestMethod]
    public void A_preset_without_a_source_layer_still_composes_a_chain()
    {
        var preset = new LayerPreset { Id = "cache-only", Layers = new() { new() { Id = "cache", Type = "cache" } } };
        var profile = NewMountDraft.Create(preset, "Work", @"D:\Mounts\Work", Array.Empty<(LayerFieldDescriptor, string)>(),
            Array.Empty<LayerDescriptor>(), new[] { "svn" });
        Assert.AreEqual(1, profile.Layers!.Count);
        Assert.AreEqual("", profile.Source.Url);
        Assert.IsNull(NewMountDraft.SourceDescriptor(preset, Array.Empty<LayerDescriptor>(), new[] { "svn" }));
    }

    [TestMethod]
    public void A_value_is_written_to_the_source_layer_and_nowhere_else()
    {
        // Two layers that could hold a URL: the source is the one whose type is registered as a
        // source, so the value lands there even when it is not the first layer in the chain.
        var preset = new LayerPreset
        {
            Layers = new()
            {
                new() { Id = "overlay", Type = "overlay" },
                new() { Id = "svn", Type = "svn" },
                new() { Id = "dir", Type = "dir" }
            }
        };
        var descriptors = new[]
        {
            SvmDescriptor(),
            new LayerDescriptor { Type = "dir", Fields = new() { new() { Path = "Source.Path", Required = true, Title = new("Folder", "Folder") } } }
        };
        var profile = NewMountDraft.Create(preset, "Repo", @"D:\Mounts\Repo", new[]
        {
            (SvmDescriptor().Fields[0], "https://svn.example.com/repo")
        }, descriptors, new[] { "svn", "dir" });
        Assert.AreEqual("https://svn.example.com/repo", profile.Layers!.Single(layer => layer.Id == "svn").Source.Url);
        Assert.AreEqual("", profile.Layers!.Single(layer => layer.Id == "dir").Source.Url);
    }

    [TestMethod]
    public void A_composed_instance_is_a_configuration_the_service_accepts()
    {
        // The composed profile is what gets appended and saved. A chain whose ids, types or source
        // the service refuses would otherwise only be found after the reader pressed the button,
        // with the dialog already closed.
        using var temp = new TempDir();
        var preset = LayerPresetRegistry.All.Single(entry => entry.Id == "local-directory");
        var descriptors = LayerRegistry.Descriptors.ToList();
        var sourceTypes = SourceRegistry.Types.ToList();
        var descriptor = NewMountDraft.SourceDescriptor(preset, descriptors, sourceTypes)!;
        var field = NewMountDraft.Essentials(descriptor).Single(entry => entry.Required);
        var profile = NewMountDraft.Create(preset, "Work", temp.Sub("Work"),
            new[] { (field, temp.Path) }, descriptors, sourceTypes);

        var config = new AppConfig { Mounts = { new() { Name = "Other", MountPoint = temp.Sub("Other") } } };
        config.Mounts.Add(profile);
        config.Validate();
        Assert.AreEqual(temp.Path, profile.Layers!.Single(layer => layer.Type == "dir").Source.Path);

        // And it reads back as the same instance: the save writes this document and the service
        // reads it again.
        var back = AppConfig.FromJson(config.ToJson());
        Assert.AreEqual(2, back.Mounts.Count);
        Assert.AreEqual(profile.Id, back.Mounts[1].Id);
        Assert.AreEqual(temp.Path, back.Mounts[1].GetLayers().Single(layer => layer.Type == "dir").Source.Path);
    }
}
