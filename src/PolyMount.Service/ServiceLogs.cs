using Microsoft.Extensions.Hosting.WindowsServices;

namespace PolyMount.Service;

/// <summary>Where the host writes its own diagnostics.
///
/// An installed copy lives under Program Files and runs as LocalService, which cannot create a
/// file there. The sink does not fail loudly when it cannot: the host starts and serves and simply
/// writes no diagnostics, which is the worst version of this -- everything works until somebody
/// needs the log. They go to the common-application-data folder the installer has already given
/// the service account, beside the configuration and the stores.
///
/// A host started from a build keeps them beside its executable, which is where a developer
/// looks for them and where the account running it can write.
/// </summary>
internal static class ServiceLogs
{
    public static string Directory { get; } = Choose();

    /// <summary>The full path of one log, with the folder made first: a missing directory turns
    /// into a permission error at the first write, which is the wrong thing to be told.</summary>
    public static string PathOf(string fileName)
    {
        System.IO.Directory.CreateDirectory(Directory);
        return Path.Combine(Directory, fileName);
    }

    private static string Choose()
    {
        // The supervisor decides where its workers log and says so, because a worker is started by
        // the supervisor rather than by the service control manager: asking "am I a service?"
        // inside one answers no, and it would try to write beside an executable it cannot write
        // beside.
        if (Environment.GetEnvironmentVariable("POLYMOUNT_LOG_DIR") is { Length: > 0 } told) return told;
        if (!WindowsServiceHelpers.IsWindowsService()) return AppContext.BaseDirectory;
        string data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(data, "PolyMount", "logs");
    }
}
