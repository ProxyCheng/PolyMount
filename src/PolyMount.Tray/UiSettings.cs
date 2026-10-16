using System.Text.Json;

namespace PolyMount.Tray;

/// <summary>The tray's own preferences, in one file beside the language: what the reader chose -
/// which language, which scheme - as opposed to what the page last happened to be showing, which
/// is `stats.json`.
///
/// It exists as a helper because two features share the file and each used to write the whole of
/// it: a language change rewrote the settings as <c>{ Language }</c>, so anything else kept there
/// would quietly disappear the first time somebody switched language. Setting one key edits the
/// document rather than replacing it.</summary>
internal static class UiSettings
{
    internal static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PolyMount", "ui.json");

    /// <summary>One key, or null when the file is missing, unreadable or does not carry it.</summary>
    internal static string? Get(string key)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path));
            return json.RootElement.TryGetProperty(key, out var value) ? value.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>Sets one key, keeping whatever else the file holds.</summary>
    internal static void Set(string key, string value)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path));
            foreach (var property in json.RootElement.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String)
                    settings[property.Name] = property.Value.GetString() ?? "";
        }
        catch { /* a first run, or a file somebody edited: start from what is known */ }
        settings[key] = value;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(settings));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
