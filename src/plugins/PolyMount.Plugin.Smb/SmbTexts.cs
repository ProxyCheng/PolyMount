using System.Text.Json;
using PolyMount.Plugins;

namespace PolyMount.Plugins.Smb;

/// <summary>
/// Localized text for this plugin's descriptors. The strings live in the embedded resource so no
/// user-visible text is written in code; the keys are resolved like Core's own.
/// </summary>
internal static class SmbTexts
{
    private static readonly Dictionary<string, Dictionary<string, string>> Texts = Load();

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        using var stream = typeof(SmbTexts).Assembly.GetManifestResourceStream("PolyMount.Plugin.Smb.Texts")
            ?? throw new InvalidOperationException("Missing plugin text resource");
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream)
            ?? throw new InvalidDataException("Invalid plugin text resource");
    }

    public static LocalizedText Get(string key) => new() { Values = All(key) };
    public static Dictionary<string, string> All(string key) => new(Texts[key]);
}
