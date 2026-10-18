using System.Text.Json;

namespace PolyMount.Core.Ipc;

/// <summary>
/// One tool call's arguments, read as the types the operation declares them to be.
///
/// The wire carries them as loosely typed JSON — a model assembles an object, and may leave a
/// field out, spell it differently, or put a string where a number belongs. Reading them through
/// this class is what turns those into one sentence naming the argument that was wrong, rather
/// than a <see cref="JsonException"/> or, worse, a default that silently means something else:
/// a missing revision must not become revision zero, and a missing "enabled" must not become
/// "disabled".
/// </summary>
public sealed class ToolArguments
{
    private readonly JsonElement _root;

    public ToolArguments(JsonElement root) => _root = root;

    /// <summary>The arguments of a call that has none: every read of a required one fails.</summary>
    public static ToolArguments None { get; } = new(default);

    /// <summary>Whether the call supplied this argument at all, with a non-null value.</summary>
    public bool Has(string name) => TryGet(name, out var value) && value.ValueKind != JsonValueKind.Null;

    public string String(string name) => As(name, "a string", JsonValueKind.String).GetString()!;

    public string? OptionalString(string name) =>
        Has(name) ? As(name, "a string", JsonValueKind.String).GetString() : null;

    public long Long(string name) => As(name, "an integer", JsonValueKind.Number).GetInt64();

    public double Double(string name) => As(name, "a number", JsonValueKind.Number).GetDouble();

    public bool Bool(string name) => As(name, "a boolean", JsonValueKind.True, JsonValueKind.False).GetBoolean();

    public bool? OptionalBool(string name) =>
        Has(name) ? As(name, "a boolean", JsonValueKind.True, JsonValueKind.False).GetBoolean() : null;

    /// <summary>An array of strings, in the order the caller gave them. The order matters for
    /// the layer-chain operations, where it is checked against the chain the service holds.</summary>
    public IReadOnlyList<string> StringList(string name)
    {
        var value = As(name, "an array of strings", JsonValueKind.Array);
        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"every item of argument '{name}' must be a string");
            list.Add(item.GetString()!);
        }
        return list;
    }

    /// <summary>A structured argument, as the JSON text the control protocol carries. A string is
    /// taken at its word — a caller that already serialized the value should not have it quoted
    /// a second time — and anything else is serialized back.</summary>
    public string Json(string name)
    {
        var value = Required(name);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
    }

    private JsonElement As(string name, string expected, params JsonValueKind[] kinds)
    {
        var value = Required(name);
        foreach (var kind in kinds)
            if (value.ValueKind == kind) return value;
        throw new ArgumentException($"argument '{name}' must be {expected}");
    }

    private JsonElement Required(string name)
    {
        if (!Has(name)) throw new ArgumentException($"missing required argument '{name}'");
        return _root.GetProperty(name);
    }

    private bool TryGet(string name, out JsonElement value)
    {
        value = default;
        if (_root.ValueKind != JsonValueKind.Object) return false;
        return _root.TryGetProperty(name, out value);
    }
}
