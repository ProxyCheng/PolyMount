using System.Text.Json;
using System.Text.Json.Nodes;
using PolyMount.Core;
using PolyMount.Core.Ipc;

namespace PolyMount.Plugins.Svn;

/// <summary>Standalone sample plugin. Only the Core contracts are required.</summary>
public sealed class SvnPlugin : IPolyMountPlugin
{
    /// <summary>The commands this plugin answers on an instance's own pipe. Each operation below
    /// is the front-end side of one of them; both live here so the pair cannot drift.</summary>
    internal const string SourceCommand = "svn.source";
    internal const string LanesCommand = "svn.lanes";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public void Register(IPluginContext context)
    {
        // The instance side: what the SVN layers of this instance know. The secret never leaves
        // the transport — the account is a username, or null for anonymous.
        context.RegisterCommand(SourceCommand, command =>
            command.Response.Result = JsonSerializer.Serialize(Describe(command.Instance, command.Request.Layer), Json));
        context.RegisterCommand(LanesCommand, command =>
            command.Response.Result = JsonSerializer.Serialize(LaneReport(command.Instance, command.Request.Layer), Json));

        // The front-end side: operations in the control catalog, so a surface built on it (the
        // MCP server among them) offers them like any other tool.
        context.RegisterControlOperation(new ControlOperation(
            Name: "get_svn_source",
            Title: "Get SVN source",
            Description: "What an instance's SVN layers are doing: the revision they serve, the account they present (username only, null for anonymous), read-ahead counters and settings, cached metadata, and the read lanes the reader and read-ahead share.",
            Effect: ControlEffect.Read,
            Idempotent: true,
            InputSchema: """{"type":"object","properties":{"instance":{"type":"string","description":"Instance name, as list_instances reports it."},"layer":{"type":"string","description":"SVN layer id, as get_instance lists it under providers[].name. Omit for every SVN layer."}},"required":["instance"]}""",
            Invoke: (link, arguments) => link.Instance(arguments.String("instance"),
                new ControlRequest { Cmd = SourceCommand, Layer = arguments.OptionalString("layer") }),
            Present: response => JsonNode.Parse(string.IsNullOrWhiteSpace(response.Result) ? "{}" : response.Result)));

        context.RegisterControlOperation(new ControlOperation(
            Name: "list_svn_lanes",
            Title: "List SVN read lanes",
            Description: "The read lanes an instance's SVN layers share out their source with: requests, in-flight and queued counts, and mean wait per priority class. The reader's class keeping its wait flat while read-ahead's rises is what 'read-ahead is the one yielding' reads as.",
            Effect: ControlEffect.Read,
            Idempotent: true,
            InputSchema: """{"type":"object","properties":{"instance":{"type":"string","description":"Instance name, as list_instances reports it."},"layer":{"type":"string","description":"SVN layer id. Omit for every SVN layer."}},"required":["instance"]}""",
            Invoke: (link, arguments) => link.Instance(arguments.String("instance"),
                new ControlRequest { Cmd = LanesCommand, Layer = arguments.OptionalString("layer") }),
            Present: response => JsonNode.Parse(string.IsNullOrWhiteSpace(response.Result) ? "{}" : response.Result)));

        context.RegisterPreset(new()
        {
            Id = "svn-workspace",
            Titles = SvnTexts.All("preset.svn-workspace.title"),
            Layers = new()
            {
                // Below the memory cache: warming RAM with listings would spend the memory
                // cache's budget on records the first client enumeration fills on demand
                // anyway, and evict the file blocks that cache exists for. Above the overlay,
                // so the walk still sees local deletions. Here it fills the durable cache —
                // the one that answers a restart.
                new() { Id = "memory-cache", Type = "memory-cache" },
                new() { Id = "warmup", Type = "warmup" },
                // Predictions read through the overlay, so a guess sees local writes. The
                // durable read cache sits below it (it caches the chain's view) but above the
                // overlay, which is the only layer that accepts writes and therefore sits
                // directly above the source.
                new() { Id = "predict", Type = "prediction-cache", Parameters = new()
                    { ["CapacityBytes"] = System.Text.Json.JsonSerializer.SerializeToElement(16L * 1048576) } },
                new() { Id = "cache", Type = "cache", Parameters = new()
                    { ["Writable"] = System.Text.Json.JsonSerializer.SerializeToElement(false) } },
                new() { Id = "overlay", Type = "overlay" },
                new() { Id = "svn", Type = "svn", Source = new() { Type = "svn" } }
            }
        });
        context.RegisterSource("svn", (config, state) =>
            SvnLayer.Create(config.Url, config.TrustServerCertFailures, Path.Combine(state, "pin.txt"), config.Username,
                config.Password, SvnPrefetchOptions.From(config.Parameters), config.CredentialId),
            config =>
            {
                if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var repository) || config.PinnedRevision < 0)
                    throw new ArgumentException("SVN requires an absolute repository URL and nonnegative revision");
                // The transport speaks HTTP/WebDAV. svn:// and svn+ssh:// are the svnserve
                // protocol, which only the command-line client spoke, and it is no longer shipped.
                if (repository.Scheme != Uri.UriSchemeHttp && repository.Scheme != Uri.UriSchemeHttps)
                    throw new ArgumentException("only http(s) repositories are supported; '" + repository.Scheme +
                        "://' needs the svn command-line client, which this build no longer includes");
                SvnPrefetchOptions.From(config.Parameters);
            }, new LayerDescriptor
            {
                Type = "svn", Title = SvnTexts.Get("svn.title"), Icon = "repository",
                Description = SvnTexts.Get("svn.description"),
                Fields = new()
                {
                    new() { Path = "Source.Url", Title = SvnTexts.Get("svn.field.url"), Required = true },
                    new() { Path = "Source.PinnedRevision", Kind = LayerFieldKind.Integer, Minimum = 0,
                        Title = SvnTexts.Get("svn.field.revision") },
                    new() { Path = "Source.TrustServerCertFailures", Title = SvnTexts.Get("svn.field.trust") },
                    new() { Path = "Source.CredentialId", Kind = LayerFieldKind.Credential,
                        Title = SvnTexts.Get("svn.field.credential"),
                        Placeholder = SvnTexts.Get("svn.field.credential.placeholder"),
                        Description = SvnTexts.Get("svn.field.credential.description") },
                    // No username or password field: the transport reads the credentials the
                    // Subversion client already cached for this Windows user, which is the only
                    // place they exist without somebody typing them into a configuration file.
                    // A hand-edited configuration may still carry them; `SvnCredentials` honours
                    // that first, so an installation whose service account has no profile of its
                    // own can be configured, it is just not something the window asks for.
                    // Directory enumerations are one round trip each, so a walk pays them one
                    // after another unless the next level is fetched while the caller reads
                    // the current one. Bounded, and one level deep.
                    new() { Path = "Source.Parameters.PrefetchConcurrency", Kind = LayerFieldKind.Integer, Minimum = 0, Maximum = 8,
                        DefaultValue = "2", Title = SvnTexts.Get("svn.field.prefetch.concurrency"),
                        Description = SvnTexts.Get("svn.field.prefetch.concurrency.description") },
                    new() { Path = "Source.Parameters.PrefetchQueueDepth", Kind = LayerFieldKind.Integer, Minimum = 1, Maximum = 1024,
                        DefaultValue = "16", Title = SvnTexts.Get("svn.field.prefetch.queue"),
                        Description = SvnTexts.Get("svn.field.prefetch.queue.description") },
                    new() { Path = "Source.Parameters.PrefetchChildren", Kind = LayerFieldKind.Integer, Minimum = 0, Maximum = 256,
                        DefaultValue = "8", Title = SvnTexts.Get("svn.field.prefetch.children"),
                        Description = SvnTexts.Get("svn.field.prefetch.children.description") },
                    // Read-ahead is a burst of requests against a source the reader is using too.
                    // Serving both from one budget is what put a speculative download in front of
                    // a demanded one, so the source is shared out by class instead: the reader
                    // keeps nearly all of it, and read-ahead has a floor of one and this ceiling.
                    new() { Path = "Source.Parameters.SpeculativeConcurrency", Kind = LayerFieldKind.Integer, Minimum = 1, Maximum = 8,
                        DefaultValue = "2", Title = SvnTexts.Get("svn.field.lane"),
                        Description = SvnTexts.Get("svn.field.lane.description") }
                },
                Actions = new() { new() { Command = PolyMount.Core.Ipc.ControlCommands.RefreshHead,
                    Field = "Source.PinnedRevision",
                    Title = SvnTexts.Get("svn.action.refreshHead"),
                    Confirmation = SvnTexts.Get("svn.action.refreshHead.confirmation") } }
            });
    }

    /// <summary>The instance's SVN layers, keyed by the configuration id the status reports.
    /// <paramref name="only"/> narrows the answer to one layer, or to none when that id is not
    /// an SVN layer of this instance.</summary>
    private static IEnumerable<(string Id, SvnLayer Layer)> SvnLayers(MountInstance instance, string? only)
    {
        foreach (var pair in instance.Layers)
        {
            if (pair.Value is not SvnLayer layer) continue;
            if (!string.IsNullOrEmpty(only) && !string.Equals(pair.Key, only, StringComparison.OrdinalIgnoreCase)) continue;
            yield return (pair.Key, layer);
        }
    }

    private static object Describe(MountInstance instance, string? only)
    {
        var configs = instance.Profile.GetLayers().ToDictionary(layer => layer.Id, StringComparer.OrdinalIgnoreCase);
        var layers = new List<object>();
        foreach (var (id, layer) in SvnLayers(instance, only))
        {
            configs.TryGetValue(id, out var config);
            SvnPrefetchOptions? prefetch = null;
            try { prefetch = config == null ? null : SvnPrefetchOptions.From(config.Source.Parameters); }
            catch (ArgumentException) { /* the mount validated it; report what is live, not what is not */ }
            layers.Add(new
            {
                layer = id,
                url = config?.Source.Url,
                revision = layer.Revision,
                account = layer.Account,
                readAhead = new
                {
                    listings = layer.PrefetchedListings,
                    hits = layer.PrefetchHits,
                    stale = layer.PrefetchStale,
                    failures = layer.PrefetchFailures,
                    concurrency = prefetch?.Concurrency,
                    queueDepth = prefetch?.QueueDepth,
                    children = prefetch?.Children,
                    speculative = prefetch?.SpeculativeConcurrency
                },
                metadata = new { directories = layer.CachedDirectories, estimatedBytes = layer.EstimatedMetadataBytes },
                lanes = layer.Lanes.Snapshot()
            });
        }
        return new { instance = instance.Profile.Name, layers };
    }

    private static object LaneReport(MountInstance instance, string? only) => new
    {
        instance = instance.Profile.Name,
        layers = SvnLayers(instance, only).Select(entry => new { layer = entry.Id, lanes = entry.Layer.Lanes.Snapshot() }).ToArray()
    };
}
