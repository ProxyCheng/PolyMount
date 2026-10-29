using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PolyMount.Core;

/// <summary>What a host process is: the build it was made from, and the machine it is running on.
///
/// Both halves of this product answer with these — the tray in its About section, the service over
/// the control pipe — and neither may answer for the other: they are installed and updated
/// separately, so "which version is on this machine" has two answers and comparing them is the
/// point. Sharing the readers is what keeps the two answers worded the same.
///
/// The release is the informational version without its source revision; the revision after the
/// "+" is the commit, the one thing that tells two builds of the same number apart. Nothing here is
/// on a request path.</summary>
public static class HostEnvironment
{
    /// <summary>The build an assembly was stamped with: the release, its commit, and the whole
    /// informational string. A build asked for a version carries that; a development build carries
    /// the assembly default, which is why the revision is reported beside it rather than instead.</summary>
    public static (string Version, string Revision, string Informational) Build(Assembly assembly)
    {
        string full;
        try
        {
            full = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                is [AssemblyInformationalVersionAttribute informational, ..]
                ? informational.InformationalVersion
                : assembly.GetName().Version?.ToString() ?? "unknown";
        }
        catch { full = "unknown"; }

        string[] halves = full.Split('+', 2);
        string version = halves[0].Trim();
        return (version.Length > 0 ? version : "unknown", halves.Length > 1 ? halves[1].Trim() : "", full);
    }

    /// <summary>The .NET runtime this process is running on, not the one it was built against.</summary>
    public static string Runtime => Environment.Version.ToString();

    /// <summary>The operating system and the width of this process.</summary>
    public static string System => RuntimeInformation.OSDescription + " · " + (Environment.Is64BitProcess ? "x64" : "x86");

    /// <summary>The WinFsp build the mounts run against. Read from the install location rather than
    /// from configuration, which the service owns and a window only sees through status.</summary>
    public static string WinFsp
    {
        get
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinFsp", "bin", "winfsp-x64.dll");
                return FileVersionInfo.GetVersionInfo(path).FileVersion ?? "unknown";
            }
            catch { return "unknown"; }
        }
    }

    /// <summary>When this process started, as a round-trip timestamp, or "" when the process cannot
    /// read its own start time.</summary>
    public static string StartedUtc
    {
        get
        {
            try { return Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("O"); }
            catch (Exception) { return ""; }
        }
    }

    /// <summary>The account this process runs under. For an installed service that is the identity
    /// its network requests carry — LocalService on a default setup — and not the account of the
    /// person who installed it, which is why a credential can work in a window and be refused by a
    /// mount.</summary>
    public static string Account
    {
        get
        {
            try { return global::System.Security.Principal.WindowsIdentity.GetCurrent().Name; }
            catch (Exception) { return ""; }
        }
    }
}
