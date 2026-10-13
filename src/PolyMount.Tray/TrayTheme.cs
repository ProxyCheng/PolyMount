namespace PolyMount.Tray;

internal static class TrayTheme
{
    public static string StyleSheet { get; } = Load();
    private static string Load()
    {
        using var stream = typeof(TrayTheme).Assembly.GetManifestResourceStream("PolyMount.theme.qss")
            ?? throw new InvalidOperationException("Theme resource is missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
