using System.Text.Json;
using System.Text.Json.Nodes;
using PolyMount.Core;
using PolyMount.Core.Ipc;

namespace PolyMount.Plugins.Smb;

/// <summary>
/// Reads a folder on another machine as a mount source.
///
/// Setup on the far side is a share and an account that may read it — nothing else is installed
/// there. What this plugin owns is the part that is specific to SMB: the session (an account the
/// service presents, since session 0 has none of its own), the generation a remote folder can
/// offer, and the diagnostics that say which of "unreachable", "refused" and "gone" happened.
/// </summary>
public sealed class SmbPlugin : IPolyMountPlugin, IPluginMetadata
{
    public LocalizedText Title => SmbTexts.Get("smb.title");
    public LocalizedText Description => SmbTexts.Get("smb.description");
    public string Icon => "folder";

    /// <summary>The command this plugin answers on an instance's own pipe; the operation below
    /// is the front-end side of the same answer, and both live here so they cannot drift.</summary>
    internal const string SourceCommand = "smb.source";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public void Register(IPluginContext context)
    {
        context.RegisterCommand(SourceCommand, command =>
            command.Response.Result = JsonSerializer.Serialize(Describe(command.Instance, command.Request.Layer), Json));

        context.RegisterControlOperation(new ControlOperation(
            Name: "get_smb_source",
            Title: "Get remote folder source",
            Description: "What an instance's remote-folder layers are doing: the folder and share they map, the account they present (username only, null when the service account is used), whether the session is established, the generation caches key on and how old that reading is.",
            Effect: ControlEffect.Read,
            Idempotent: true,
            InputSchema: """{"type":"object","properties":{"instance":{"type":"string","description":"Instance name, as list_instances reports it."},"layer":{"type":"string","description":"Remote-folder layer id, as get_instance lists it under providers[].name. Omit for every one."}},"required":["instance"]}""",
            Invoke: (link, arguments) => link.Instance(arguments.String("instance"),
                new ControlRequest { Cmd = SourceCommand, Layer = arguments.OptionalString("layer") }),
            Present: response => JsonNode.Parse(string.IsNullOrWhiteSpace(response.Result) ? "{}" : response.Result)));

        context.RegisterPreset(new()
        {
            Id = "smb-workspace",
            Titles = SmbTexts.All("preset.smb-workspace.title"),
            Layers = new()
            {
                // The same chain the SVN workspace uses, minus the prediction layer: there is no
                // revision to learn a walk from, and a folder that costs one round trip per
                // listing is what the warm-up layer is for.
                new() { Id = "memory-cache", Type = "memory-cache" },
                new() { Id = "warmup", Type = "warmup" },
                // Above the overlay, below the warm-up layer: it caches the chain's view, and the
                // overlay is the only layer that accepts writes, so it sits directly on the source.
                new() { Id = "cache", Type = "cache", Parameters = new()
                    { ["Writable"] = System.Text.Json.JsonSerializer.SerializeToElement(false) } },
                new() { Id = "overlay", Type = "overlay" },
                new() { Id = "smb", Type = "smb", Source = new() { Type = "smb" } }
            }
        });

        context.RegisterSource("smb",
            (config, _) => SmbLayer.Create(config),
            config =>
            {
                SmbPath.Parse(config.Path);
                SmbOptions.From(config.Parameters);
                // A certificate credential cannot open a share; refusing it here is better than
                // building a layer that will present an empty password.
                if (!string.IsNullOrWhiteSpace(config.CredentialId) &&
                    CredentialStore.FindById(config.CredentialId) is { } credential &&
                    credential.Kind != CredentialTypes.Password)
                    throw new ArgumentException("a share is opened with an account and password; choose a password credential");
            },
            new LayerDescriptor
            {
                Type = "smb", Title = SmbTexts.Get("smb.title"), Icon = "folder",
                Description = SmbTexts.Get("smb.description"),
                Fields = new()
                {
                    new() { Path = "Source.Path", Title = SmbTexts.Get("smb.field.path"), Required = true,
                        Placeholder = SmbTexts.Get("smb.field.path.placeholder"),
                        Description = SmbTexts.Get("smb.field.path.description") },
                    // The service runs in session 0 and has no connection of its own, so the
                    // account is what decides whether the share answers. It is a picker and not a
                    // username field for the same reason SVN's is: a password typed into a layer
                    // would be written to the configuration file.
                    new() { Path = "Source.CredentialId", Kind = LayerFieldKind.Credential,
                        CredentialKinds = new() { CredentialTypes.Password },
                        Title = SmbTexts.Get("smb.field.credential"),
                        Placeholder = SmbTexts.Get("smb.field.credential.placeholder"),
                        Description = SmbTexts.Get("smb.field.credential.description") },
                    // The generation is read for every cache lookup the layers above make, so it
                    // is read from the share once per interval. Zero would mean one round trip per
                    // lookup.
                    new() { Path = "Source.Parameters.GenerationTtlSeconds", Kind = LayerFieldKind.Integer,
                        Minimum = SmbOptions.MinimumTtlSeconds, Maximum = SmbOptions.MaximumTtlSeconds,
                        DefaultValue = SmbOptions.DefaultGenerationTtlSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Title = SmbTexts.Get("smb.field.ttl"),
                        Description = SmbTexts.Get("smb.field.ttl.description") }
                }
            });
    }

    /// <summary>The instance's remote-folder layers, keyed by the configuration id the status
    /// reports. <paramref name="only"/> narrows the answer to one layer.</summary>
    private static IEnumerable<(string Id, SmbLayer Layer)> Layers(MountInstance instance, string? only)
    {
        foreach (var pair in instance.Layers)
        {
            if (pair.Value is not SmbLayer layer) continue;
            if (!string.IsNullOrEmpty(only) && !string.Equals(pair.Key, only, StringComparison.OrdinalIgnoreCase)) continue;
            yield return (pair.Key, layer);
        }
    }

    private static object Describe(MountInstance instance, string? only) => new
    {
        instance = instance.Profile.Name,
        layers = Layers(instance, only).Select(entry => entry.Layer.Describe(entry.Id)).ToArray()
    };
}
