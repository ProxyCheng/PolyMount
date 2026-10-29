using PolyMount.Core;
using PolyMount.Core.Ipc;

namespace PolyMount.Service;

/// <summary>Which build this service is, and where it lives — the answer to "build" on the control
/// pipe, and the service's half of what the tray's About section says about itself. The two halves
/// are installed and updated separately, so this is asked rather than assumed equal.
///
/// Everything here is read from the process that answers: the program folder is the one this
/// assembly was loaded from, the configuration is the file the supervisor owns, and a host started
/// by hand says so. Nothing is guessed from the machine's environment beyond WinFsp, which is a
/// fact about the machine by definition.</summary>
internal static class BuildFacts
{
    public static BuildInfo Describe(bool dev, string configPath)
    {
        string baseDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        string parent = Path.GetFullPath(Path.Combine(baseDirectory, ".."));
        bool installed = File.Exists(Path.Combine(parent, "install.json"));
        var (version, revision, informational) = HostEnvironment.Build(typeof(BuildFacts).Assembly);

        return new BuildInfo
        {
            Version = version,
            Revision = revision,
            Informational = informational,
            Dev = dev,
            Installed = installed,
            Root = installed ? parent : baseDirectory,
            ConfigPath = configPath,
            PluginsDirectory = Path.Combine(baseDirectory, "plugins"),
            Runtime = HostEnvironment.Runtime,
            WinFsp = HostEnvironment.WinFsp,
            System = HostEnvironment.System,
            ProcessId = Environment.ProcessId,
            Account = HostEnvironment.Account,
            StartedUtc = HostEnvironment.StartedUtc
        };
    }
}
