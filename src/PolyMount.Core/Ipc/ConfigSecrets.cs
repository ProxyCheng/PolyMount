using System.Text.Json;
using System.Text.Json.Nodes;

namespace PolyMount.Core.Ipc;

/// <summary>The secret-shaped fields of a configuration document, and the round trip that lets a
/// caller read the configuration and hand it back without erasing what it could not read.
///
/// A password belongs to the service. The tray sets one and then never needs to see it again, an
/// agent must never have it in a transcript, and the control pipe carries the whole document to
/// both — so the document leaves with <c>[redacted]</c> in those fields, and a save that still
/// says <c>[redacted]</c> puts back what the service already holds, matched by the instance and
/// layer identities that never change.
///
/// This lives here rather than in the MCP front-end because the front-end was not the only door:
/// the same document crosses the raw control pipe to the tray, the Explorer overlay and any
/// script, and a redaction that only one of them applies is a redaction of nothing.</summary>
public static class ConfigSecrets
{
    /// <summary>The sentinel a secret is replaced with on the way out.</summary>
    public const string Redacted = "[redacted]";

    /// <summary>The source fields whose value must never travel over the configuration API.
    /// Every one of them is set from the tray and kept redacted everywhere else.</summary>
    public static readonly string[] Fields = { "Password", "ClientCertificatePassword" };

    /// <summary>Replaces every stored secret in a serialized configuration with the sentinel.
    /// A document that does not parse is returned unchanged: this is a display transform, and a
    /// caller that handed over garbage has a larger problem than a hidden password.</summary>
    public static string RedactJson(string json)
    {
        JsonNode? config;
        try { config = JsonNode.Parse(json); }
        catch (JsonException) { return json; }
        return ConfigSecrets.Redact(config)?.ToJsonString() ?? json;
    }

    public static JsonNode? Redact(JsonNode? config)
    {
        foreach (var mount in Mounts(config))
        {
            RedactSource(mount["Source"] as JsonObject);
            foreach (var layer in Layers(mount))
                RedactSource(layer["Source"] as JsonObject);
        }
        return config;
    }

    private static void RedactSource(JsonObject? source)
    {
        foreach (string field in Fields)
            if (source?[field] is JsonValue value &&
                value.TryGetValue<string>(out var secret) &&
                !string.IsNullOrEmpty(secret))
                source[field] = Redacted;
    }

    /// <summary>Puts back the secrets the outgoing document had taken out. Only the sentinel is
    /// replaced — a real value in the incoming document is written as it came, which is how the
    /// tray sets a password in the first place.</summary>
    public static void RestoreRedacted(JsonNode incoming, JsonNode? current)
    {
        var priorMounts = Mounts(current).ToList();
        foreach (var mount in Mounts(incoming))
        {
            var prior = MatchMount(mount, priorMounts);
            foreach (string field in Fields)
                RestoreField(mount["Source"] as JsonObject, prior?["Source"] as JsonObject, field);

            foreach (var layer in Layers(mount))
                foreach (string field in Fields)
                    RestoreField(layer["Source"] as JsonObject, MatchLayer(layer, prior)?["Source"] as JsonObject, field);
        }
    }

    /// <summary>Whether the incoming document still carries the sentinel for a field that has
    /// nothing stored behind it. The MCP surface refuses that rather than writing the sentinel as
    /// a password; the tray lets it through, because a tray that shows the sentinel offers a field
    /// to type into and an empty one is what the person meaning to clear it would send.</summary>
    public static void RestoreField(JsonObject? source, JsonObject? prior, string field)
    {
        if (source?[field] is not JsonValue value || !value.TryGetValue<string>(out var secret)) return;
        if (secret != Redacted) return;
        source[field] = prior?[field] is JsonValue stored && stored.TryGetValue<string>(out var original) &&
                        !string.IsNullOrEmpty(original)
            ? original
            : null;
    }

    public static IEnumerable<JsonObject> Mounts(JsonNode? config)
    {
        if (config is not JsonObject root || root["Mounts"] is not JsonArray mounts) yield break;
        foreach (var mount in mounts)
            if (mount is JsonObject obj) yield return obj;
    }

    public static IEnumerable<JsonObject> Layers(JsonObject mount)
    {
        if (mount["Layers"] is not JsonArray layers) yield break;
        foreach (var layer in layers)
            if (layer is JsonObject obj) yield return obj;
    }

    public static JsonObject? MatchMount(JsonObject mount, IReadOnlyList<JsonObject> prior)
    {
        string? id = StringOf(mount["Id"]);
        if (!string.IsNullOrEmpty(id))
            return prior.FirstOrDefault(candidate =>
                string.Equals(StringOf(candidate["Id"]), id, StringComparison.OrdinalIgnoreCase));

        string? name = StringOf(mount["Name"]);
        return name is null ? null : prior.FirstOrDefault(candidate =>
            string.Equals(StringOf(candidate["Name"]), name, StringComparison.OrdinalIgnoreCase));
    }

    public static JsonObject? MatchLayer(JsonObject layer, JsonObject? mount)
    {
        if (mount == null) return null;
        string? id = StringOf(layer["Id"]);
        if (string.IsNullOrEmpty(id)) return null;
        return Layers(mount).FirstOrDefault(candidate =>
            string.Equals(StringOf(candidate["Id"]), id, StringComparison.OrdinalIgnoreCase));
    }

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
