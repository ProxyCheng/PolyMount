using System.Runtime.InteropServices;
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
        InstallCrashTracer();
        TrayLog.Start(window: args.Length == 0);
        I18n.Initialize();
        TrayTheme.Initialize();
        Serilog.Log.Information("PolyMount tray {Version} starting ({Mode})",
            typeof(Program).Assembly.GetName().Version, args.Length == 0 ? "window" : string.Join(' ', args));
        using var options = new QCommandLineParser();
        options.SetApplicationDescription("PolyMount tray and object layer management.");
        using var help = options.AddHelpOption();
        using var install = new QCommandLineOption("install-shell-menu", "Register the Explorer context menu for the current user.");
        using var smoke = new QCommandLineOption("ui-smoke", "Run bounded UI checks without saving mount configuration.");
        using var checksOption = new QCommandLineOption("checks",
            "With --ui-smoke, run only these areas (comma separated; 'list' names them). All of them when omitted.", "areas");
        using var captureOption = new QCommandLineOption("capture", "Save UI smoke screenshots to an existing directory.", "directory");
        using var manage = new QCommandLineOption(new[] { "manage-cache", "probe" }, "Open object cache boundary management (repeatable).", "path");
        using var record = new QCommandLineOption("record", "Record an instance for --seconds and write the bundle, without a page.", "mount");
        using var secondsOption = new QCommandLineOption("seconds", "How long --record collects for.", "seconds", "20");
        using var outputOption = new QCommandLineOption("output", "Where --record writes the bundle.", "directory");
        using var render = new QCommandLineOption("render", "Draw the performance cards from a fixture into an existing directory, with no service.", "directory");
        using var sizeOption = new QCommandLineOption("size", "Card size for --render, as WxH.", "size", "418x280");
        using var languageOption = new QCommandLineOption("lang", "Language for --render: en or zh-CN.", "language");
        options.AddOption(install);
        options.AddOption(smoke);
        options.AddOption(checksOption);
        options.AddOption(captureOption);
        options.AddOption(manage);
        options.AddOption(record);
        options.AddOption(secondsOption);
        options.AddOption(outputOption);
        options.AddOption(render);
        options.AddOption(sizeOption);
        options.AddOption(languageOption);
        options.AddPositionalArgument("paths", "Additional objects to manage with --manage-cache.", "[paths...]");
        if (!options.Parse(QCoreApplication.Arguments()))
        {
            Console.Error.WriteLine(options.ErrorText());
            return 2;
        }
        if (options.IsSet(help)) { Console.WriteLine(options.HelpText()); return 0; }
        if (options.IsSet(captureOption) && !options.IsSet(smoke) ||
            options.IsSet(checksOption) && !options.IsSet(smoke) ||
            (options.IsSet(secondsOption) || options.IsSet(outputOption)) && !options.IsSet(record) ||
            (options.IsSet(sizeOption) || options.IsSet(languageOption)) && !options.IsSet(render) ||
            options.PositionalArguments().Length > 0 && !options.IsSet(manage) ||
            new[] { options.IsSet(install), options.IsSet(smoke), options.IsSet(manage), options.IsSet(record), options.IsSet(render) }.Count(set => set) > 1)
        {
            Console.Error.WriteLine("Choose one mode; --capture and --checks require --ui-smoke, --seconds/--output require --record, "
                + "--size and --lang require --render, and object paths require --manage-cache.");
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
        {
            string? checks = options.IsSet(checksOption) ? options.Value(checksOption) : null;
            if (string.Equals(checks?.Trim(), "list", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Smoke areas: " + string.Join(", ", SmokeAreas));
                return 0;
            }
            HashSet<string>? areas;
            try
            {
                areas = ParseSmokeAreas(checks);
            }
            catch (ArgumentException error)
            {
                Console.Error.WriteLine(error.Message);
                return 2;
            }
            return RunUiSmoke(options.IsSet(captureOption) ? options.Value(captureOption) : null, areas);
        }

        // The performance cards drawn from a fixture: looking at how a card is laid out is looking
        // at a picture, and a picture of a chart does not need an instance behind it.
        if (options.IsSet(render))
        {
            if (!CardSize(options.Value(sizeOption), out int width, out int height))
            {
                Console.Error.WriteLine($"--size takes WxH of at least 240x200, not '{options.Value(sizeOption)}'");
                return 2;
            }
            return ChartPreview.Run(options.Value(render), width, height,
                options.IsSet(languageOption) ? options.Value(languageOption) : null);
        }

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

    private delegate void CrashTracerInstall();

    /// <summary>A native crash inside the Qt binding prints nothing by itself — no managed stack, no
    /// symbols, just a faulting offset in a module. Set <c>POLYMOUNT_CRASH_TRACER</c> to the path of
    /// the QtSharp crash tracer (<c>D:\QtSharp\build_crashtracer.bat</c> builds it) and this process
    /// installs it at startup: it walks the stack on an access violation and writes every frame as
    /// module + offset + symbol to <c>D:\QtSharp\crash_stack.txt</c>. Nothing is loaded unless the
    /// variable names a file that is there.</summary>
    private static void InstallCrashTracer()
    {
        string? tracer = Environment.GetEnvironmentVariable("POLYMOUNT_CRASH_TRACER");
        if (string.IsNullOrEmpty(tracer) || !File.Exists(tracer)) return;
        try
        {
            var library = NativeLibrary.Load(tracer);
            var install = (CrashTracerInstall)Marshal.GetDelegateForFunctionPointer(
                NativeLibrary.GetExport(library, "InstallCrashTracer"), typeof(CrashTracerInstall));
            install();
        }
        catch (Exception ex) { Serilog.Log.Warning(ex, "the crash tracer was not installed"); }
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
        // When the setup registered the entry for every user, this tray cannot take it away again
        // (that is an HKLM write) and has nothing left to add, so it reports the fact instead of
        // offering a switch that could not move.
        bool shellForEveryone = ShellMenuInstaller.IsMachineWide();
        I18n.Bind(shell.SetText, shellForEveryone ? "tray.shell.allusers" : "tray.shell");
        shell.SetCheckable(!shellForEveryone);
        shell.SetChecked(ShellMenuInstaller.IsInstalled());
        shell.SetEnabled(!shellForEveryone);
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
        if (_dashboard == null)
        {
            var window = new Dashboard();
            // The reader can close the window for good, so what is kept here has to let go of it:
            // the next open builds a new one rather than showing a destroyed widget.
            window.Destroyed += _ => { if (ReferenceEquals(_dashboard, window)) _dashboard = null; };
            _dashboard = window;
        }
        if (_dashboard.IsMinimized()) _dashboard.ShowNormal();
        _dashboard.ShowWindow();
    }

    /// <summary>Self-check for opening the dashboard after the reader has closed it: a window of its
    /// own is built rather than the one that was closed being shown again. In two steps, because the
    /// window a close let go of is deleted when the event loop next gets a turn — a second open
    /// before that turn is still holding the window that is on its way out — and because it goes
    /// through the same call the tray icon and its menu make, which is where that remembering lives.
    /// </summary>
    private static Dashboard? _closedDashboard;

    private static void CloseDashboardForCheck()
    {
        ShowDashboard();
        _closedDashboard = _dashboard is { } opened
            ? opened
            : throw new InvalidOperationException("Opening the dashboard left nothing to show");
        _closedDashboard.Close();
    }

    private static void VerifyDashboardReopens()
    {
        var closed = _closedDashboard ?? throw new InvalidOperationException("No dashboard was closed to reopen");
        _closedDashboard = null;
        ShowDashboard();
        if (_dashboard is not { } reopened || ReferenceEquals(reopened, closed))
            throw new InvalidOperationException("Opening the dashboard after closing it did not build a window of its own");
        reopened.Close();
    }

    /// <summary>The first instance the service is running, so a smoke run charts live data
    /// rather than an empty window.</summary>
    private static string? LiveInstance()
    {
        try { return SupervisorClient.Default.Status().Mounts.FirstOrDefault(mount => mount.Enabled && mount.Mounted)?.Name; }
        catch { return null; }
    }

    /// <summary>The size a render draws its cards at, as WxH. A size that is not one is refused
    /// rather than defaulted: a render is looked at, and a picture drawn at a size nobody asked for
    /// is read as a layout that is wrong.</summary>
    private static bool CardSize(string size, out int width, out int height)
    {
        var parts = size.Split('x', 'X');
        width = height = 0;
        return parts.Length == 2 && int.TryParse(parts[0], out width) && int.TryParse(parts[1], out height)
            && width >= 240 && height >= 200;
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

    /// <summary>The parts of the smoke run that can be asked for by name. A check that fails in one
    /// window should not cost the time of driving every other one, and a change to one window
    /// should not have to be verified against every other one either — so the run says what it
    /// covers, and the caller can ask for less.</summary>
    internal static readonly string[] SmokeAreas =
        { "workspace", "dashboard", "editor", "object", "service", "stats", "recording", "locale" };

    /// <summary>The areas a run was asked for, or null for all of them. Names are matched
    /// case-insensitively, and a name this build does not know is refused rather than ignored:
    /// a typo that silently ran nothing would read as a pass.</summary>
    private static HashSet<string>? ParseSmokeAreas(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var areas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!SmokeAreas.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Unknown smoke area '{name}'. Known: {string.Join(", ", SmokeAreas)}");
            areas.Add(name.ToLowerInvariant());
        }
        return areas.Count == 0 ? null : areas;
    }

    private static int RunUiSmoke(string? capture, HashSet<string>? areas = null)
    {
        // Which parts of the run this invocation was asked for. Everything that is only a check,
        // or only the scenario a check needs, is behind one of these; what the run needs whatever
        // it was asked for — the windows, the event loop, the page drawn — is not.
        bool Runs(string area) => areas == null || areas.Contains(area);
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
        DateTime awayFrom = DateTime.MinValue;
        // The range check below counts the readings the page takes, so it pins the rate it measures
        // against and puts both choices back the way it found them.
        int rangeBefore = -1, readingsAtRangeChange = 0;
        DateTime rangeChangedAt = DateTime.MinValue;
        double rateBefore = 0;
        string? failure = null;
        ApiRuntime.UnhandledCallbackException += (context, ex) => failure = context + ": " + ex;
        timer.Timeout += () =>
        {
            ticks++;
            if (ticks == 2 && Runs("workspace"))
            {
                try { dashboard.SmokeTabs(instance); } catch (Exception ex) { failure = ex.ToString(); }
                // Those interactions close the performance tab and open it again, and a closed tab
                // takes its page with it: what the rest of this run drives — its captures, its
                // checks — is the page that came back rather than the one that was closed.
                stats = dashboard.OpenPerformance(instance);
            }
            // The reader changing the range is a change of window, not of cadence: readings are
            // counted from the moment the range changes, against a rate pinned here so the count
            // means the same thing on every machine.
            if (ticks == 4 && Runs("stats"))
            {
                rateBefore = stats.SamplingRate;
                stats.SmokeSamplingRate(1);
            }
            if (ticks == 8 && Runs("stats"))
            {
                readingsAtRangeChange = stats.ProcessReadings;
                rangeChangedAt = DateTime.UtcNow;
                rangeBefore = stats.SmokeChangeRange();
            }
            if (ticks == 44 && Runs("stats"))
            {
                try { stats.VerifyRangeKeepsCadence(readingsAtRangeChange, rangeChangedAt); }
                catch (Exception ex) { failure = ex.ToString(); }
                stats.SmokeRestoreRange(rangeBefore);
                stats.SmokeSamplingRate(rateBefore);
            }
            if (ticks == 18 && capture != null)
            {
                dashboard.Capture(Path.Combine(capture, "dashboard.png"));
                dashboard.CaptureDetail(capture);
                editor.Capture(Path.Combine(capture, "layers.png"));
                // Folded page first, then folded window, then the window again with the sections
                // opened — and the expanded window *before* the expanded page, deliberately: the
                // page capture lays the whole page out, which is a thing the live window never
                // does for itself, so taking it first would hide exactly what this shot is for.
                // The whole page is drawn for the checks and the pictures that follow: a card
                // outside the viewport is not drawn on purpose, and it would be verified and
                // photographed half empty.
                stats.DrawEverything();
                stats.CapturePage(Path.Combine(capture, "stats-page.png"));
                stats.Capture(Path.Combine(capture, "stats.png"));
                stats.ExpandAll();
                stats.Capture(Path.Combine(capture, "stats-expanded.png"));
                stats.CapturePage(Path.Combine(capture, "stats-page-expanded.png"));
                stats.CaptureTable(Path.Combine(capture, "stats-table.png"));
                stats.CaptureCharts(capture);
                stats.CaptureRecording(Path.Combine(capture, "recording.png"));
                stats.CaptureProcess(capture);
                stats.CaptureHeld(capture);
                editor.CaptureCacheAction(Path.Combine(capture, "cache-action.png"));
            }
            if (ticks == 20)
            {
                if (Runs("dashboard")) dashboard.VerifyPickers();
                if (Runs("object"))
                {
                    try { LayerVisualizer.SmokeBoundary(); } catch (Exception ex) { failure = ex.ToString(); }
                }
                if (Runs("editor"))
                {
                    try { editor.SmokeLayerPicker(capture == null ? null : Path.Combine(capture, "layer-picker.png")); }
                    catch (Exception ex) { failure = ex.ToString(); }
                    editor.SmokeInteractions();
                }
                if (Runs("dashboard")) dashboard.SmokeSilentUpdate();
                if (Runs("locale")) I18n.SetLanguage(original == "en" ? "zh-CN" : "en");
            }
            // Step away from the performance tab and back again, the way a reader glancing at
            // another tab does — Qt hides the page it is not showing — and read what the page took
            // down while it was not on screen: a page that pauses when it is not on show comes back
            // to a hole where the time it was away should be.
            if (ticks == 22 && Runs("stats")) { awayFrom = DateTime.UtcNow; stats.Hide(); }
            if (ticks == 48 && Runs("stats"))
            {
                var until = DateTime.UtcNow;
                stats.Show();
                try { stats.VerifyWatchedWhileHidden(awayFrom, until); }
                catch (Exception ex) { failure = ex.ToString(); }
            }
            if (ticks == 36 && capture != null)
            {
                if (Runs("editor"))
                {
                    try { editor.SmokeLayerPicker(Path.Combine(capture, "layer-picker-alternate.png")); }
                    catch (Exception ex) { failure = ex.ToString(); }
                }
                dashboard.Capture(Path.Combine(capture, "dashboard-alternate.png"));
                editor.Capture(Path.Combine(capture, "layers-alternate.png"));
                editor.CaptureCacheAction(Path.Combine(capture, "cache-action-alternate.png"));
            }
            if (ticks == 40 && Runs("locale"))
            {
                // A catalog that is missing a key answers with the key itself, which is what the
                // window would then show, so every string these windows name is asked for here.
                // Every string these windows name is asked for, so a catalog that lost one - a
                // resource bundle built before the catalog was, for instance - fails here rather
                // than quietly painting the key itself into a chart title.
                foreach (string key in new[] { "dashboard.title", "cache.clear", "confirm.cache", "cache.done",
                    "stats.process", "stats.process.cpu", "stats.process.memory", "stats.process.threads",
                    "stats.process.handles", "stats.process.figures", "stats.unit.cores",
                    "stats.process.moved", "stats.process.moved.read", "stats.process.moved.write",
                    "theme", "theme.system", "theme.light", "theme.dark",
                    "stats.access.reads", "stats.access.writes", "stats.access.byLayer", "stats.access.byProcess",
                    "stats.access.byProcess.aligned", "stats.access.byProcess.stacked", "stats.access.byProcess.hint",
                    "stats.access.other", "stats.process.pid", "stats.process.unattributed",
                    "stats.access.noreads", "stats.access.nowrites", "stats.access.slice",
                    "stats.layerHeld.memory", "stats.layerHeld.disk",
                    "stats.reconcile.bytes", "record.process",
                    "disable", "disable.done", "disable.tip", "enable.tip", "confirm.disable",
                    "stats.section.layers", "stats.section.layers.summary",
                    "stats.section.process.summary", "stats.section.account", "stats.section.account.summary" })
                    if (I18n.T(key) == key) failure = "Qt resource translation lookup failed: " + key;
                I18n.SetLanguage(original);
            }
            if (ticks == 45)
            {
                dashboard.Resize(560, 420);
            }
            if (ticks == 50)
            {
                if (Runs("workspace") && dashboard.Width > 560)
                    failure = $"Workspace windows could not shrink to the requested size: "
                        + $"{dashboard.Width}x{dashboard.Height} where 560x420 was asked for";
                if (capture != null)
                {
                    dashboard.Capture(Path.Combine(capture, "dashboard-small.png"));
                    editor.Capture(Path.Combine(capture, "layers-small.png"));
                    stats.Capture(Path.Combine(capture, "stats-small.png"));
                    stats.CaptureRecording(Path.Combine(capture, "recording-small.png"));
                    editor.CaptureCacheAction(Path.Combine(capture, "cache-action-small.png"));
                }
            }
            if (ticks == 52 && Runs("workspace"))
            {
                try { Dashboard.CloseWindowForCheck(); }
                catch (Exception ex) { failure = ex.ToString(); }
            }
            // Two ticks later, so that the event loop has been round once: a window that was only
            // hidden would still be there.
            if (ticks == 54 && Runs("workspace"))
            {
                try
                {
                    Dashboard.VerifyWindowClosed();
                    CloseDashboardForCheck();
                }
                catch (Exception ex) { failure = ex.ToString(); }
            }
            if (ticks == 56 && Runs("workspace"))
            {
                try { VerifyDashboardReopens(); }
                catch (Exception ex) { failure = ex.ToString(); }
            }
            if (ticks >= 60)
            {
                timer.Stop();
                try
                {
                    if (Runs("service"))
                    {
                        VerifyInstanceDirectory();
                        VerifyInstanceStatus();
                    }
                    // The whole page is drawn for the checks and the pictures that follow: a card
                    // outside the viewport is not drawn on purpose, and it would be verified and
                    // photographed half empty. A recording's bundle is built from that page too.
                    if (Runs("stats") || Runs("recording")) stats.DrawEverything();
                    if (Runs("stats"))
                    {
                        stats.VerifyResponseSampling();
                        stats.VerifyAccessSplit();
                        stats.VerifyAccessGrid();
                        stats.VerifyAccessModes();
                        stats.VerifyChartScale();
                        stats.VerifyLayout();
                        stats.VerifyCharts();
                        stats.VerifySmoothing();
                        stats.VerifyFrames();
                        stats.VerifyChartsModule(capture == null ? null : Path.Combine(capture, "charts-module.png"));
                        stats.VerifyHistories();
                        stats.VerifyProcess();
                        stats.VerifyByteAccount();
                    }
                    if (Runs("recording"))
                    {
                        stats.VerifyRecording(capture == null ? null : Path.Combine(capture, "chart-markers.png"));
                        stats.SmokeRecordingStartAndDiscard(capture == null ? null : Path.Combine(capture, "recording-live.png"));
                        stats.VerifyRecordingBundle(Path.Combine(capture ?? Path.GetTempPath(), "polymount-export-smoke"));
                    }
                }
                catch (Exception ex) { failure = ex.ToString(); }
                string covered = areas == null
                    ? "tab reorder, four-way splits, detach/return, preserved drafts, Dashboard, ConfigEditor, "
                        + "Stats with its recording panel, and live localization"
                    : "areas " + string.Join(", ", SmokeAreas.Where(area => areas.Contains(area)));
                Console.WriteLine(failure ?? "UI smoke passed: " + covered + ".");
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
