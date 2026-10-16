using Microsoft.Win32;

namespace PolyMount.Tray;

/// <summary>
/// Registers/unregisters an Explorer context-menu entry that launches this executable with
/// <c>--manage-cache "&lt;path&gt;"</c>.
///
/// Two hives carry the same key. The setup writes it machine-wide when the product is installed,
/// and this class writes it for the current user when it is not. Both have to be read, or an
/// installed machine answers "not installed" and the tray's own toggle adds a second copy of the
/// same submenu beside the first, which is what a reader sees as Explorer showing PolyMount twice.
/// </summary>
public static class ShellMenuInstaller
{
    private const string KeyName = "PolyMountLayers";

    private static readonly string[] Types = { "*", "Directory" };

    private static string ShellPath(string type) => $@"Software\Classes\{type}\shell\{KeyName}";

    public static bool IsInstalled() => IsMachineWide() || Exists(Registry.CurrentUser);

    /// <summary>Whether the setup registered it for every user of this machine. Such an entry is
    /// not the current user's to remove — HKLM needs administrator rights to write — so the tray
    /// offers it as a fact rather than as a switch.</summary>
    public static bool IsMachineWide() => Exists(Registry.LocalMachine);

    private static bool Exists(RegistryKey root)
    {
        using var k = root.OpenSubKey(ShellPath("*"));
        return k != null;
    }

    public static void Install(string exePath)
    {
        // A machine-wide entry already covers this user; a second, per-user one would show the
        // submenu twice.
        if (IsMachineWide()) return;
        foreach (var type in Types)
        {
            using var shell = Registry.CurrentUser.CreateSubKey(ShellPath(type));
            shell.SetValue("MUIVerb", "PolyMount");
            shell.SetValue("SubCommands", "");
            shell.SetValue("Icon", $"\"{exePath}\"");
            shell.DeleteSubKeyTree("command", throwOnMissingSubKey: false);
            using var action = shell.CreateSubKey(@"shell\ManageObject");
            action.SetValue(null, I18n.T("object.manageBoundary"));
            action.SetValue("Icon", $"\"{exePath}\"");
            action.SetValue("MultiSelectModel", "Document");
            using var cmd = action.CreateSubKey("command");
            cmd.SetValue(null, $"\"{exePath}\" --manage-cache \"%1\"");
        }
    }

    public static void Uninstall()
    {
        if (IsMachineWide()) return;
        foreach (var type in Types)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(ShellPath(type), throwOnMissingSubKey: false); }
            catch { /* best effort */ }
        }
    }
}
