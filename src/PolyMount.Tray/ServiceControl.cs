using System.Diagnostics;
using System.IO;
using PolyMount.Core.Ipc;

namespace PolyMount.Tray;

/// <summary>Starts/stops the PolyMount service from the tray.</summary>
internal static class ServiceControl
{
    /// <summary>Is the service reachable over the control pipe?</summary>
    public static bool IsRunning()
    {
        try { return ControlClient.Status().Ok; }
        catch { return false; }
    }

    public static string Start()
    {
        if (IsRunning()) return "service already running";

        var exe = ServiceExePath();
        if (exe != null && File.Exists(exe))
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
                return "service starting...";
            }
            catch (Exception ex) { return "start failed: " + ex.Message; }
        }

        // Fall back to an installed Windows service.
        try
        {
            Process.Start(new ProcessStartInfo("sc.exe", "start PolyMount") { UseShellExecute = false, CreateNoWindow = true });
            return "service start requested (sc.exe)";
        }
        catch (Exception ex) { return "start failed (no exe, no service): " + ex.Message; }
    }

    public static string Stop()
    {
        if (!IsRunning()) return "service not running";
        try
        {
            ControlClient.Send(new ControlRequest { Cmd = ControlCommands.Shutdown });
            return "service stopping...";
        }
        catch (Exception ex) { return "stop failed: " + ex.Message; }
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
