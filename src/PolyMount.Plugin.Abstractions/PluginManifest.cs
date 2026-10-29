using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PolyMount.Plugins;

/// <summary>What a plugin says about itself before any of its code runs.
///
/// A manifest is authored beside the plugin's project and carried by the SDK: embedded into the
/// assembly as a resource, so a plugin is one file to copy and one file to keep current — the
/// `<c>plugin.json</c>` beside the project is what its author edits, and the copy shipped is the
/// resource. (A manifest beside the assembly is still read, which is what every plugin published
/// before the resource existed looks like.) Everything in it is a fact the host needs
/// <em>before</em> loading: which plugin this is, what to call it in a list, and what it may reach
/// for. A disabled plugin is not loaded at all — that is the point of disabling it — so without a
/// manifest its row could only show a file name.
///
/// It is not a permission system. A plugin runs with the service account's rights and can do
/// whatever that account can; <see cref="Permissions"/> is a declaration shown to the person
/// enabling it, in the same spirit as a package manager listing what it will touch. The two real
/// boundaries are elsewhere: the assembly has to be inside the installation directory, and the
/// contract version has to match.
/// </summary>
public sealed class PluginManifest
{
    /// <summary>The manifest's own format. A version the host does not know is refused rather
    /// than half understood.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>The plugin's identifier: stable across versions, unique among what is installed,
    /// and what a person would recognise. Lowercase, digits, dots and hyphens.</summary>
    public string Id { get; set; } = "";

    public Dictionary<string, string> Name { get; set; } = new();
    public Dictionary<string, string> Description { get; set; } = new();
    public string Icon { get; set; } = "plugin";
    public string Publisher { get; set; } = "";
    public string License { get; set; } = "";

    /// <summary>What the plugin reaches for, in the product's own vocabulary: <c>network</c>,
    /// <c>filesystem</c>, <c>registry</c>, <c>native</c>. Unknown words are kept and shown — a
    /// plugin that says it does something this host has no word for is still worth telling the
    /// reader about.</summary>
    public List<string> Permissions { get; set; } = new();

    /// <summary>Which of the product's processes need this plugin's code: <c>backend</c> (servers —
    /// layers, sources, commands) or <c>frontend</c> (tools — control operations). Absent means
    /// both, which is what every plugin written before roles existed did. See
    /// <see cref="PluginRoles"/>.</summary>
    public List<string>? Roles { get; set; }

    /// <summary>The declaration, parsed. Both when the manifest names none.</summary>
    [JsonIgnore]
    public PluginRoles RoleSet => Names_ ??= ResolveRoles();
    private PluginRoles? Names_;

    private PluginRoles ResolveRoles() =>
        PluginRoleNames.TryParse(Roles, out var parsed, out _) ? parsed : PluginRoles.Both;

    /// <summary>The host versions this plugin was tested against, as a declaration only. Nothing
    /// refuses a load on it: the product's versions move every release and a plugin that stopped
    /// working would say so by failing, while the contract version — which is checked — is what
    /// actually decides whether the code can run.</summary>
    [JsonPropertyName("host")]
    public string HostRange { get; set; } = "";

    /// <summary>The title in the reader's language, or an empty string when the manifest names
    /// none.</summary>
    public LocalizedText Title => new() { Values = Name };
    public LocalizedText Detail => new() { Values = Description };

    /// <summary>Where a plugin's manifest lives: beside its assembly, named after it.</summary>
    public static string PathFor(string assemblyPath) =>
        Path.Combine(Path.GetDirectoryName(assemblyPath) ?? "",
            Path.GetFileNameWithoutExtension(assemblyPath) + ".plugin.json");

    /// <summary>Reads a plugin's manifest: the resource embedded in its assembly, or — for a
    /// plugin published before that existed — the file beside it. Returns null when there is
    /// none, which is what a plugin built before manifests existed looks like; a manifest that
    /// exists but cannot be used is null with <paramref name="error"/> set.</summary>
    public static PluginManifest? Read(string assemblyPath, out string error)
    {
        error = "";
        string path = PathFor(assemblyPath);
        string? text = File.Exists(path) ? File.ReadAllText(path) : ReadEmbedded(assemblyPath);
        if (text == null) return null;
        PluginManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(text, Json);
        }
        catch (Exception ex)
        {
            error = Path.GetFileName(path) + ": " + ex.Message;
            return null;
        }
        if (manifest == null)
        {
            error = Path.GetFileName(path) + ": the file is empty";
            return null;
        }
        if (manifest.SchemaVersion is < 1 or > 1)
        {
            error = Path.GetFileName(path) + $": schemaVersion {manifest.SchemaVersion} is not one this host knows";
            return null;
        }
        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            error = Path.GetFileName(path) + ": the manifest has no id";
            return null;
        }
        // Refused rather than read as "both": a role the host does not know would otherwise load
        // the plugin somewhere its code is never reached, and nothing would say so.
        if (!PluginRoleNames.TryParse(manifest.Roles, out _, out string roleError))
        {
            error = Path.GetFileName(path) + ": " + roleError;
            return null;
        }
        return manifest;
    }

    /// <summary>The resource a plugin's manifest is embedded under. Named by the contract rather
    /// than after the assembly: one plugin per assembly, one manifest per assembly.</summary>
    public const string EmbeddedResourceName = "PolyMount.Plugin.Manifest";

    /// <summary>The embedded manifest, or null when the assembly carries none.
    ///
    /// Read out of the file's metadata instead of loading it: a plugin is one file now, and one
    /// this host may have to describe without ever running it — a plugin built against another
    /// contract is exactly the assembly that must not be loaded.</summary>
    private static string? ReadEmbedded(string assemblyPath)
    {
        try
        {
            using var stream = File.Open(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata || pe.PEHeaders.CorHeader is not { } cor) return null;
            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.ManifestResources)
            {
                var resource = reader.GetManifestResource(handle);
                if (!reader.StringComparer.Equals(resource.Name, EmbeddedResourceName)) continue;
                // In this assembly or nowhere: a link to another assembly's resource is not a
                // manifest anybody can rely on being beside the plugin.
                if (!resource.Implementation.IsNil) return null;
                var blob = pe.GetSectionData(cor.ResourcesDirectory.RelativeVirtualAddress).GetReader();
                blob.Offset = (int)resource.Offset;
                int length = blob.ReadInt32();
                return Encoding.UTF8.GetString(blob.ReadBytes(length));
            }
        }
        catch (Exception) { /* not an assembly, or not one this account can read */ }
        return null;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}
