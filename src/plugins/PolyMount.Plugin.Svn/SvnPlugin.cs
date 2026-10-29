using System.Text.Json;
using System.Text.Json.Nodes;

namespace PolyMount.Plugins.Svn;

/// <summary>Standalone sample plugin. Only the Core contracts are required.</summary>
public sealed class SvnPlugin : IPolyMountPlugin, IPluginMetadata
{
    public LocalizedText Title => SvnTexts.Get("svn.title");
    public LocalizedText Description => SvnTexts.Get("svn.description");
    public string Icon => "repository";

    /// <summary>The commands this plugin answers on an instance's own pipe. Each operation below
    /// is the front-end side of one of them; both live here so the pair cannot drift.</summary>
    internal const string SourceCommand = "svn.source";
    internal const string LanesCommand = "svn.lanes";
    internal const string FamiliesCommand = "game-engine.families";

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

        // The package-family prefetcher. It lives inside this plugin rather than in one of its
        // own because it is only ever useful in front of a Subversion source: a content
        // repository laid out as package directories, opened family by family during a start-up.
        // Reading the learned state out of the prediction layer is a question for the instance,
        // so it is a command; Core knows neither the strategy nor the shape of the answer.
        context.RegisterCommand(FamiliesCommand, command =>
        {
            var model = FindFamilyModel(command.Instance);
            command.Response.Result = JsonSerializer.Serialize(new
            {
                instance = command.Instance.Name,
                families = model?.Snapshot() ?? Array.Empty<ManifestFamilyPrediction.FamilySnapshot>()
            }, Json);
        });

        // A credential that is not a secret at all: the account the Subversion client on this
        // machine already saved. Whoever signs in with TortoiseSVN or the svn command line once
        // does not have to type the password into PolyMount as well, and the password never
        // reaches this product's configuration or its store.
        context.RegisterCredentialKind(new CredentialKindDescriptor
        {
            Kind = SubversionCacheCredential.KindName,
            Title = SvnTexts.Get("svn.credential.cached.title"),
            Description = SvnTexts.Get("svn.credential.cached.description"),
            // A tortoise, because the record says whose TortoiseSVN saved it: a list of accounts is
            // scanned, and the one thing that tells this kind apart from a password is the program
            // it belongs to.
            Icon = "tortoise",
            // Whose cache. Windows protects the password with the key of the account that saved
            // it, and a service runs as an account of its own, so "the cache on this machine" is
            // never enough to say: the record names a person. The form starts it at %USERNAME%,
            // the person filling it, and an empty value keeps the old meaning — this process's own
            // cache — which is right when the service already runs as that person.
            AccountField = SubversionCacheCredential.AccountField,
            Fields = new()
            {
                new()
                {
                    Path = SubversionCacheCredential.AccountField,
                    Title = SvnTexts.Get("svn.credential.cached.account.title"),
                    Description = SvnTexts.Get("svn.credential.cached.account.description"),
                    Placeholder = SvnTexts.Get("svn.credential.cached.account.placeholder"),
                    DefaultValue = "%USERNAME%"
                }
            },
            // No endpoint and no realm: which repository credential comes out of the cache is
            // decided when a layer is built, because the client keys its cache by realm and a
            // credential record cannot know which repository it will be used for.
            Create = values => new SubversionCacheCredential
            {
                Account = values.GetValueOrDefault(SubversionCacheCredential.AccountField, "").Trim()
            }
        });

        // What this source can present, asked with the credential itself: an account for a
        // repository that asks for one, a certificate for one behind mutual TLS, and the client's
        // own cache — a record that carries no secret and becomes an account when a repository is
        // built, so the transport can present it too. The parts are what is asked, not one object:
        // a certificate carries the account it is presented with as a record of its own.
        context.RegisterCredentialConsumer("svn", credential =>
            CredentialParts.Of(credential).Any(part =>
                part is IPasswordAuthentication or ICertificateAuthentication or SubversionCacheCredential)
            || CredentialParts.Installed(credential) != null);

        context.RegisterControlOperation(new ControlOperation(
            Name: "list_manifest_families",
            Title: "List manifest families",
            Description: "What the manifest prefetcher has learned about one instance's package families: manifests issued, read, and wasted per family, and whether unread guesses closed it. Empty until the instance has served a start-up.",
            Effect: ControlEffect.Read,
            Idempotent: true,
            InputSchema: """{"type":"object","properties":{"instance":{"type":"string","description":"Instance name, as list_instances reports it."}},"required":["instance"]}""",
            Invoke: (link, arguments) => link.Instance(arguments.String("instance"),
                new ControlRequest { Cmd = FamiliesCommand }),
            Present: response => JsonNode.Parse(string.IsNullOrWhiteSpace(response.Result) ? "{}" : response.Result)));

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

        // The strategy runs inside the shared prediction layer, so it inherits that layer's
        // held cache, quota, read lane, feedback and write invalidation. `Strategies` selects it
        // by name next to the built-in ones.
        context.RegisterPredictionStrategy("manifests", strategy => new ManifestFamilyPrediction(strategy));

        // One preset per source type: the chain a Subversion workspace wants, carrying the
        // capacities and read-ahead settings a content-repository start-up was measured with.
        // A second, separately tuned preset only invited people to pick the wrong one.
        context.RegisterPreset(new()
        {
            Id = "svn-workspace",
            Titles = SvnTexts.All("preset.svn-workspace.title"),
            Layers = new()
            {
                // Below the warm-up: warming RAM with listings would spend this budget on
                // records the first client enumeration fills on demand anyway, and evict the
                // file blocks the cache exists for. A gigabyte of blocks in front of the
                // durable cache is what the start-up was measured with.
                new() { Id = "memory-cache", Type = "memory-cache", Parameters = new()
                {
                    ["CapacityBytes"] = JsonSerializer.SerializeToElement(1024L * 1024 * 1024),
                    ["BlockSize"] = JsonSerializer.SerializeToElement("65536")
                } },
                // Above the overlay, so the walk still sees local deletions. Here it fills the
                // durable cache — the one that answers a restart.
                new() { Id = "warmup", Type = "warmup", Parameters = new()
                {
                    ["Limit"] = JsonSerializer.SerializeToElement(10000),
                    ["Seconds"] = JsonSerializer.SerializeToElement(180),
                    ["QuietMs"] = JsonSerializer.SerializeToElement(250)
                } },
                // Predictions read through the overlay, so a guess sees local writes. The
                // durable read cache sits below it (it caches the chain's view) but above the
                // overlay, which is the only layer that accepts writes and therefore sits
                // directly above the source. Only the manifest prefetcher runs: the general
                // strategies measured no gain on this load and spend the budget on guesses.
                new() { Id = "predict", Type = "prediction-cache", Parameters = new()
                {
                    ["CapacityBytes"] = JsonSerializer.SerializeToElement(256L * 1024 * 1024),
                    ["Strategies"] = JsonSerializer.SerializeToElement("manifests")
                } },
                // 16 GiB, the size the start-up was measured with: the durable cache is what
                // answers the next start, and the manifests the strategy prefetches are only
                // useful if they are still there when the program asks.
                new() { Id = "cache", Type = "cache", CacheCapacityBytes = 16L * 1024 * 1024 * 1024,
                    Parameters = new() { ["Writable"] = JsonSerializer.SerializeToElement(false) } },
                new() { Id = "overlay", Type = "overlay" },
                // Directory enumeration is one round trip each; fetching the next level while
                // the caller reads the current one is what keeps a cold walk linear in depth
                // rather than in directory count. The values are the ones the start-up was
                // measured with.
                new() { Id = "svn", Type = "svn", Source = new()
                {
                    Type = "svn",
                    Parameters = new()
                    {
                        ["PrefetchConcurrency"] = JsonSerializer.SerializeToElement(2),
                        ["PrefetchQueueDepth"] = JsonSerializer.SerializeToElement(64),
                        ["PrefetchChildren"] = JsonSerializer.SerializeToElement(32),
                        ["SpeculativeConcurrency"] = JsonSerializer.SerializeToElement(2)
                    }
                } }
            }
        });
        context.RegisterSource("svn", (config, state) =>
            SvnLayer.Create(config.Url, config.TrustServerCertFailures, Path.Combine(state, "pin.txt"), config.Username,
                config.Password, SvnPrefetchOptions.From(config.Parameters), config.CredentialId,
                config.AllowInsecureTransport, config.VerifyOnly),
            config =>
            {
                if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var repository) || config.PinnedRevision < 0)
                    throw new ArgumentException("SVN requires an absolute repository URL and nonnegative revision");
                // The transport speaks HTTP/WebDAV. svn:// and svn+ssh:// are the svnserve
                // protocol, which only the command-line client spoke, and it is no longer shipped.
                if (repository.Scheme != Uri.UriSchemeHttp && repository.Scheme != Uri.UriSchemeHttps)
                    throw new ArgumentException("only http(s) repositories are supported; '" + repository.Scheme +
                        "://' needs the svn command-line client, which this build no longer includes");
                // Plain http is the transport without a server identity: the listing becomes the
                // mounted files and the account goes with the request. It is allowed only where the
                // source says so, which is a decision written in the configuration rather than one
                // taken by default.
                if (repository.Scheme == Uri.UriSchemeHttp && !config.AllowInsecureTransport)
                    throw new ArgumentException("'" + config.Url + "' is a plain http repository; set "
                        + "Source.AllowInsecureTransport to use an unencrypted transport deliberately");
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
                    new() { Path = "Source.TrustServerCertFailures", Title = SvnTexts.Get("svn.field.trust"),
                        Description = SvnTexts.Get("svn.field.trust.description") },
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
                Actions = new() { new() { Command = PolyMount.Plugins.Ipc.ControlCommands.RefreshHead,
                    Field = "Source.PinnedRevision",
                    Title = SvnTexts.Get("svn.action.refreshHead"),
                    Confirmation = SvnTexts.Get("svn.action.refreshHead.confirmation") } }
            });
    }

    /// <summary>The family model inside an instance's prediction layer: this plugin's strategy,
    /// or the one inside a composite when several strategies share the layer.</summary>
    private static ManifestFamilyPrediction? FindFamilyModel(IPluginInstance instance)
    {
        foreach (var layer in instance.Layers.Values.OfType<IPredictionModelHost>())
            if (FindStrategy(layer.Strategy) is { } model) return model;
        return null;
    }

    private static ManifestFamilyPrediction? FindStrategy(IPredictionModel? model) => model switch
    {
        ManifestFamilyPrediction family => family,
        CompositePrediction composite => composite.Models.Select(FindStrategy).FirstOrDefault(found => found != null),
        _ => null
    };

    /// <summary>The instance's SVN layers, keyed by the configuration id the status reports.
    /// <paramref name="only"/> narrows the answer to one layer, or to none when that id is not
    /// an SVN layer of this instance.</summary>
    private static IEnumerable<(string Id, SvnLayer Layer)> SvnLayers(IPluginInstance instance, string? only)
    {
        foreach (var pair in instance.Layers)
        {
            if (pair.Value is not SvnLayer layer) continue;
            if (!string.IsNullOrEmpty(only) && !string.Equals(pair.Key, only, StringComparison.OrdinalIgnoreCase)) continue;
            yield return (pair.Key, layer);
        }
    }

    private static object Describe(IPluginInstance instance, string? only)
    {
        var configs = instance.LayerConfigs.ToDictionary(layer => layer.Id, StringComparer.OrdinalIgnoreCase);
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
        return new { instance = instance.Name, layers };
    }

    private static object LaneReport(IPluginInstance instance, string? only) => new
    {
        instance = instance.Name,
        layers = SvnLayers(instance, only).Select(entry => new { layer = entry.Id, lanes = entry.Layer.Lanes.Snapshot() }).ToArray()
    };
}
