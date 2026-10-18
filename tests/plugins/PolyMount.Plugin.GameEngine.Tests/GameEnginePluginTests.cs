using System.Text.Json;
using PolyMount.Core;
using PolyMount.Core.Ipc;
using PolyMount.Plugin.Testing;

namespace PolyMount.Plugins.GameEngine;

/// <summary>Tests exported by a separate test DLL; never shipped with the production plugin.</summary>
public sealed class GameEnginePluginTests : IPluginTestProvider
{
    public string PluginName => "game-engine";

    private static bool Until(Func<bool> condition, int milliseconds = 5000)
    {
        long deadline = Environment.TickCount64 + milliseconds;
        while (!condition() && Environment.TickCount64 < deadline) Thread.Sleep(2);
        return condition();
    }

    public Task<IReadOnlyList<PluginTestResult>> RunAsync(PluginTestContext context)
    {
        var results = new List<PluginTestResult>();
        void Check(string name, Action test)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            try { test(); results.Add(new(PluginName, name, PluginTestOutcome.Passed)); }
            catch (Exception ex) { results.Add(new(PluginName, name, PluginTestOutcome.Failed, ex.Message)); }
        }

        Check("registration-and-preset", () =>
        {
            var path = typeof(GameEnginePlugin).Assembly.Location;
            var errors = PluginLoader.LoadConfigured(new[] { new PluginConfig { AssemblyPath = path } }, AppContext.BaseDirectory);
            if (errors.Count > 0) throw new Exception(string.Join("; ", errors));
            if (!PredictionStrategyRegistry.Names.Contains("manifests", StringComparer.OrdinalIgnoreCase))
                throw new Exception("The manifests strategy was not registered");
            var preset = LayerPresetRegistry.All.SingleOrDefault(p => p.Id == "game-engine-workspace")
                ?? throw new Exception("Missing the game engine preset");
            var order = preset.Layers.Select(l => l.Type).ToArray();
            if (!order.SequenceEqual(new[] { "memory-cache", "warmup", "prediction-cache", "cache", "overlay", "svn" }))
                throw new Exception("Unexpected game engine workspace layer order: " + string.Join(",", order));
            var predict = preset.Layers.Single(l => l.Type == "prediction-cache");
            if (!predict.Parameters.TryGetValue("Strategies", out var strategies) ||
                !string.Equals(strategies.GetString(), "manifests", StringComparison.OrdinalIgnoreCase))
                throw new Exception("The preset does not select the manifests strategy");
            var cache = preset.Layers.Single(l => l.Type == "cache");
            if (!cache.Parameters.TryGetValue("Writable", out var writable) || writable.ValueKind != JsonValueKind.False)
                throw new Exception("The read cache above the overlay must not accept writes");
            if (cache.CacheCapacityBytes != 16L * 1024 * 1024 * 1024)
                throw new Exception("The preset no longer matches the capacity the start-up was measured with");
            if (typeof(ManifestFamilyPrediction).Assembly == typeof(ILayer).Assembly)
                throw new Exception("The plugin is not independent of Core");
            if (ControlOperations.Find("list_manifest_families") == null)
                throw new Exception("The plugin's control operation was not registered");
            // Unloading removes what the plugin added; nothing of it may outlive the plugin.
            PluginLoader.LoadConfigured(Array.Empty<PluginConfig>(), AppContext.BaseDirectory);
            if (PredictionStrategyRegistry.Names.Count > 0)
                throw new Exception("A disabled plugin's strategy remains registered");
            if (LayerPresetRegistry.All.Any(p => p.Id == "game-engine-workspace"))
                throw new Exception("A disabled plugin's preset remains registered");
            if (ControlOperations.Find("list_manifest_families") != null)
                throw new Exception("A disabled plugin's control operation remains registered");
            PluginLoader.LoadConfigured(new[] { new PluginConfig { AssemblyPath = path } }, AppContext.BaseDirectory);
        });

        Check("a-family-tool-asks-the-instance", () =>
        {
            var operation = ControlOperations.Find("list_manifest_families")
                ?? throw new Exception("The family tool is not in the catalog");
            if (operation.Effect != ControlEffect.Read || !operation.Idempotent)
                throw new Exception("The family tool must be a read and idempotent");
            var link = new FakeLink { Result = """{"instance":"Project","families":[{"family":"char3_","issued":2,"hits":2,"wasted":0,"disabled":false}]}""" };
            var arguments = new ToolArguments(JsonSerializer.SerializeToElement(
                new Dictionary<string, JsonElement> { ["instance"] = JsonSerializer.SerializeToElement("Project") }));
            var response = operation.Invoke(link, arguments);
            if (link.InstanceName != "Project" || link.Request?.Cmd != GameEnginePlugin.FamiliesCommand)
                throw new Exception("The tool did not ask the instance for its families");
            var presented = operation.Present(response) as System.Text.Json.Nodes.JsonNode
                ?? throw new Exception("The tool did not project the plugin's payload");
            if (presented["families"]?[0]?["family"]?.GetValue<string>() != "char3_")
                throw new Exception("The tool's payload did not reach the caller");
        });


        Check("a-manifest-read-prefetches-its-family", () =>
        {
            var source = new TreeSource();
            source.AddFile("Repo/char3_10071.local/resource.repository", "<Repository/>");
            source.AddFile("Repo/char3_10072.local/resource.repository", "<Repository/>");
            source.AddFile("Repo/char3_10073.local/resource.repository", "<Repository/>");
            source.AddFile("Repo/other_1.local/resource.repository", "<Repository/>");
            var vfs = new VirtualFileSystem(new ILayer[] { source });
            var loaded = new List<string>();
            using var model = NewModel(vfs, tempPath(), (path, stream, origin, group) =>
            {
                lock (loaded) loaded.Add(path);
                return true;
            });
            long stream = RequestDiagnostics.StreamId(4242, 1);
            using (model.Observe("Repo/char3_10071.local/resource.repository", stream, false)) { }
            if (!Until(() => model.Stats.Completed > 0)) throw new Exception("Nothing was prefetched");
            lock (loaded)
            {
                if (!loaded.Any(p => p.EndsWith("char3_10072.local/resource.repository", StringComparison.OrdinalIgnoreCase)))
                    throw new Exception("The next manifest in the family was not prefetched: " + string.Join(",", loaded));
                if (!loaded.Any(p => p.EndsWith("char3_10073.local/resource.repository", StringComparison.OrdinalIgnoreCase)))
                    throw new Exception("The family's manifests were not prefetched: " + string.Join(",", loaded));
                if (loaded.Any(p => p.Contains("other_1", StringComparison.OrdinalIgnoreCase)))
                    throw new Exception("A manifest outside the trigger's family was prefetched");
                if (loaded.Any(p => p.EndsWith("char3_10071.local/resource.repository", StringComparison.OrdinalIgnoreCase)))
                    throw new Exception("The manifest the caller was already reading was prefetched");
            }
        });

        Check("the-family-listing-is-shared", () =>
        {
            var source = new TreeSource();
            source.AddFile("Repo/fa_1.local/resource.repository", "<Repository/>");
            source.AddFile("Repo/fa_2.local/resource.repository", "<Repository/>");
            source.AddFile("Repo/fa_3.local/resource.repository", "<Repository/>");
            var vfs = new VirtualFileSystem(new ILayer[] { source });
            using var model = NewModel(vfs, tempPath(), (path, stream, origin, group) => true);
            long stream = RequestDiagnostics.StreamId(4243, 1);
            using (model.Observe("Repo/fa_1.local/resource.repository", stream, false)) { }
            if (!Until(() => model.Stats.Completed > 0)) throw new Exception("Nothing was prefetched");
            int afterFirst = source.ListCalls;
            using (model.Observe("Repo/fa_2.local/resource.repository", stream, false)) { }
            if (!Until(() => model.Stats.Completed > 2)) throw new Exception("The second lookup did not run");
            if (source.ListCalls != afterFirst)
                throw new Exception($"The repository was listed again ({afterFirst} -> {source.ListCalls}); the listing should be reused");
        });

        Check("a-family-that-keeps-wasting-is-closed", () =>
        {
            var source = new TreeSource();
            source.AddFile("Repo/fa_1.local/resource.repository", "<Repository/>");
            source.AddFile("Repo/fa_2.local/resource.repository", "<Repository/>");
            source.AddFile("Repo/fa_3.local/resource.repository", "<Repository/>");
            var vfs = new VirtualFileSystem(new ILayer[] { source });
            var loaded = new List<string>();
            using var model = NewModel(vfs, tempPath(), (path, stream, origin, group) =>
            {
                lock (loaded) loaded.Add(path);
                return true;
            }, new Dictionary<string, JsonElement> { ["Manifest.GroupWasteStreak"] = JsonSerializer.SerializeToElement(2) });
            long stream = RequestDiagnostics.StreamId(4244, 1);
            using (model.Observe("Repo/fa_1.local/resource.repository", stream, false)) { }
            if (!Until(() => model.Stats.Completed > 0)) throw new Exception("Nothing was prefetched");
            model.ReportGroup("fa_", false);
            model.ReportGroup("fa_", false);
            if (model.Stats.ClosedGroups != 1) throw new Exception("The family was not closed by its unread guesses");
            loaded.Clear();
            using (model.Observe("Repo/fa_1.local/resource.repository", stream, false)) { }
            Thread.Sleep(200);
            lock (loaded)
                if (loaded.Count != 0) throw new Exception("A closed family kept prefetching");
        });

        Check("files-that-are-not-manifests-are-ignored", () =>
        {
            var source = new TreeSource();
            source.AddFile("Repo/fa_1.local/resource.repository", "<Repository/>");
            source.AddFile("Repo/fa_1.local/texture.xml", "<Texture/>");
            source.AddFile("Repo/fa_2.local/resource.repository", "<Repository/>");
            var vfs = new VirtualFileSystem(new ILayer[] { source });
            var loaded = new List<string>();
            using var model = NewModel(vfs, tempPath(), (path, stream, origin, group) =>
            {
                lock (loaded) loaded.Add(path);
                return true;
            });
            long stream = RequestDiagnostics.StreamId(4245, 1);
            using (model.Observe("Repo/fa_1.local/texture.xml", stream, false)) { }
            Thread.Sleep(150);
            lock (loaded)
                if (loaded.Count != 0) throw new Exception("A non-manifest read started a prefetch");
        });

        Check("families-follow-the-package-layout", () =>
        {
            if (ManifestFamilyPrediction.FamilyOf("char3_10071.local", ".local", "_") != "char3_")
                throw new Exception("char3 packages are not grouped");
            if (ManifestFamilyPrediction.FamilyOf("000_BLEACH_model.local", ".local", "_") != "000_BLEACH_")
                throw new Exception("Packages are not grouped by their last separator");
            if (ManifestFamilyPrediction.FamilyOf("101763.local", ".local", "_") != "101763")
                throw new Exception("A name without a separator is its own family");
            if (ManifestFamilyPrediction.FamilyOf("world-42.pak", ".pak", "-") != "world-")
                throw new Exception("The layout parameters do not reach the family rule");
        });

        return Task.FromResult<IReadOnlyList<PluginTestResult>>(results);
    }

    private static string tempPath() => Path.Combine(Path.GetTempPath(), "polymount-engine-" + Guid.NewGuid().ToString("n") + ".json");

    private static ManifestFamilyPrediction NewModel(VirtualFileSystem vfs, string modelPath, PreloadRequest preload,
        Dictionary<string, JsonElement>? parameters = null)
    {
        parameters ??= new Dictionary<string, JsonElement>();
        parameters["Manifest.TickIntervalMs"] = JsonSerializer.SerializeToElement(10);
        parameters["Manifest.FamilyCooldownMs"] = JsonSerializer.SerializeToElement(0);
        var context = new PredictionStrategyContext(vfs, modelPath, preload,
            new PredictionTuning(8L * 1024 * 1024, 5000, 32), parameters);
        return new ManifestFamilyPrediction(context);
    }

    /// <summary>A control link that records what an operation asked for, so a tool can be
    /// exercised without a running service.</summary>
    private sealed class FakeLink : IControlLink
    {
        public string? InstanceName;
        public ControlRequest? Request;
        public string Result = "{}";

        public ControlResponse Supervisor(ControlRequest request) => throw new NotSupportedException();
        public ControlResponse Instance(string? name, ControlRequest request)
        {
            InstanceName = name;
            Request = request;
            return new ControlResponse { Result = Result };
        }
        public ControlResponse ForPath(string fullPath, ControlRequest request) => throw new NotSupportedException();
        public string? OwnerOf(string fullPath) => null;
        public ControlResponse Status() => throw new NotSupportedException();
        public List<MountMetrics> Metrics(double windowSeconds, string? name) => throw new NotSupportedException();
    }

    /// <summary>An in-memory source: directories and files, with a count of the listings asked
    /// for so a test can see that one lookup's listing is reused by the next.</summary>
    private sealed class TreeSource : ILayer
    {
        private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase) { "" };
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        private int _listCalls;

        public int ListCalls { get { lock (_dirs) return _listCalls; } }
        public string Name => "tree";
        public bool Writable => false;
        public string Generation => "g1";

        public void AddFile(string rel, string content)
        {
            lock (_dirs)
            {
                string parent = VfsPath.Parent(rel);
                while (parent.Length > 0 && _dirs.Add(parent)) parent = VfsPath.Parent(parent);
                _files[rel] = System.Text.Encoding.UTF8.GetBytes(content);
            }
        }

        public bool TryGetEntry(string rel, out VfsEntry entry)
        {
            lock (_dirs)
            {
                if (rel.Length == 0) { entry = new VfsEntry { Name = "", IsDir = true }; return true; }
                if (_dirs.Contains(rel)) { entry = new VfsEntry { Name = VfsPath.Name(rel), IsDir = true }; return true; }
                if (_files.TryGetValue(rel, out var bytes))
                {
                    entry = new VfsEntry { Name = VfsPath.Name(rel), IsDir = false, Size = bytes.Length };
                    return true;
                }
                entry = null!;
                return false;
            }
        }

        public IReadOnlyList<VfsEntry> ListOwn(string rel)
        {
            lock (_dirs)
            {
                _listCalls++;
                var entries = new List<VfsEntry>();
                foreach (var dir in _dirs)
                    if (dir.Length > 0 && string.Equals(VfsPath.Parent(dir), rel, StringComparison.OrdinalIgnoreCase))
                        entries.Add(new VfsEntry { Name = VfsPath.Name(dir), IsDir = true });
                foreach (var file in _files.Keys)
                    if (string.Equals(VfsPath.Parent(file), rel, StringComparison.OrdinalIgnoreCase))
                        entries.Add(new VfsEntry { Name = VfsPath.Name(file), IsDir = false, Size = _files[file].Length });
                return entries;
            }
        }

        public bool TryOpenRead(string rel, out VfsEntry entry, out Stream stream)
        {
            stream = null!;
            if (!TryGetEntry(rel, out entry) || entry.IsDir) return false;
            lock (_dirs) stream = new MemoryStream(_files[rel], writable: false);
            return true;
        }
    }
}
