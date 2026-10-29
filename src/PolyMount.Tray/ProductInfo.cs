using System.Diagnostics;
using System.IO;
using System.Text.Json;
using PolyMount.Core;

namespace PolyMount.Tray;

/// <summary>What this copy is: the release it was built as and the commit behind it, what it runs
/// on, and where the program, its configuration and the service beside it live. The about page and
/// the diagnostic text it copies both read from here, so a reader comparing the two never finds
/// them disagreeing about which build is in front of them.
///
/// The release number is the informational version without its source revision: a build asked for
/// a version carries that, a development build carries the assembly default, and in both cases the
/// text after the "+" is the commit — the one thing that tells two builds of the same number apart.
/// Nothing here is asked on a request path; the version is read once from the assembly's own
/// attributes, and the rest when somebody opens the page.</summary>
internal static class ProductInfo
{
    /// <summary>The release, e.g. "0.9.4".</summary>
    public static string Version { get; }

    /// <summary>The commit that build came from, shortened, or "" when the build carries none.</summary>
    public static string Revision { get; }

    /// <summary>The whole informational version, revision included.</summary>
    public static string Full { get; }

    /// <summary>Whether this program was put here by the setup, which leaves install.json beside
    /// the program folders; a development tree runs from its build output instead.</summary>
    public static bool Installed { get; }

    /// <summary>The folder the program lives in: the installation directory, or the build output a
    /// development copy runs from.</summary>
    public static string Root { get; }

    static ProductInfo()
    {
        var (version, revision, full) = HostEnvironment.Build(typeof(ProductInfo).Assembly);
        Version = version;
        Revision = revision;
        Full = full;

        string baseDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        string parent = Path.GetFullPath(Path.Combine(baseDirectory, ".."));
        Installed = File.Exists(Path.Combine(parent, "install.json"));
        Root = Installed ? parent : baseDirectory;
    }

    /// <summary>The short revision, for a line that has no room for forty characters.</summary>
    public static string ShortRevision => Revision.Length > 7 ? Revision[..7] : Revision;

    /// <summary>The .NET runtime this program is running on, not the one it was built against.</summary>
    public static string Dotnet => HostEnvironment.Runtime;

    /// <summary>The Qt runtime beside the program. Read from the DLL rather than from the binding:
    /// the file is what a recipient of the LGPL build may replace, and its version is the one that
    /// matters when it happens.</summary>
    public static string Qt
    {
        get
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(Path.Combine(AppContext.BaseDirectory, "Qt6Core.dll"));
                string version = info.ProductVersion is { Length: > 0 } product ? product : info.FileVersion ?? "";
                string[] parts = version.Split('.');
                if (parts.Length == 4 && parts[3] == "0") version = string.Join('.', parts[..3]);
                return version.Length > 0 ? version : "unknown";
            }
            catch { return "unknown"; }
        }
    }

    /// <summary>The WinFsp build the mount runs against, shared with the service's answer so the
    /// two cannot disagree.</summary>
    public static string WinFsp => HostEnvironment.WinFsp;

    /// <summary>The operating system and the width of this process.</summary>
    public static string System => HostEnvironment.System;

    /// <summary>Where the service's program is: the installation's own folder, or the sibling build
    /// a development tree runs. Empty when neither is there.</summary>
    public static string ServiceExe
    {
        get
        {
            if (Installed)
            {
                string installed = Path.Combine(Root, "Service", "PolyMount.Service.exe");
                if (File.Exists(installed)) return installed;
            }
            return ServiceControl.ExePath() ?? "";
        }
    }

    /// <summary>The version of that program — not of the process that may be running, which cannot
    /// be swapped while it is, so the file is what is running. Empty when there is no such file.</summary>
    public static string ServiceVersion
    {
        get
        {
            string exe = ServiceExe;
            if (exe.Length == 0 || !File.Exists(exe)) return "";
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                string version = info.ProductVersion is { Length: > 0 } product ? product : info.FileVersion ?? "";
                int plus = version.IndexOf('+');
                return plus >= 0 ? version[..plus] : version;
            }
            catch { return ""; }
        }
    }

    /// <summary>The configuration the service reads. The setup records where it put it, because the
    /// program lives under Program Files while the file is shared and writable in ProgramData; a
    /// development tree keeps it beside the service that reads it.</summary>
    public static string ConfigPath
    {
        get
        {
            if (InstallField("ConfigPath") is { Length: > 0 } configured)
                return Environment.ExpandEnvironmentVariables(configured);
            string exe = ServiceExe;
            if (exe.Length == 0) return "";
            string beside = Path.Combine(Path.GetDirectoryName(exe) ?? "", "config.json");
            return File.Exists(beside) ? beside : "";
        }
    }

    /// <summary>The third-party notices the setup installs beside the program, or — in a
    /// development tree — the copy the installer is built from. Empty when neither is there.</summary>
    public static string Notices
    {
        get
        {
            string installed = Path.Combine(Root, "THIRD-PARTY-NOTICES.txt");
            if (File.Exists(installed)) return installed;
            for (var directory = new DirectoryInfo(Root); directory != null; directory = directory.Parent)
                if (Path.Combine(directory.FullName, "installer", "THIRD-PARTY-NOTICES.txt") is { } candidate && File.Exists(candidate))
                    return candidate;
            return "";
        }
    }

    /// <summary>One field of the installation's own record. Missing, unreadable and malformed all
    /// read as "not said": the page says what it knows and leaves the rest out.</summary>
    private static string? InstallField(string name)
    {
        try
        {
            string path = Path.Combine(Root, "install.json");
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;
        }
        catch (Exception) { return null; }
    }
}
