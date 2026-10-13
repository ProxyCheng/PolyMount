using Microsoft.Win32;

namespace PolyMount.Tray;

/// <summary>
/// Registers/unregisters a per-user Explorer context-menu entry that launches this executable
/// with <c>--probe "&lt;path&gt;"</c>. HKCU only, so no administrator rights are required.
/// </summary>
public static class ShellMenuInstaller
{
    private const string KeyName = "PolyMountLayers";

    private static readonly string[] Types = { "*", "Directory" };

    private static string ShellPath(string type) => $@"Software\Classes\{type}\shell\{KeyName}";

    public static bool IsInstalled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(ShellPath("*"));
        return k != null;
    }

    public static void Install(string exePath)
    {
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
        foreach (var type in Types)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(ShellPath(type), throwOnMissingSubKey: false); }
            catch { /* best effort */ }
        }
    }
}
