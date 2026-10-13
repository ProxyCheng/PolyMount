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

        // A bounded, read-only UI smoke run for checking native widgets and both languages.
        if (Array.IndexOf(args, "--ui-smoke") >= 0)
            return RunUiSmoke(args);

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
        _tray.SetIcon(TrayIcons.App);
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
            _tray.ShowMessage("PolyMount", I18n.T("tray.message"),
                QSystemTrayIcon.MessageIcon.Information, 4000);

        ShowDashboard();
        return _app.Exec();
    }

    private static QMenu BuildMenu()
    {
        var menu = new QMenu();

        var open = menu.AddAction("");
        I18n.Bind(open.SetText, "tray.open");
        open.SetIcon(TrayIcons.Get("drive"));
        open.Triggered += _ => ShowDashboard();

        var startSvc = menu.AddAction("");
        I18n.Bind(startSvc.SetText, "service.start");
        startSvc.SetIcon(TrayIcons.Get("play"));
        startSvc.Triggered += _ => ServiceControl.Start();

        var stopSvc = menu.AddAction("");
        I18n.Bind(stopSvc.SetText, "service.stop");
        stopSvc.SetIcon(TrayIcons.Get("stop"));
        stopSvc.Triggered += _ => ServiceControl.Stop();

        var shell = menu.AddAction("");
        I18n.Bind(shell.SetText, "tray.shell");
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

        var exit = menu.AddAction("");
        I18n.Bind(exit.SetText, "tray.exit");
        exit.Triggered += _ => _app.Quit();

        return menu;
    }

    private static void ShowDashboard()
    {
        _dashboard ??= new Dashboard();
        _dashboard.ShowWindow();
    }

    private static int RunUiSmoke(string[] args)
    {
        var dashboard = new Dashboard();
        var editor = new ConfigEditor("L10");
        var stats = new StatsWindow("L10");
        dashboard.ShowWindow();
        editor.ShowWindow();
        stats.ShowWindow();
        var timer = new QTimer();
        int ticks = 0;
        string original = I18n.Language;
        int captureIndex = Array.IndexOf(args, "--capture");
        string? capture = captureIndex >= 0 && captureIndex + 1 < args.Length ? args[captureIndex + 1] : null;
        string? failure = null;
        ApiRuntime.UnhandledCallbackException += (context, ex) => failure = context + ": " + ex;
        timer.Timeout += () =>
        {
            ticks++;
            if (ticks == 18 && capture != null)
            {
                dashboard.Capture(Path.Combine(capture, "dashboard.png"));
                editor.Capture(Path.Combine(capture, "layers.png"));
                stats.Capture(Path.Combine(capture, "stats.png"));
            }
            if (ticks == 20)
            {
                editor.SmokeInteractions();
                dashboard.SmokeSilentUpdate();
                I18n.SetLanguage(original == "en" ? "zh-CN" : "en");
            }
            if (ticks == 36 && capture != null)
            {
                dashboard.Capture(Path.Combine(capture, "dashboard-alternate.png"));
                editor.Capture(Path.Combine(capture, "layers-alternate.png"));
            }
            if (ticks == 40) I18n.SetLanguage(original);
            if (ticks >= 60)
            {
                timer.Stop();
                Console.WriteLine(failure ?? "UI smoke passed: Dashboard, ConfigEditor, Stats and live localization.");
                _app.Exit(failure == null ? 0 : 1);
            }
        };
        timer.Start(100);
        int code = _app.Exec();
        GC.KeepAlive(dashboard);
        GC.KeepAlive(editor);
        GC.KeepAlive(stats);
        GC.KeepAlive(timer);
        return code;
    }
}
