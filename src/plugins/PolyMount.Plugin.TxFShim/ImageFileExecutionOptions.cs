using Microsoft.Win32;

namespace PolyMount.Plugins.TxFShim;

/// <summary>
/// The one registry surface this plugin touches: the <c>Debugger</c> value under an image's
/// Image File Execution Options key, which is how Windows is told to start something else in place
/// of a named image.
///
/// It is an interface because the interesting behaviour — refusing to take a key that points
/// somewhere else, reporting drift, removing only what we wrote — has to be testable without an
/// administrator and without a registry.
/// </summary>
internal interface IImageFileExecutionOptions
{
    /// <summary>The <c>Debugger</c> value for this image as it is now, or null when the image has
    /// no key (or the key names nothing).</summary>
    string? Launcher(string imageName);

    void SetLauncher(string imageName, string launcher);

    void RemoveLauncher(string imageName);

    /// <summary>Every image that has a <c>Debugger</c> value at all, whether or not it is ours.</summary>
    IEnumerable<string> Instrumented();
}

internal sealed class RegistryImageFileExecutionOptions : IImageFileExecutionOptions
{
    private const string BaseKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    public string? Launcher(string imageName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{BaseKey}\{imageName}");
        return key?.GetValue("Debugger") as string is { Length: > 0 } value ? value : null;
    }

    public void SetLauncher(string imageName, string launcher)
    {
        using var key = Registry.LocalMachine.CreateSubKey($@"{BaseKey}\{imageName}", writable: true)
            ?? throw new UnauthorizedAccessException($"cannot create the Image File Execution Options key for '{imageName}'");
        key.SetValue("Debugger", launcher, RegistryValueKind.String);
    }

    public void RemoveLauncher(string imageName)
    {
        // The value is removed, not the key: the key may hold values that predate us, and deleting
        // it would take those with it.
        using var key = Registry.LocalMachine.OpenSubKey($@"{BaseKey}\{imageName}", writable: true);
        key?.DeleteValue("Debugger", throwOnMissingValue: false);
    }

    public IEnumerable<string> Instrumented()
    {
        using var root = Registry.LocalMachine.OpenSubKey(BaseKey);
        if (root == null) yield break;
        foreach (string name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);
            if (key?.GetValue("Debugger") is string { Length: > 0 }) yield return name;
        }
    }
}
