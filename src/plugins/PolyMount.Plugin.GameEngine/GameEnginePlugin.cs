using System.Text.Json;
using System.Text.Json.Nodes;
using PolyMount.Core;
using PolyMount.Core.Ipc;
using PolyMount.Layers;

namespace PolyMount.Plugins.GameEngine;

/// <summary>
/// A game-engine plugin: a prediction strategy that knows how content repositories lay their
/// packages out, and the preset that turns it on with the rest of the chain tuned for an editor
/// start-up against a Subversion source.
///
/// Nothing here is specific to one product. The layout the strategy understands — a repository
/// directory of package directories, each holding a manifest index, family names sharing a
/// prefix — is a configuration (see <see cref="ManifestFamilyOptions"/>), so an engine whose
/// packages are named differently changes parameters rather than writing another plugin.
/// </summary>
public sealed class GameEnginePlugin : IPolyMountPlugin
{
    /// <summary>The command this plugin answers on the instance's own pipe. The operation below
    /// is the front-end side of the same question; both live here so the pair cannot drift.</summary>
    internal const string FamiliesCommand = "game-engine.families";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public void Register(IPluginContext context)
    {
        // The instance side: read the strategy this plugin registered out of the prediction
        // layer and report what it has learned. Core knows neither the strategy nor the shape of
        // this answer; the payload travels as a JSON string.
        context.RegisterCommand(FamiliesCommand, command =>
        {
            var model = FindFamilyModel(command.Instance);
            command.Response.Result = JsonSerializer.Serialize(new
            {
                instance = command.Instance.Profile.Name,
                families = model?.Snapshot() ?? Array.Empty<ManifestFamilyPrediction.FamilySnapshot>()
            }, Json);
        });

        // The front-end side: an operation in the control catalog, so every surface built on it
        // (the MCP server among them) offers this as a tool. It runs in the front-end's process
        // and reaches the instance through the control link.
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

        // The strategy runs inside the shared prediction layer, so it inherits that layer's
        // held cache, quota, read lane, feedback and write invalidation. `Strategies` selects it
        // by name next to the built-in ones.
        context.RegisterPredictionStrategy("manifests", strategy => new ManifestFamilyPrediction(strategy));

        context.RegisterPreset(new()
        {
            Id = "game-engine-workspace",
            Titles = GameEngineTexts.All("preset.game-engine-workspace.title"),
            Layers = new()
            {
                // The measured start-up configuration: a gigabyte of block cache in front of the
                // durable one, listings warmed for three minutes, and a durable read cache that
                // never takes a write (the overlay is the only write target).
                new() { Id = "memory-cache", Type = "memory-cache", Parameters = new()
                {
                    ["CapacityBytes"] = JsonSerializer.SerializeToElement(1024L * 1024 * 1024),
                    ["BlockSize"] = JsonSerializer.SerializeToElement("65536")
                } },
                new() { Id = "warmup", Type = "warmup", Parameters = new()
                {
                    ["Limit"] = JsonSerializer.SerializeToElement(10000),
                    ["Seconds"] = JsonSerializer.SerializeToElement(180),
                    ["QuietMs"] = JsonSerializer.SerializeToElement(250)
                } },
                // Above the durable cache, so a guessed manifest fills the cache that answers the
                // next start; below the warm-up, so listing work stays in RAM.
                new() { Id = "predict", Type = "prediction-cache", Parameters = new()
                {
                    ["CapacityBytes"] = JsonSerializer.SerializeToElement(256L * 1024 * 1024),
                    ["Strategies"] = JsonSerializer.SerializeToElement("manifests")
                } },
                // 16 GiB, the size the start-up was measured with: the durable cache is what
                // answers the next start, and the manifests the strategy prefetches are only
                // useful if they are still there when the engine asks.
                new() { Id = "cache", Type = "cache", CacheCapacityBytes = 16L * 1024 * 1024 * 1024,
                    Parameters = new() { ["Writable"] = JsonSerializer.SerializeToElement(false) } },
                new() { Id = "overlay", Type = "overlay" },
                // Directory enumeration is one round trip each; fetching the next level while the
                // caller reads the current one is what keeps a cold walk linear in depth rather
                // than in directory count. The defaults are the ones the editor start-up was
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
    }

    /// <summary>The family model inside an instance's prediction layer: this plugin's strategy,
    /// or the one inside a composite when several strategies share the layer.</summary>
    private static ManifestFamilyPrediction? FindFamilyModel(MountInstance instance)
    {
        foreach (var layer in instance.Layers.Values.OfType<PredictionLayerBase>())
            if (FindStrategy(layer.Strategy) is { } model) return model;
        return null;
    }

    private static ManifestFamilyPrediction? FindStrategy(IPredictionModel? model) => model switch
    {
        ManifestFamilyPrediction family => family,
        CompositePrediction composite => composite.Models.Select(FindStrategy).FirstOrDefault(found => found != null),
        _ => null
    };
}
