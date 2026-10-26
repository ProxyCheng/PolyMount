using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Core.Ipc;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

/// <summary>The extension points a plugin reaches: a named strategy inside the shared prediction
/// layer, and commands the control pipe routes to an instance.</summary>
[TestClass]
public sealed class PluginExtensionTests
{
    private sealed class StubStrategy : IPredictionModel
    {
        private readonly PreloadRequest _preload;
        public readonly List<string> Observed = new();
        public readonly List<string> Preloaded = new();
        public StubStrategy(PredictionStrategyContext context) { _preload = context.Preload; }
        public IDisposable? Observe(string path, long streamId, bool listing)
        {
            Observed.Add(path);
            // A strategy asks the layer for a fetch and never touches the cache itself.
            if (path == "trigger" && _preload("file", streamId, PreloadOrigin.Table, null)) Preloaded.Add("file");
            return null;
        }
        public PredictionModelStats Stats => default;
        public void SetThrottle(long streamId, double value) { }
        public void SetSpatialThrottle(long streamId, double value) { }
        public void SetBurstThrottle(long streamId, double value) { }
        public void ReportGroup(string group, bool paid) { }
        public void Reset() { }
        public void Dispose() { }
    }

    [TestMethod]
    public void A_plugin_strategy_is_selected_by_name_and_runs_inside_the_shared_layer()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        source.AddFile("trigger", "t");
        source.AddFile("file", "payload");
        StubStrategy? stub = null;
        PredictionStrategyRegistry.Register("test-strategy", context => stub = new StubStrategy(context));
        try
        {
            using var layer = new PredictionCacheLayer(1 << 20, () => "v1", ReadPredictionProfiles.IdleOptions(),
                null, new[] { "test-strategy" }, temp.Sub("prediction.json"));
            layer.Attach(new VirtualFileSystem(new ILayer[] { source }));
            var vfs = new VirtualFileSystem(new ILayer[] { layer, source });
            using (var read = vfs.OpenRead("trigger", out _)) read.CopyTo(Stream.Null);

            Assert.IsNotNull(stub);
            CollectionAssert.Contains(stub!.Observed, "trigger");
            CollectionAssert.Contains(stub.Preloaded, "file");
            // The fetch went through the layer's own held cache, which is what makes the
            // strategy worth writing as a name rather than as a layer.
            Assert.IsTrue(layer.TryOpenRead("file", out _, out var held), "the strategy's preload is held by the shared layer");
            held.Dispose();
        }
        finally { PredictionStrategyRegistry.Unregister("test-strategy"); }
    }

    [TestMethod]
    public void An_unknown_strategy_name_is_ignored()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        source.AddFile("trigger", "t");
        using var layer = new PredictionCacheLayer(1 << 20, () => "v1", ReadPredictionProfiles.IdleOptions(),
            null, new[] { "no-such-strategy" }, temp.Sub("prediction.json"));
        layer.Attach(new VirtualFileSystem(new ILayer[] { source }));
        var vfs = new VirtualFileSystem(new ILayer[] { layer, source });
        using (var read = vfs.OpenRead("trigger", out _)) read.CopyTo(Stream.Null);
        Assert.AreEqual(0, layer.HeldObjects);
    }

    [TestMethod]
    public void A_configuration_can_be_read_without_being_written()
    {
        using var temp = new TempDir();
        string path = temp.Sub("config.json");
        // The read-only path is what a front-end uses to find the plugins; it must not create a
        // file that is not there, because the file it was pointed at belongs to the service.
        Assert.IsNull(AppConfig.Load(path));
        Assert.IsFalse(File.Exists(path), "reading a missing configuration created one");

        var config = new AppConfig();
        config.Plugins.Add(new PluginConfig { AssemblyPath = "plugins/Example.dll", Enabled = true });
        AppConfig.Save(path, config);
        var loaded = AppConfig.Load(path);
        Assert.IsNotNull(loaded);
        Assert.AreEqual(1, loaded!.Plugins.Count);
        Assert.AreEqual("plugins/Example.dll", loaded.Plugins[0].AssemblyPath);
    }

    [TestMethod]
    public void A_plugin_operation_joins_the_control_catalog()
    {
        var operation = new ControlOperation("test.list_things", "List things", "A read operation a plugin added.",
            ControlEffect.Read, true, """{"type":"object","properties":{}}""",
            (_, _) => new ControlResponse(), _ => null);
        ControlOperations.Register(operation);
        try
        {
            CollectionAssert.Contains(ControlOperations.All.Select(o => o.Name).ToList(), "test.list_things");
            Assert.AreSame(operation, ControlOperations.Find("test.list_things"));
            // A built-in name is not the plugin's to redefine: callers already rely on it.
            var clash = operation with { Name = "list_instances" };
            Assert.ThrowsException<ArgumentException>(() => ControlOperations.Register(clash));
        }
        finally { ControlOperations.Unregister("test.list_things"); }
        Assert.IsNull(ControlOperations.Find("test.list_things"));
    }

    [TestMethod]
    public void A_plugin_command_is_routed_by_name()
    {
        string? seen = null;
        PluginCommands.Register("test.plugin.command", command => seen = command.Request.Cmd);
        try
        {
            Assert.IsTrue(PluginCommands.Handles("test.plugin.command"));
            Assert.IsFalse(PluginCommands.Handles("test.plugin.other"));
            var applied = PluginCommands.TryApply(new PluginCommand(null!, "",
                new ControlRequest { Cmd = "test.plugin.command" }, new ControlResponse()));
            Assert.IsTrue(applied);
            Assert.AreEqual("test.plugin.command", seen);
            Assert.IsFalse(PluginCommands.TryApply(new PluginCommand(null!, "",
                new ControlRequest { Cmd = "test.plugin.other" }, new ControlResponse())),
                "an unregistered command is not answered by a plugin");
        }
        finally { PluginCommands.Unregister("test.plugin.command"); }
        Assert.IsFalse(PluginCommands.Handles("test.plugin.command"));
    }

    [TestMethod]
    public void One_plugin_entry_per_file_and_no_empty_ones()
    {
        var duplicate = new AppConfig();
        duplicate.Plugins.Add(new PluginConfig { AssemblyPath = "plugins/Example.dll" });
        duplicate.Plugins.Add(new PluginConfig { AssemblyPath = "plugins\\Example.dll" });
        Assert.ThrowsException<ArgumentException>(() => duplicate.Validate());

        var empty = new AppConfig();
        empty.Plugins.Add(new PluginConfig { AssemblyPath = " " });
        Assert.ThrowsException<ArgumentException>(() => empty.Validate());
    }

    [TestMethod]
    public void The_plugin_folder_offers_only_assemblies_that_declare_the_plugin_contract()
    {
        using var temp = new TempDir();
        string directory = temp.Sub("plugins");
        Directory.CreateDirectory(directory);
        // A real assembly without the contract (Core owns the interface, not an implementation),
        // a file that is not a PE at all, and the test assemblies the publish step leaves behind:
        // none of them is something a reader could add.
        string plainAssembly = Path.Combine(directory, "NotAPlugin.dll");
        File.Copy(typeof(PluginLoader).Assembly.Location, plainAssembly);
        File.WriteAllText(Path.Combine(directory, "Broken.dll"), "not a PE file");
        File.WriteAllText(Path.Combine(directory, "Some.Tests.dll"), "x");
        File.WriteAllText(Path.Combine(directory, "notes.txt"), "x");

        var files = PluginLoader.DiscoverFiles(Array.Empty<PluginConfig>(), temp.Path);

        Assert.AreEqual(0, files.Count);
        Assert.IsFalse(PluginLoader.IsPluginAssembly(plainAssembly));
        Assert.IsFalse(PluginLoader.IsPluginAssembly(Path.Combine(directory, "Broken.dll")));
    }

    [TestMethod]
    public void A_missing_plugin_file_is_reported_rather_than_thrown()
    {
        using var temp = new TempDir();
        var report = PluginLoader.LoadConfiguredDetailed(new[]
        {
            new PluginConfig { AssemblyPath = "plugins/Missing.dll", Enabled = true },
            // A disabled entry whose file is gone is a stale name the manager can still show and
            // remove; it is not a reason for the host to refuse the configuration.
            new PluginConfig { AssemblyPath = "plugins/AlsoMissing.dll", Enabled = false }
        }, temp.Path);

        Assert.AreEqual(1, report.Errors.Count, string.Join("; ", report.Errors));
        StringAssert.Contains(report.Errors[0], "does not exist");
        Assert.IsFalse(report.Plugins[0].Present);
        Assert.IsFalse(report.Plugins[0].Loaded);
        Assert.IsNotNull(report.Plugins[0].Error);
        Assert.IsFalse(report.Plugins[1].Present);
        Assert.IsNull(report.Plugins[1].Error);
        Assert.AreEqual(2, PluginLoader.LastReport.Plugins.Count);
    }
}
