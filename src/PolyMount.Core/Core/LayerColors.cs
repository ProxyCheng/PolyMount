using System.Globalization;

namespace PolyMount.Core;

/// <summary>
/// Assigns each layer a stable color. The color is derived deterministically from the layer's
/// identity (so it is consistent across processes and runs) unless the user supplies an override.
/// Used by the layer visualization and the Explorer badge/overlay.
/// </summary>
public static class LayerColors
{
    public static IReadOnlyList<string> OriginPalette { get; } = LoadPalette();
    private static IReadOnlyList<string> LoadPalette()
    {
        using var stream = typeof(LayerColors).Assembly.GetManifestResourceStream("PolyMount.OriginPalette")!;
        using var reader = new StreamReader(stream);
        return Array.AsReadOnly(System.Text.RegularExpressions.Regex.Matches(reader.ReadToEnd(), "0x([0-9A-F]{6})")
            .Select(m => "#" + m.Groups[1].Value).ToArray());
    }
    // A few hand-picked colors for the well-known built-in layers; everything else is hashed.
    private static readonly Dictionary<string, string> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["overlay"] = OriginPalette[0],
        ["cache"] = OriginPalette[1],
        ["svn"] = OriginPalette[2],
        ["dir"] = OriginPalette[3]
    };

    /// <summary>Deterministic auto color for an arbitrary layer key, as "#RRGGBB".</summary>
    public static string Auto(string key)
    {
        if (Defaults.TryGetValue(key, out var preset)) return preset;

        uint h = Fnv1a(key);
        double hue = h % 360;                 // spread across the wheel
        double sat = 0.60 + (h >> 9) % 25 / 100.0;  // 0.60..0.84
        double light = 0.45 + (h >> 17) % 15 / 100.0; // 0.45..0.59
        return HslToHex(hue, sat, light);
    }

    /// <summary>
    /// Resolve the color for a layer key, honoring a user override map when it contains a valid
    /// "#RRGGBB" value; otherwise falls back to <see cref="Auto"/>.
    /// </summary>
    public static string Resolve(string key, IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides != null && overrides.TryGetValue(key, out var c) && TryNormalizeHex(c, out var norm))
            return norm;
        return Auto(key);
    }

    public static bool TryNormalizeHex(string? value, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = value.Trim();
        if (s[0] != '#') s = "#" + s;
        if (s.Length != 7) return false;
        for (int i = 1; i < 7; i++)
            if (!Uri.IsHexDigit(s[i])) return false;
        normalized = "#" + s.Substring(1).ToUpperInvariant();
        return true;
    }

    private static uint Fnv1a(string s)
    {
        uint hash = 2166136261;
        foreach (var ch in s) { hash ^= ch; hash *= 16777619; }
        return hash;
    }

    private static string HslToHex(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        double m = l - c / 2;
        double r = 0, g = 0, b = 0;
        switch ((int)(h / 60) % 6)
        {
            case 0: r = c; g = x; break;
            case 1: r = x; g = c; break;
            case 2: g = c; b = x; break;
            case 3: g = x; b = c; break;
            case 4: r = x; b = c; break;
            default: r = c; b = x; break;
        }
        int R = (int)Math.Round((r + m) * 255);
        int G = (int)Math.Round((g + m) * 255);
        int B = (int)Math.Round((b + m) * 255);
        return "#" + R.ToString("X2", CultureInfo.InvariantCulture)
                   + G.ToString("X2", CultureInfo.InvariantCulture)
                   + B.ToString("X2", CultureInfo.InvariantCulture);
    }
}
