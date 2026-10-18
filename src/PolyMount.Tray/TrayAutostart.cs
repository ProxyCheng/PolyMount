using Microsoft.Win32;

namespace PolyMount.Tray;

/// <summary>Whether the tray starts when the reader signs in.
///
/// Two registrations can carry this: the setup writes one machine-wide when the task is chosen,
/// and the tray writes one for the current user when it is toggled here. Both are read, for the
/// same reason the Explorer menu reads both hives — a per-user copy beside a machine-wide one is
/// a second entry that starts the same program twice.
/// </summary>
internal static class TrayAutostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PolyMountTray";

    /// <summary>Set by the setup for every user of the machine, which this tray cannot remove: that
    /// is an HKLM write. The menu reports it instead of offering a switch that could not move.</summary>
    public static bool IsMachineWide => Registry.LocalMachine.OpenSubKey(RunKey)?.GetValue(ValueName) != null;

    public static bool IsEnabled => IsMachineWide || Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(ValueName) != null;

    public static void Enable()
    {
        // A machine-wide entry already covers this user; a second, per-user one would start a
        // second tray, which the instance lock turns into an activation, but it is still a
        // duplicate registration nobody asked for.
        if (IsMachineWide) return;
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key?.SetValue(ValueName, $"\"{exe}\"");
    }

    public static void Disable()
    {
        if (IsMachineWide) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
