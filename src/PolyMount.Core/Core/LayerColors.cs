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
    // Defaults always have an exact Explorer overlay counterpart. All seven palette entries
    // are assigned to built-in layers, so no two of them can share a dot; a key that is not
    // listed here hashes into the same palette and can collide with one of these.
    private static readonly Dictionary<string, string> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["overlay"] = OriginPalette[0],
        ["cache"] = OriginPalette[1],
        ["svn"] = OriginPalette[2],
        ["dir"] = OriginPalette[3],
        ["prediction-cache"] = OriginPalette[4],
        // The co-read strategy is another predictor; Explorer can only draw the seven palette
        // colors, so it shares the prediction family's red rather than hashing elsewhere.
        ["prediction-burst"] = OriginPalette[4],
        ["memory-cache"] = OriginPalette[5],
        // Without this the layer hashed to slot 0 and showed the overlay's blue.
        ["warmup"] = OriginPalette[6]
    };

    /// <summary>Deterministic auto color for an arbitrary layer key, as "#RRGGBB".</summary>
    public static string Auto(string key)
    {
        if (Defaults.TryGetValue(key, out var preset)) return preset;

        return OriginPalette[(int)(Fnv1a(key.ToUpperInvariant()) % (uint)OriginPalette.Count)];
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

}
