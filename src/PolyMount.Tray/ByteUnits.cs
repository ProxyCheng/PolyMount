namespace PolyMount.Tray;

internal static class ByteUnits
{
    private static readonly string[] Units = { "B", "KiB", "MiB", "GiB", "TiB", "PiB" };
    public static (double Scale, string Unit) Select(double bytes)
    {
        double scale = 1;
        int index = 0;
        while (Math.Abs(bytes) / scale >= 1024 && index < Units.Length - 1) { scale *= 1024; index++; }
        return (scale, Units[index]);
    }
    public static string Format(double bytes)
    {
        var (scale, unit) = Select(bytes);
        return (bytes / scale).ToString("0.##", I18n.Culture) + " " + unit;
    }
}
