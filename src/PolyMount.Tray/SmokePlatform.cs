using System.Runtime.InteropServices;

namespace PolyMount.Tray;

/// <summary>
/// Where a self-check's windows are drawn: nowhere. The checks build real windows, show them and
/// measure them — that is the point — but a run that maps a window on somebody's desktop is a run
/// nobody wants to start while they are working, and CI has no desktop at all.
///
/// Qt picks its QPA plugin when the QApplication is constructed, and it reads the environment
/// through the C runtime: Environment.SetEnvironmentVariable alone leaves Qt looking at the old
/// table. _wputenv_s writes both. This is the same recipe QtSharp's own test host uses, including
/// the font directory — the offscreen plugin has no font database of its own on Windows, and
/// without one QFontMetrics returns synthetic rectangles and every layout check measures nothing.
///
/// An explicit QT_QPA_PLATFORM is honoured, so <c>set QT_QPA_PLATFORM=windows</c> runs the checks
/// on a real desktop when a reader wants to watch them.
/// </summary>
internal static class SmokePlatform
{
    public static void Choose(IReadOnlyList<string> args)
    {
        bool smoke = args.Any(argument => string.Equals(argument, "--ui-smoke", StringComparison.OrdinalIgnoreCase));
        if (!smoke) return;
        string? chosen = Environment.GetEnvironmentVariable("QT_QPA_PLATFORM");
        bool offscreen = string.IsNullOrEmpty(chosen) || string.Equals(chosen, "offscreen", StringComparison.OrdinalIgnoreCase);
        if (offscreen && !OffscreenPluginPresent())
        {
            // Qt aborts the process when the platform it was told to use is not in the build, and
            // an abort is a worse answer than a window: the checks run on the desktop instead and
            // say why. The Windows payload of the binding ships platforms\qwindows.dll only.
            Console.WriteLine("[platform] windows: this build has no offscreen plugin, so the self-check draws windows");
            return;
        }
        string platform = offscreen ? "offscreen" : chosen!;
        Set("QT_QPA_PLATFORM", platform);
        if (offscreen && OperatingSystem.IsWindows() &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("QT_QPA_FONTDIR")))
            Set("QT_QPA_FONTDIR", Environment.GetFolderPath(Environment.SpecialFolder.Fonts));
        Console.WriteLine(platform == "offscreen"
            ? "[platform] offscreen: the self-check draws no window; set QT_QPA_PLATFORM=windows to watch it"
            : "[platform] " + platform + " (from QT_QPA_PLATFORM)");
    }

    /// <summary>Whether this build can actually run offscreen: the QPA plugin is a file next to the
    /// executable, and its absence is what turns "don't draw a window" into an abort.</summary>
    private static bool OffscreenPluginPresent() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "platforms", "qoffscreen.dll"));

    /// <summary>Writes the variable where Qt reads it. Qt's plugin choice and its font lookup both
    /// go through the C runtime, whose table is a different one from .NET's; the managed copy is
    /// written as well so that anything in this process reading it back sees the same answer.</summary>
    private static void Set(string name, string value)
    {
        if (OperatingSystem.IsWindows())
        {
            _wputenv_s(name, value);
            Environment.SetEnvironmentVariable(name, value);
            return;
        }
        Environment.SetEnvironmentVariable(name, value);
    }

    [DllImport("ucrtbase", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int _wputenv_s(string name, string value);
}
