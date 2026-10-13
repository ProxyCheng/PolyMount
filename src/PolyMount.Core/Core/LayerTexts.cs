using System.Text.Json;

namespace PolyMount.Core;

internal static class LayerTexts
{
    private static readonly Dictionary<string, Dictionary<string, string>> Texts = Load();
    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        using var stream = typeof(LayerTexts).Assembly.GetManifestResourceStream("PolyMount.LayerTexts")
            ?? throw new InvalidOperationException("Missing layer text resource");
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream)
            ?? throw new InvalidDataException("Invalid layer text resource");
    }
    public static LocalizedText Get(string key) => new() { Values = new(Texts[key]) };
}
