using System.Diagnostics;
using System.IO;
using PolyMount.Core.Ipc;

namespace PolyMount.Tray;

/// <summary>What became of a start or a stop: the line to show, and whether the request was ever
/// made. A refusal — nothing to stop, or a launch that failed — is not something a window can wait
/// on, and saying so is what keeps it from staying flat for the state that would never come.</summary>
internal readonly record struct ServiceOutcome(bool Ok, string Message);

/// <summary>Starts/stops the PolyMount service from the tray.</summary>
internal static class ServiceControl
{
    /// <summary>Is the service reachable over the control pipe?</summary>
    public static bool IsRunning()
    {
        try { return SupervisorClient.Default.Status().Ok; }
        catch { return false; }
    }

    public static ServiceOutcome Start()
    {
        if (IsRunning()) return new(true, I18n.T("service.already"));

        // Installed layout uses the SCM account and ProgramData configuration, never a
        // second interactive service process beside the packaged tray executable.
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "..", "install.json")))
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo("sc.exe", "start PolyMount")
                    { UseShellExecute = false, CreateNoWindow = true })!;
                process.WaitForExit(5000);
                return process.HasExited && process.ExitCode == 0
                    ? new(true, I18n.T("service.startRequested"))
                    : new(false, I18n.T("operation.failed") + "sc.exe start PolyMount");
            }
            catch (Exception ex) { return new(false, I18n.T("operation.failed") + ex.Message); }
        }

        var exe = ServiceExePath();
        if (exe != null && File.Exists(exe))
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(exe)!
                });
                return new(true, I18n.T("service.starting"));
            }
            catch (Exception ex) { return new(false, I18n.T("operation.failed") + ex.Message); }
        }

        // Fall back to an installed Windows service.
        try
        {
            Process.Start(new ProcessStartInfo("sc.exe", "start PolyMount") { UseShellExecute = false, CreateNoWindow = true });
            return new(true, I18n.T("service.startRequested"));
        }
        catch (Exception ex) { return new(false, I18n.T("operation.failed") + ex.Message); }
    }

    public static ServiceOutcome Stop()
    {
        if (!IsRunning()) return new(true, I18n.T("service.notRunning"));
        try
        {
            SupervisorClient.Default.Shutdown();
            return new(true, I18n.T("service.stopping"));
        }
        catch (Exception ex) { return new(false, I18n.T("operation.failed") + ex.Message); }
    }

    // In the dev layout the tray and service live in sibling bin dirs; derive one path from the
    // other by name. (For an installed service, Start() falls back to sc.exe.)
    private static string? ServiceExePath()
    {
        var tray = Environment.ProcessPath;
        if (string.IsNullOrEmpty(tray)) return null;
        var candidate = tray.Replace("PolyMount.Tray", "PolyMount.Service");
        return candidate == tray ? null : candidate;
    }
}
