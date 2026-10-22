using System.Text.Json;
using System.Text.Json.Nodes;
using PolyMount.Core;
using PolyMount.Core.Ipc;
using PolyMount.Plugin.Testing;

namespace PolyMount.Plugins.Smb;

/// <summary>Tests exported by a separate test DLL; never shipped with the production plugin.</summary>
public sealed class SmbPluginTests : IPluginTestProvider
{
    public string PluginName => "smb";

    /// <summary>A check that cannot run here. Reported as skipped rather than passed: a test
    /// that quietly succeeded against nothing would be the one nobody notices.
    /// </summary>
    private sealed class Skip(string reason) : Exception(reason);

    public Task<IReadOnlyList<PluginTestResult>> RunAsync(PluginTestContext context)
    {
        var results = new List<PluginTestResult>();
        void Check(string name, Action test)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            try { test(); results.Add(new(PluginName, name, PluginTestOutcome.Passed)); }
            catch (Skip skip) { results.Add(new(PluginName, name, PluginTestOutcome.Skipped, skip.Message)); }
            catch (Exception ex) { results.Add(new(PluginName, name, PluginTestOutcome.Failed, ex.Message)); }
        }

        Check("configured-loading-and-preset", () =>
        {
            string path = typeof(SmbPlugin).Assembly.Location;
            var errors = PluginLoader.LoadConfigured(new[] { new PluginConfig { AssemblyPath = path } }, AppContext.BaseDirectory);
            if (errors.Count > 0) throw new Exception(string.Join("; ", errors));
            if (!SourceRegistry.Contains("smb")) throw new Exception("the source type was not registered");

            var preset = LayerPresetRegistry.All.Single(p => p.Id == "smb-workspace");
            if (!preset.Layers.Select(l => l.Type).SequenceEqual(new[] { "memory-cache", "warmup", "cache", "overlay", "smb" }))
                throw new Exception("Unexpected remote folder layer order: " + string.Join(", ", preset.Layers.Select(l => l.Type)));
            // The cache above the overlay is not the write target; a cache that accepted writes
            // would keep them from ever reaching the overlay.
            var cache = preset.Layers.Single(l => l.Type == "cache");
            if (!cache.Parameters.TryGetValue("Writable", out var writable) || writable.ValueKind != JsonValueKind.False)
                throw new Exception("The read cache above the overlay must not accept writes");

            var descriptor = LayerRegistry.Describe("smb");
            if (!descriptor.Fields.Any(f => f.Path == "Source.Path" && f.Required))
                throw new Exception("Missing the remote folder field");
            // The account is a picker, not a username and password pair: a password typed into a
            // layer would be written to the configuration file.
            if (descriptor.Fields.Any(f => f.Path is "Source.Username" or "Source.Password"))
                throw new Exception("The property panel still asks for credentials");
            var credential = descriptor.Fields.SingleOrDefault(f => f.Path == "Source.CredentialId");
            if (credential is not { Kind: LayerFieldKind.Credential })
                throw new Exception("The property panel offers no credential picker");
            // A share cannot be opened with a certificate: the picker has to say so up front.
            if (!credential.CredentialKinds.SequenceEqual(new[] { CredentialTypes.Password }))
                throw new Exception("The credential picker accepts kinds a share cannot use");
            var ttl = descriptor.Fields.SingleOrDefault(f => f.Path == "Source.Parameters.GenerationTtlSeconds");
            if (ttl is not { Kind: LayerFieldKind.Integer, Minimum: >= 1 })
                throw new Exception("The generation refresh interval is not bounded");

            if (typeof(SmbLayer).Assembly == typeof(ILayer).Assembly) throw new Exception("Plugin is not independent");
            if (ControlOperations.Find("get_smb_source") == null) throw new Exception("The plugin's MCP operation was not registered");

            PluginLoader.LoadConfigured(Array.Empty<PluginConfig>(), AppContext.BaseDirectory);
            if (SourceRegistry.Contains("smb") || LayerPresetRegistry.All.Any(p => p.Id == "smb-workspace"))
                throw new Exception("Disabled plugin registrations remain active");
            if (ControlOperations.Find("get_smb_source") != null)
                throw new Exception("A disabled plugin's MCP operation remains registered");
            PluginLoader.LoadConfigured(new[] { new PluginConfig { AssemblyPath = path } }, AppContext.BaseDirectory);
        });

        Check("path-parsing", () =>
        {
            var share = SmbPath.Parse(@"\\host\share");
            if (share.ShareRoot != @"\\host\share" || share.Root != @"\\host\share" || share.Folder.Length != 0)
                throw new Exception("A bare share did not parse: " + share);

            var folder = SmbPath.Parse(@"\\host\share\art\src");
            if (folder.Server != "host" || folder.Share != "share" || folder.Folder != @"art\src" ||
                folder.ShareRoot != @"\\host\share" || folder.Root != @"\\host\share\art\src")
                throw new Exception("A folder inside a share did not parse: " + folder);

            // Windows accepts forward slashes everywhere else, and a trailing one is how a folder
            // is usually copied out of an address bar.
            if (SmbPath.Parse("//host/share/art/").Root != @"\\host\share\art")
                throw new Exception("Forward slashes or a trailing separator were not normalized");

            void Rejects(string path)
            {
                try
                {
                    var parsed = SmbPath.Parse(path);
                    throw new Exception("'" + path + "' was accepted as " + parsed.Root);
                }
                catch (ArgumentException) { }
            }
            Rejects(@"");
            Rejects(@"C:\temp");
            Rejects(@"\\host");
            Rejects(@"host\share");
            Rejects(@"\\host\share\..\other");
            Rejects(@"\\?\UNC\host\share");
            Rejects(@"\\host\share\a:b");
        });

        Check("options", () =>
        {
            if (SmbOptions.From(null).GenerationTtlSeconds != SmbOptions.DefaultGenerationTtlSeconds)
                throw new Exception("The default refresh interval changed");
            var thirty = SmbOptions.From(new Dictionary<string, JsonElement>
            {
                ["GenerationTtlSeconds"] = JsonSerializer.SerializeToElement(30)
            });
            if (thirty.GenerationTtlSeconds != 30) throw new Exception("The configured interval was not read");
            foreach (var bad in new[] { 0, -1, 301 })
            {
                try
                {
                    SmbOptions.From(new Dictionary<string, JsonElement>
                    {
                        ["GenerationTtlSeconds"] = JsonSerializer.SerializeToElement(bad)
                    });
                    throw new Exception("A refresh interval of " + bad + " was accepted");
                }
                catch (ArgumentException) { }
            }
        });

        Check("content-version", () =>
        {
            // The caches compare this and never interpret it, so what matters is that different
            // bytes get a different version and the same bytes keep theirs.
            var modified = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            string version = SmbLayer.VersionOf(10, modified);
            if (version != SmbLayer.VersionOf(10, modified)) throw new Exception("The same file got two versions");
            if (version == SmbLayer.VersionOf(11, modified)) throw new Exception("A size change kept the version");
            if (version == SmbLayer.VersionOf(10, modified.AddSeconds(1))) throw new Exception("A write kept the version");
        });

        Check("unreachable-share-is-an-error", () =>
        {
            // A share that cannot be reached must fail when the instance is built. A mount that
            // answered "empty folder" instead would have every tool read it as a deleted tree.
            try
            {
                using var dead = new SmbLayer(SmbPath.Parse(@"\\polymount-no-such-host\share"), new SmbOptions(5));
                throw new Exception("an unreachable share produced a layer");
            }
            catch (IOException ex)
            {
                if (!ex.Message.Contains("polymount-no-such-host"))
                    throw new Exception("The failure did not name the folder: " + ex.Message);
            }
        });

        Check("real-share", () =>
        {
            // The one check that needs a share. It defaults to the machine's own administrative
            // share, which every Windows install has, and skips when this account cannot read it
            // — a build agent that refuses it should not read as a broken layer.
            string configured = context.Parameters.GetValueOrDefault("unc") is { Length: > 0 } value
                ? value : @"\\localhost\C$\Windows";
            SmbPath parsed;
            try { parsed = SmbPath.Parse(configured); }
            catch (ArgumentException ex) { throw new Skip("no usable share: " + ex.Message); }
            if (!Directory.Exists(parsed.Root)) throw new Skip("nothing reachable at " + parsed.Root);

            using var layer = new SmbLayer(parsed, new SmbOptions(1));

            if (!layer.TryGetEntry("", out var root) || !root.IsDir)
                throw new Exception("The mapped folder did not resolve to a directory");
            if (!string.IsNullOrEmpty(root.Name)) throw new Exception("The mapped folder reported a name of its own: " + root.Name);

            var children = layer.ListOwn("");
            if (children.Count == 0) throw new Exception("The share listed nothing");

            // A file's header, from the listing and from a lookup, has to agree: the cache keys
            // content on this string, and two answers for one file would key it twice.
            var listed = children.FirstOrDefault(c => !c.IsDir && c.Size > 0);
            if (listed == null) throw new Skip("the share holds no file to read");
            if (string.IsNullOrEmpty(listed.ContentVersion)) throw new Exception("A listed file carried no content version: " + listed.Name);
            if (!layer.TryGetEntry(listed.Name, out var header))
                throw new Exception("A listed file did not resolve on its own: " + listed.Name);
            if (header.Size != listed.Size || header.ContentVersion != listed.ContentVersion)
                throw new Exception($"A file was described differently by the listing ({listed.Size}/{listed.ContentVersion}) and by a lookup ({header.Size}/{header.ContentVersion})");

            if (!layer.TryOpenRead(listed.Name, out var opened, out var stream))
                throw new Exception("A listed file could not be opened: " + listed.Name);
            using (stream)
            {
                if (opened.Size != listed.Size) throw new Exception("The opened file reported another size");
                if (stream.ReadByte() < 0) throw new Exception("The opened file was empty");
            }

            // A name that is not there is "not found", not an error; the share is still up.
            if (layer.TryGetEntry("polymount-no-such-file.txt", out _))
                throw new Exception("A name that does not exist resolved");

            // Windows resolves `..` in the client, so a name carrying one would reach a folder
            // above the one this layer maps.
            try
            {
                layer.LocalPath("../elsewhere");
                throw new Exception("a path leaving the mapped folder was accepted");
            }
            catch (UnauthorizedAccessException) { }

            // The generation is memoized: two reads inside one interval cost one round trip and
            // answer the same, which is what keeps a cache lookup off the network.
            string first = layer.Generation;
            string second = layer.Generation;
            if (first != second) throw new Exception("The generation moved between two reads inside its interval");
            if (layer.GenerationAgeSeconds < 0) throw new Exception("The generation's age was reported as negative");
            if (!layer.Connected) throw new Exception("The layer reports no session");
        });

        Check("mcp-operation", () =>
        {
            var operation = ControlOperations.Find("get_smb_source")
                ?? throw new Exception("get_smb_source is not in the catalog");
            if (operation.Effect != ControlEffect.Read || !operation.Idempotent)
                throw new Exception("get_smb_source must be a read and idempotent");

            var link = new FakeLink
            {
                Result = """{"instance":"Demo","layers":[{"layer":"smb","path":"\\\\host\\share","connected":true}]}"""
            };
            var arguments = new ToolArguments(JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
            {
                ["instance"] = JsonSerializer.SerializeToElement("Demo"),
                ["layer"] = JsonSerializer.SerializeToElement("smb")
            }));
            operation.Invoke(link, arguments);
            if (link.InstanceName != "Demo" || link.Request?.Cmd != SmbPlugin.SourceCommand || link.Request?.Layer != "smb")
                throw new Exception("get_smb_source did not ask the instance for its source");
            var presented = operation.Present(link.LastResponse) as JsonNode
                ?? throw new Exception("get_smb_source did not project the payload");
            if (presented["layers"]?[0]?["connected"]?.GetValue<bool>() != true)
                throw new Exception("The source payload did not reach the caller");
        });

        return Task.FromResult<IReadOnlyList<PluginTestResult>>(results);
    }

    private sealed class FakeLink : IControlLink
    {
        public string? InstanceName;
        public ControlRequest? Request;
        public string Result = "{}";
        public ControlResponse LastResponse { get; private set; } = new();

        public ControlResponse Supervisor(ControlRequest request) => throw new NotSupportedException();
        public ControlResponse Instance(string? name, ControlRequest request)
        {
            InstanceName = name;
            Request = request;
            LastResponse = new ControlResponse { Result = Result };
            return LastResponse;
        }
        public ControlResponse ForPath(string fullPath, ControlRequest request) => throw new NotSupportedException();
        public string? OwnerOf(string fullPath) => null;
        public ControlResponse Status() => throw new NotSupportedException();
        public List<MountMetrics> Metrics(double windowSeconds, string? name) => throw new NotSupportedException();
    }
}
