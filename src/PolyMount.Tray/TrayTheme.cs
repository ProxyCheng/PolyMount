namespace PolyMount.Tray;

internal static class TrayTheme
{
    public static string StyleSheet { get; } = Load();
    private static string Load()
    {
        return QtResources.ReadText(":/polymount/theme.qss");
    }
}
