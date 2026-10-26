using PolyMount.Core;
using Serilog;

namespace PolyMount.Tray;

/// <summary>The tray's own log, in the reader's application data beside the settings it keeps.
///
/// The tray used to have none: the command-line modes write to the console, which is what a console
/// mode is for, but the window ran without leaving a trace of anything — an operation that failed,
/// a page that lost the service, or an exception on the way out were all shown once in a status
/// line and then gone. What the service has had all along is an asynchronous file sink
/// (<see cref="AsyncFileLog"/>, the same call, the same template, the same rolling), and the tray
/// takes it as it is: writing to a log must never be something the interface waits for, and the
/// sink is configured not to block the caller even if it cannot keep up.
///
/// It goes under the application data rather than beside the executable, where the service keeps
/// its own: an installed tray lives in Program Files, and nothing it writes may need an
/// administrator.
/// </summary>
internal static class TrayLog
{
    internal const string FileName = "polymount-tray.log";

    /// <summary>Where a reader finds it: the same folder the tray already keeps ui.json and
    /// stats.json in.</summary>
    internal static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PolyMount", "logs", FileName);

    /// <summary>Where a command-line mode writes instead: its own file, because a file sink opens
    /// exclusively, and a mode started while the window is running would otherwise be turned away
    /// from the log — or, worse, be the one that gets to write it.</summary>
    internal static string CliPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PolyMount", "logs", "polymount-tray-cli.log");

    /// <summary>Turns it on. Called before anything can fail, and before any mode runs: a
    /// command-line mode writes its result to the console and its diagnostics to its own file.</summary>
    internal static void Start(bool window = true)
    {
        string path = window ? Path : CliPath;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            Log.Logger = AsyncFileLog.Create(path);
        }
        catch (Exception)
        {
            // A log that cannot be opened is not a reason to refuse to run, and it is usually not a
            // fault at all: a command-line mode started while the window is running cannot open a
            // file the window holds. Nothing is said about it on purpose — the console of a mode is
            // where its result goes, and a diagnostic that cannot be written down is not one.
            return;
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception, "the tray is going down");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "a background task failed with nobody awaiting it");
            e.SetObserved();
        };
        // Qt catches what a signal handler or a virtual override throws and raises this, because
        // an exception has nowhere to land in its C++ frames. The runtime's own report goes to
        // stderr, which a windowed process does not show: an unsubscribed handler that throws
        // makes the button that raised it do nothing at all. The log is where it can be read.
        QtSharp.Runtime.ApiRuntime.UnhandledCallbackException += (what, exception) =>
            Log.Error(exception, "unhandled exception in {Where}", what);
    }
}
