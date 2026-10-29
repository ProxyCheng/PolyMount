using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PolyMount.Plugins;

/// <summary>Portable UI metadata. Plugins supply data, never frontend widgets.</summary>
public sealed class LocalizedText
{
    public Dictionary<string, string> Values { get; set; } = new();
    public LocalizedText() { }
    public LocalizedText(string en, string zh) => Values = new() { ["en"] = en, ["zh-CN"] = zh };
    /// <summary>A hint that reads the same in every language, such as a list of keywords.</summary>
    public LocalizedText(string text) => Values = new() { ["en"] = text, ["zh-CN"] = text };
    public string Resolve(string language) => Values.GetValueOrDefault(language) ?? Values.GetValueOrDefault("en") ?? Values.Values.FirstOrDefault() ?? "";
}

public enum LayerFieldKind { Text, Secret, Integer, Boolean, Choice, Credential }

public sealed class LayerFieldChoice
{
    public string Value { get; set; } = "";
    public LocalizedText Title { get; set; } = new();
}

public sealed class LayerFieldDescriptor
{
    // Paths address LayerConfig JSON, e.g. Source.Url or Parameters.Endpoint.
    public string Path { get; set; } = "";
    public LocalizedText Title { get; set; } = new();
    public LocalizedText Description { get; set; } = new();
    public LocalizedText Placeholder { get; set; } = new();
    public LayerFieldKind Kind { get; set; }
    public string? DefaultValue { get; set; }
    public bool Required { get; set; }
    public bool Trim { get; set; } = true;
    public bool EmptyIsNull { get; set; }
    public long Scale { get; set; } = 1;
    public long? Minimum { get; set; }
    public long? Maximum { get; set; }
    public List<LayerFieldChoice> Choices { get; set; } = new();
}

public sealed class LayerActionDescriptor
{
    public string Command { get; set; } = "";
    public LocalizedText Title { get; set; } = new();
    public LocalizedText Confirmation { get; set; } = new();
    /// <summary>The property field this action belongs to, by its descriptor path (for example
    /// <c>Source.PinnedRevision</c>). A front-end renders such an action beside that field's
    /// editor, where its result lands; an empty path means a layer-wide action and belongs at
    /// the bottom of the panel.</summary>
    public string Field { get; set; } = "";
}

public sealed class LayerDescriptor
{
    public string Type { get; set; } = "";
    public LocalizedText Title { get; set; } = new();
    public LocalizedText Description { get; set; } = new();
    public string Icon { get; set; } = "layers";
    public List<LayerFieldDescriptor> Fields { get; set; } = new();
    public List<LayerActionDescriptor> Actions { get; set; } = new();
}

/// <summary>Shared JSON binding and validation; works with plugin-defined parameter keys.</summary>
public static class LayerProperties
{
    public static string Read(LayerConfig config, LayerFieldDescriptor field)
    {
        JsonNode? node = JsonSerializer.SerializeToNode(config);
        foreach (string part in field.Path.Split('.')) node = node?[part];
        if (node == null) return field.DefaultValue ?? "";
        if (field.Kind == LayerFieldKind.Integer && field.Scale > 1)
            return (node.GetValue<long>() / field.Scale).ToString(CultureInfo.InvariantCulture);
        return node.ToString();
    }

    public static void Apply(LayerConfig config, IEnumerable<(LayerFieldDescriptor Field, string Value)> values)
    {
        var root = JsonSerializer.SerializeToNode(config)!.AsObject();
        foreach (var (field, input) in values)
        {
            string value = field.Trim ? input.Trim() : input;
            JsonNode? node;
            if (field.Required && string.IsNullOrWhiteSpace(value) && config.Enabled) throw new ArgumentException(field.Path + " is required");
            switch (field.Kind)
            {
                case LayerFieldKind.Integer:
                    if (field.Scale <= 0 || !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) ||
                        number < field.Minimum || number > field.Maximum || number > long.MaxValue / field.Scale || number < long.MinValue / field.Scale)
                    {
                        if (!config.Enabled) continue;
                        throw new ArgumentException("Invalid integer: " + field.Path);
                    }
                    node = JsonValue.Create(checked(number * field.Scale));
                    break;
                case LayerFieldKind.Boolean:
                    if (!bool.TryParse(value, out bool flag)) throw new ArgumentException("Invalid boolean: " + field.Path);
                    node = JsonValue.Create(flag);
                    break;
                case LayerFieldKind.Choice:
                    if (config.Enabled && !field.Choices.Any(c => c.Value == value)) throw new ArgumentException("Invalid choice: " + field.Path);
                    node = JsonValue.Create(value);
                    break;
                default:
                    node = field.EmptyIsNull && value.Length == 0 ? null : JsonValue.Create(value);
                    break;
            }
            string[] parts = field.Path.Split('.');
            if (parts.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Invalid field path");
            var parent = root;
            foreach (string part in parts[..^1])
            {
                if (parent[part] == null) parent[part] = new JsonObject();
                parent = parent[part]!.AsObject();
            }
            parent[parts[^1]] = node;
        }
        var updated = root.Deserialize<LayerConfig>()!;
        foreach (var property in typeof(LayerConfig).GetProperties()) property.SetValue(config, property.GetValue(updated));
    }
}
