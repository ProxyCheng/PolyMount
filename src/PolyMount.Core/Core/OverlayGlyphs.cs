namespace PolyMount.Core;

/// <summary>
/// The Explorer badge's glyph vocabulary, in slot order: one entry per palette colour, drawn by
/// the shell extension and picked by the icon a layer descriptor names. It is the tray's icon
/// set, so a layer looks the same in the window and on its files.
/// </summary>
public static class OverlayGlyphs
{
    public static IReadOnlyList<string> Names { get; } = Load();
    private static IReadOnlyList<string> Load()
    {
        using var stream = typeof(OverlayGlyphs).Assembly.GetManifestResourceStream("PolyMount.OverlayGlyphs")!;
        using var reader = new StreamReader(stream);
        return Array.AsReadOnly(System.Text.RegularExpressions.Regex.Matches(reader.ReadToEnd(), "PM_GLYPH\\(\"([a-z0-9-]+)\"\\)")
            .Select(m => m.Groups[1].Value).ToArray());
    }

    /// <summary>What a layer with no glyph of its own wears: the last entry is the generic mark.</summary>
    public static string Generic => Names[^1];

    /// <summary>
    /// The slot a layer type's icon maps to, or the generic slot when the descriptor names none or
    /// names one the overlay does not draw. A descriptor that names a tray icon the shell has no
    /// badge for is not an error: the badge is a summary, and "some layer" is still true.
    /// </summary>
    public static int SlotOf(string? icon)
    {
        for (int i = 0; i < Names.Count; i++)
            if (string.Equals(Names[i], icon, StringComparison.OrdinalIgnoreCase)) return i;
        return Names.Count - 1;
    }
}
