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
        // Qt argv includes the executable name; Main(args) omits it.
        _app = new QApplication(new[] { Environment.ProcessPath ?? "PolyMount.Tray" }.Concat(args).ToArray());
        I18n.Initialize();
        using var options = new QCommandLineParser();
        options.SetApplicationDescription("PolyMount tray and object layer management.");
        using var help = options.AddHelpOption();
        using var install = new QCommandLineOption("install-shell-menu", "Register the Explorer context menu for the current user.");
        using var smoke = new QCommandLineOption("ui-smoke", "Run bounded UI checks without saving mount configuration.");
        using var captureOption = new QCommandLineOption("capture", "Save UI smoke screenshots to an existing directory.", "directory");
        using var manage = new QCommandLineOption(new[] { "manage-cache", "probe" }, "Open object cache boundary management (repeatable).", "path");
        options.AddOption(install);
        options.AddOption(smoke);
        options.AddOption(captureOption);
        options.AddOption(manage);
        options.AddPositionalArgument("paths", "Additional objects to manage with --manage-cache.", "[paths...]");
        if (!options.Parse(QCoreApplication.Arguments()))
        {
            Console.Error.WriteLine(options.ErrorText());
            return 2;
        }
        if (options.IsSet(help)) { Console.WriteLine(options.HelpText()); return 0; }
        if (options.IsSet(captureOption) && !options.IsSet(smoke) ||
            options.PositionalArguments().Length > 0 && !options.IsSet(manage) ||
            new[] { options.IsSet(install), options.IsSet(smoke), options.IsSet(manage) }.Count(set => set) > 1)
        {
            Console.Error.WriteLine("Choose one mode; --capture requires --ui-smoke and object paths require --manage-cache.");
            return 2;
        }

        if (options.IsSet(install))
        {
            ShellMenuInstaller.Install(Environment.ProcessPath!);
            Console.WriteLine("Explorer file/folder cache boundary menu registered.");
            return 0;
        }

        // A bounded, read-only UI smoke run for checking native widgets and both languages.
        if (options.IsSet(smoke))
            return RunUiSmoke(options.IsSet(captureOption) ? options.Value(captureOption) : null);

        // Launched from the Explorer context menu: show just the layer view, no tray.
        if (options.IsSet(manage))
        {
            foreach (string path in options.Values(manage).Concat(options.PositionalArguments()).Distinct(StringComparer.OrdinalIgnoreCase)) LayerVisualizer.Open(path);
            return _app.Exec();
        }

        string settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PolyMount");
        Directory.CreateDirectory(settingsDirectory);
        using var instanceLock = new QLockFile(Path.Combine(settingsDirectory, "tray.lock"));
        instanceLock.SetStaleLockTime(0);
        if (!instanceLock.TryLock(0))
        {
            if (instanceLock.Error() == QLockFile.LockError.LockFailedError &&
                TrayActivation.ActivateExisting(TrayActivation.ServerName(settingsDirectory))) return 0;
            Console.Error.WriteLine("Unable to activate the existing tray or acquire its instance lock: " + instanceLock.Error());
            return 2;
        }
        using var activation = new TrayActivation(TrayActivation.ServerName(settingsDirectory), ShowDashboard);

        // Closing the dashboard must not quit the app; the tray keeps it alive.
        QApplication.SetQuitOnLastWindowClosed(false);
        if (ShellMenuInstaller.IsInstalled()) ShellMenuInstaller.Install(Environment.ProcessPath!);

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
        if (_dashboard.IsMinimized()) _dashboard.ShowNormal();
        _dashboard.ShowWindow();
    }

    /// <summary>The first instance the service is running, so a smoke run charts live data
    /// rather than an empty window.</summary>
    private static string? LiveInstance()
    {
        try { return ControlClient.Status().Mounts.FirstOrDefault(mount => mount.Enabled && mount.Mounted)?.Name; }
        catch { return null; }
    }

    private static int RunUiSmoke(string? capture)
    {
        TrayActivation.Smoke();
        var dashboard = new Dashboard();
        var editor = dashboard.OpenEditor("L10")!;
        string instance = LiveInstance() ?? "L10";
        var stats = dashboard.OpenPerformance(instance);
        LayerVisualizer.Open("D:\\L10\\20260924");
        dashboard.ShowWindow();
        var timer = new QTimer();
        int ticks = 0;
        string original = I18n.Language;
        string? failure = null;
        ApiRuntime.UnhandledCallbackException += (context, ex) => failure = context + ": " + ex;
        timer.Timeout += () =>
        {
            ticks++;
            if (ticks == 2)
            {
                try { dashboard.SmokeTabs(instance); } catch (Exception ex) { failure = ex.ToString(); }
            }
            if (ticks == 18 && capture != null)
            {
                dashboard.Capture(Path.Combine(capture, "dashboard.png"));
                editor.Capture(Path.Combine(capture, "layers.png"));
                stats.Capture(Path.Combine(capture, "stats.png"));
            }
            if (ticks == 20)
            {
                try { LayerVisualizer.SmokeBoundary(); } catch (Exception ex) { failure = ex.ToString(); }
                try { editor.SmokeLayerPicker(capture == null ? null : Path.Combine(capture, "layer-picker.png")); }
                catch (Exception ex) { failure = ex.ToString(); }
                editor.SmokeInteractions();
                dashboard.SmokeSilentUpdate();
                I18n.SetLanguage(original == "en" ? "zh-CN" : "en");
            }
            if (ticks == 36 && capture != null)
            {
                try { editor.SmokeLayerPicker(Path.Combine(capture, "layer-picker-alternate.png")); }
                catch (Exception ex) { failure = ex.ToString(); }
                dashboard.Capture(Path.Combine(capture, "dashboard-alternate.png"));
                editor.Capture(Path.Combine(capture, "layers-alternate.png"));
            }
            if (ticks == 40)
            {
                if (I18n.T("dashboard.title") == "dashboard.title") failure = "Qt resource translation lookup failed";
                I18n.SetLanguage(original);
            }
            if (ticks == 45)
            {
                dashboard.Resize(560, 420);
            }
            if (ticks == 50)
            {
                if (dashboard.Width > 560)
                    failure = "Workspace windows could not shrink to the requested size";
                if (capture != null)
                {
                    dashboard.Capture(Path.Combine(capture, "dashboard-small.png"));
                    editor.Capture(Path.Combine(capture, "layers-small.png"));
                    stats.Capture(Path.Combine(capture, "stats-small.png"));
                }
            }
            if (ticks >= 60)
            {
                timer.Stop();
                try
                {
                    stats.VerifyResponseSampling();
                    stats.VerifyChartScale();
                    stats.VerifyExport(Path.Combine(capture ?? Path.GetTempPath(), "polymount-export-smoke"));
                }
                catch (Exception ex) { failure = ex.Message; }
                Console.WriteLine(failure ?? "UI smoke passed: tab reorder, four-way splits, detach/return, preserved drafts, Dashboard, ConfigEditor, Stats and live localization.");
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
