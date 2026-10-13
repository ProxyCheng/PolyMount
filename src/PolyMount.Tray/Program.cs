using PolyMount.Core.Ipc;
using QtSharp.Api;

namespace PolyMount.Tray;

internal static class Program
{
    private static QApplication _app = null!;
    private static Dashboard? _dashboard;
    private static QSystemTrayIcon? _tray;
    private static QMenu? _menu;

    [STAThread]
    private static int Main(string[] args)
    {
        _app = new QApplication(args);

        // Launched from the Explorer context menu: show just the layer view, no tray.
        int probeIdx = Array.IndexOf(args, "--probe");
        if (probeIdx >= 0 && probeIdx + 1 < args.Length)
        {
            var view = LayerVisualizer.Open(args[probeIdx + 1]);
            return view ? _app.Exec() : 1;
        }

        // Closing the dashboard must not quit the app; the tray keeps it alive.
        QApplication.SetQuitOnLastWindowClosed(false);

        _tray = new QSystemTrayIcon();
        _tray.SetIcon(QApplication.Style().StandardIcon(QStyle.StandardPixmap.SP_DriveHDIcon));
        _tray.SetToolTip("PolyMount");
        _menu = BuildMenu();            // keep a reference so it is not garbage-collected
        _tray.SetContextMenu(_menu);
        _tray.Activated += reason =>
        {
            if (reason is QSystemTrayIcon.ActivationReason.Trigger or QSystemTrayIcon.ActivationReason.DoubleClick)
                ShowDashboard();
        };
        _tray.SetVisible(true);

        // Surface the icon: Windows parks new tray icons in the overflow area, so pop a balloon
        // and open the dashboard once so the user can find it.
        if (QSystemTrayIcon.IsSystemTrayAvailable())
            _tray.ShowMessage("PolyMount", "Running in the system tray. Double-click the icon or use the menu.",
                QSystemTrayIcon.MessageIcon.Information, 4000);

        ShowDashboard();
        return _app.Exec();
    }

    private static QMenu BuildMenu()
    {
        var menu = new QMenu();

        var open = menu.AddAction("Open Dashboard");
        open.Triggered += _ => ShowDashboard();

        var startSvc = menu.AddAction("Start Service");
        startSvc.Triggered += _ => ServiceControl.Start();

        var stopSvc = menu.AddAction("Stop Service");
        stopSvc.Triggered += _ => ServiceControl.Stop();

        var shell = menu.AddAction("Explorer context menu");
        shell.SetCheckable(true);
        shell.SetChecked(ShellMenuInstaller.IsInstalled());
        shell.Triggered += _ =>
        {
            try
            {
                if (ShellMenuInstaller.IsInstalled()) ShellMenuInstaller.Uninstall();
                else ShellMenuInstaller.Install(Environment.ProcessPath ?? "");
            }
            catch { /* ignore */ }
            shell.SetChecked(ShellMenuInstaller.IsInstalled());
        };

        menu.AddSeparator();

        var exit = menu.AddAction("Exit");
        exit.Triggered += _ => _app.Quit();

        return menu;
    }

    private static void ShowDashboard()
    {
        _dashboard ??= new Dashboard();
        _dashboard.ShowWindow();
    }
}
