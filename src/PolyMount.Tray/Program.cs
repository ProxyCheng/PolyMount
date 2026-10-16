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
        using var record = new QCommandLineOption("record", "Record an instance for --seconds and write the bundle, without a page.", "mount");
        using var secondsOption = new QCommandLineOption("seconds", "How long --record collects for.", "seconds", "20");
        using var outputOption = new QCommandLineOption("output", "Where --record writes the bundle.", "directory");
        options.AddOption(install);
        options.AddOption(smoke);
        options.AddOption(captureOption);
        options.AddOption(manage);
        options.AddOption(record);
        options.AddOption(secondsOption);
        options.AddOption(outputOption);
        options.AddPositionalArgument("paths", "Additional objects to manage with --manage-cache.", "[paths...]");
        if (!options.Parse(QCoreApplication.Arguments()))
        {
            Console.Error.WriteLine(options.ErrorText());
            return 2;
        }
        if (options.IsSet(help)) { Console.WriteLine(options.HelpText()); return 0; }
        if (options.IsSet(captureOption) && !options.IsSet(smoke) ||
            (options.IsSet(secondsOption) || options.IsSet(outputOption)) && !options.IsSet(record) ||
            options.PositionalArguments().Length > 0 && !options.IsSet(manage) ||
            new[] { options.IsSet(install), options.IsSet(smoke), options.IsSet(manage), options.IsSet(record) }.Count(set => set) > 1)
        {
            Console.Error.WriteLine("Choose one mode; --capture requires --ui-smoke, --seconds/--output require --record, "
                + "and object paths require --manage-cache.");
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

        // A recording driven from the command line: the page is how this is meant to be used, but
        // a bundle has to be producible where nobody can click, both for checking the feature and
        // for collecting evidence on a machine someone else is using.
        if (options.IsSet(record))
            return RunHeadlessRecording(options.Value(record),
                int.TryParse(options.Value(secondsOption), out int seconds) ? seconds : 20,
                options.IsSet(outputOption) ? options.Value(outputOption) : null);

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
        // A recording is a session between the service and a page. Quitting has to take the
        // service's side down with it, or the channel stays attached and keeps writing a session
        // no page will ever collect.
        _app.AboutToQuit += RecordingSessions.SuspendAll;
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
        try { return SupervisorClient.Default.Status().Mounts.FirstOrDefault(mount => mount.Enabled && mount.Mounted)?.Name; }
        catch { return null; }
    }

    /// <summary>Records one instance for a fixed time and writes the bundle, with no page and no
    /// event loop. It drives the same session and the same writer the page does, so what it
    /// produces is the same kind of evidence — this is how the whole path is checked end to end.</summary>
    private static int RunHeadlessRecording(string mount, int seconds, string? output)
    {
        var session = RecordingSession.Start(mount);
        RecordingSessions.Track(session);
        var until = DateTime.UtcNow.AddSeconds(Math.Max(1, seconds));
        while (DateTime.UtcNow < until)
        {
            var pulse = session.Poll(DateTime.UtcNow);
            Console.WriteLine($"{pulse.Time.ToLocalTime():HH:mm:ss} {pulse.State} · requests {pulse.RequestLines} lines/"
                + $"{pulse.RequestBytes} B · samples {pulse.SampleRows} rows · seconds {pulse.SecondRows}"
                + (pulse.Gaps.Count == 0 ? "" : $" · gaps {pulse.Gaps.Count}") + (pulse.Error is null ? "" : " · " + pulse.Error));
            Thread.Sleep(1000);
        }
        var submission = session.Freeze(DateTime.UtcNow);
        RecordingSessions.Forget(session);
        var outcome = StatsExport.Write(output ?? Path.GetTempPath(), submission.ToSnapshot(I18n.Language));
        session.ReleaseWorkingCopy();
        Console.WriteLine($"Bundle: {outcome.Directory} ({outcome.Files} files, {outcome.Samples} rows, {outcome.Logs} logs, "
            + $"{submission.RequestLines} request lines, {submission.Gaps.Count} gaps)");
        return 0;
    }

    /// <summary>The path every window now takes: ask the supervisor once where the instances are,
    /// then talk to each instance over its own pipe. Nothing here needs a mount to be mounted —
    /// what is checked is that the list resolves, that a name resolves to the address the list
    /// gave, and that the process at that address answers for the instance it was named for.</summary>
    private static void VerifyInstanceDirectory()
    {
        var response = SupervisorClient.Default.Instances();
        if (!response.Ok) throw new InvalidOperationException("The service refused the instance list: " + response.Error);
        foreach (var endpoint in response.Instances)
        {
            if (endpoint.Id.Length == 0 || endpoint.Pipe.Length == 0)
                throw new InvalidOperationException($"Instance '{endpoint.Name}' is listed without an address (id='{endpoint.Id}', pipe='{endpoint.Pipe}')");
            var client = InstanceDirectory.ClientFor(endpoint.Name);
            if (client.PipeName != endpoint.Pipe)
                throw new InvalidOperationException($"Instance '{endpoint.Name}' resolves to '{client.PipeName}' rather than the '{endpoint.Pipe}' it is served on");
            var status = client.Status();
            if (!status.Ok || status.Mounts.All(mount => !string.Equals(mount.Name, endpoint.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"The process at '{endpoint.Pipe}' did not answer for '{endpoint.Name}': {status.Error ?? "no such mount"}");
        }
        Console.WriteLine($"Instance directory: {response.Instances.Count} instance(s) reachable over their own pipes.");
    }

    /// <summary>What the dashboard shows is two answers put together: the supervisor knows which
    /// instances exist and which process runs each, and each instance's own process knows what its
    /// layers are doing. What is checked here is that the second half is really in there — a layer
    /// generation is something only the instance itself can report, since the supervisor reads the
    /// configuration and the configuration has no generations — so a status that quietly lost the
    /// worker's half cannot pass for one that has it.</summary>
    private static void VerifyInstanceStatus()
    {
        var merged = InstanceView.Status();
        if (!merged.Ok) throw new InvalidOperationException("The merged status was refused: " + merged.Error);
        var supervisor = SupervisorClient.Default.Status().Mounts;
        foreach (var row in merged.Mounts.Where(row => row.Mounted))
        {
            if (row.Providers.Count == 0 || row.Providers.All(provider => provider.Generation.Length == 0))
                throw new InvalidOperationException($"Instance '{row.Name}' reads as mounted but carries nothing from its own process");
            if (row.WorkerPid <= 0) throw new InvalidOperationException($"Instance '{row.Name}' reads as mounted with no process behind it");
            if (supervisor.All(own => own.Name != row.Name))
                throw new InvalidOperationException($"Instance '{row.Name}' is missing from the supervisor's own view");
        }
        Console.WriteLine($"Instance status: {merged.Mounts.Count} instance(s), {merged.Mounts.Count(row => row.Mounted)} mounted and reported by their own processes.");
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
                stats.CaptureTable(Path.Combine(capture, "stats-table.png"));
                stats.CaptureRecording(Path.Combine(capture, "recording.png"));
                stats.CaptureProcess(Path.Combine(capture, "process.png"));
                editor.CaptureCacheAction(Path.Combine(capture, "cache-action.png"));
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
                editor.CaptureCacheAction(Path.Combine(capture, "cache-action-alternate.png"));
            }
            if (ticks == 40)
            {
                // A catalog that is missing a key answers with the key itself, which is what the
                // window would then show, so every string these windows name is asked for here.
                foreach (string key in new[] { "dashboard.title", "cache.clear", "confirm.cache", "cache.done",
                    "stats.process", "stats.process.cpu", "stats.process.memory", "stats.process.threads",
                    "stats.process.handles", "stats.process.figures", "stats.process.definition" })
                    if (I18n.T(key) == key) failure = "Qt resource translation lookup failed: " + key;
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
                    stats.CaptureRecording(Path.Combine(capture, "recording-small.png"));
                    editor.CaptureCacheAction(Path.Combine(capture, "cache-action-small.png"));
                }
            }
            if (ticks >= 60)
            {
                timer.Stop();
                try
                {
                    VerifyInstanceDirectory();
                    VerifyInstanceStatus();
                    stats.VerifyResponseSampling();
                    stats.VerifyChartScale();
                    stats.VerifyCharts();
                    stats.VerifyProcess();
                    stats.VerifyRecording(capture == null ? null : Path.Combine(capture, "chart-markers.png"));
                    stats.SmokeRecordingStartAndDiscard(capture == null ? null : Path.Combine(capture, "recording-live.png"));
                    stats.VerifyRecordingBundle(Path.Combine(capture ?? Path.GetTempPath(), "polymount-export-smoke"));
                }
                catch (Exception ex) { failure = ex.Message; }
                Console.WriteLine(failure ?? "UI smoke passed: tab reorder, four-way splits, detach/return, preserved drafts, Dashboard, ConfigEditor, Stats with its recording panel, and live localization.");
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
