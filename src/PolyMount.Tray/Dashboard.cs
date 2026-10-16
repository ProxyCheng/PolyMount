using System.Diagnostics;
using System.Globalization;
using PolyMount.Core.Ipc;
using QtSharp.Api;

namespace PolyMount.Tray;

/// <summary>Mount workspace. The Qt timer delivers background IPC results on the UI thread.</summary>
internal sealed class Dashboard : QWidget
{
    private readonly QTableWidget _table;
    private readonly QLineEdit _search;
    private readonly QComboBox _filter, _language;
    private readonly QLabel _service, _total, _active, _issues, _layerTotal;
    private readonly QLabel _listCount, _empty, _feedback, _updated;
    private readonly QLabel _detailName, _detailState, _mountPath, _layers, _warmup, _detailError;
    private readonly QWidget _detailBody;
    /// <summary>The panel the selected instance's details are in, kept because a smoke run
    /// photographs it: it is the part of the workspace a reader acts on, and in a workspace it is
    /// usually covered by whichever tab was opened over it.</summary>
    private readonly QWidget _detailPanel;
    private readonly QPushButton _refresh, _start, _stop, _create;
    private readonly QPushButton _toggleMount, _open, _disable, _clearCache, _edit, _performance, _inspect;
    private readonly QTimer _timer;
    private readonly List<MountStatus> _mounts = new();
    private List<MountStatus> _visible = new();
    private Task<Result>? _pending;
    private bool _connected, _filling, _wasVisible;
    private string? _selectedName;
    private DateTime _nextRefresh;
    private readonly TabWorkspace _tabs;
    private bool _quietPending;
    private Action? _queued;
    private sealed record Result(ControlResponse? Response, string? Message, string? Error, bool Quiet, bool ServiceAction = false);
    private static string T(string key) => I18n.T(key);

    public Dashboard()
    {
        SetObjectName("dashboard");
        SetWindowIcon(TrayIcons.App);
        Resize(1280, 820);
        SetStyleSheet(TrayTheme.StyleSheet);
        I18n.Bind(SetWindowTitle, "dashboard.title");
        SetMinimumSize(480, 360);
        var shell = new QVBoxLayout(this);
        shell.SetContentsMargins(0, 0, 0, 0);
        _tabs = new TabWorkspace(this);
        shell.AddWidget(_tabs);
        var overviewPage = new QWidget();
        _tabs.Open("overview", () => overviewPage, () => T("overview"), pinned: true);
        var root = new QHBoxLayout(WindowScrollArea.Create(overviewPage, 1080));
        overviewPage.SetMinimumSize(0, 0);
        overviewPage.SetSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Ignored);
        root.SetContentsMargins(0, 0, 0, 0);
        root.SetSpacing(0);

        var sidebar = Panel("sidebar");
        sidebar.SetFixedWidth(204);
        var nav = new QVBoxLayout(sidebar);
        nav.SetContentsMargins(18, 28, 18, 22);
        nav.SetSpacing(12);
        var brand = new QLabel();
        brand.SetPixmap(TrayIcons.BrandPixmap);
        nav.AddWidget(brand);
        nav.AddWidget(Label("brand.subtitle", "sidebarMuted"));
        nav.AddSpacing(28);
        nav.AddWidget(Label("workspace", "sidebarMuted"));
        nav.AddWidget(Label("overview", "navActive"));
        nav.AddStretch(1);
        nav.AddWidget(Label("service.connection", "sidebarMuted"));
        _service = PlainLabel("", "serviceBadge");
        nav.AddWidget(_service);
        _start = Button("service.start", () => RunService(true), "navButton", "play");
        _stop = Button("service.stop", () => RunService(false), "navButton", "stop");
        nav.AddWidget(_start);
        nav.AddWidget(_stop);
        nav.AddSpacing(12);
        nav.AddWidget(Label("language", "sidebarMuted"));
        _language = ContinuousScrolling.Combo();
        // A language picker names each language in its own language, so both entries read
        // the same whichever catalog is active; the platform supplies the endonym instead
        // of writing it into the code, where only the catalogs belong.
        _language.AddItem(CultureInfo.GetCultureInfo("zh-CN").NativeName);
        _language.AddItem(CultureInfo.GetCultureInfo("en").NativeName);
        _language.SetCurrentIndex(I18n.Language == "zh-CN" ? 0 : 1);
        _language.CurrentIndexChanged += i => I18n.SetLanguage(i == 0 ? "zh-CN" : "en");
        nav.AddWidget(_language);
        nav.AddWidget(Label("tray.keepRunning", "sidebarMuted"));
        root.AddWidget(sidebar);

        var content = new QVBoxLayout();
        content.SetContentsMargins(26, 24, 26, 18);
        content.SetSpacing(18);
        root.AddLayout(content, 1);
        var heading = new QHBoxLayout();
        var title = new QVBoxLayout();
        title.SetSpacing(5);
        title.AddWidget(Label("dashboard.heading", "pageTitle"));
        title.AddWidget(Label("dashboard.subtitle", "muted"));
        heading.AddLayout(title, 1);
        heading.AddStretch(1);
        _refresh = Button("refresh", () => Refresh(), icon: "refresh");
        I18n.Bind(_refresh.SetToolTip, "refresh.tip");
        heading.AddWidget(_refresh);
        _create = Button("mount.new", () => OpenEditor(create: true), "primary", "plus");
        heading.AddWidget(_create);
        content.AddLayout(heading);

        var overview = new QHBoxLayout();
        overview.SetSpacing(12);
        _total = Metric(overview, "metric.total", "metric.total.hint", "drive");
        _active = Metric(overview, "metric.active", "metric.active.hint", "check");
        _issues = Metric(overview, "metric.issues", "metric.issues.hint", "alert");
        _layerTotal = Metric(overview, "metric.layers", "metric.layers.hint", "layers");
        content.AddLayout(overview);

        var workspace = new QSplitter();
        workspace.SetHandleWidth(14);
        workspace.SetChildrenCollapsible(false);
        var listPanel = Panel("surface");
        var list = new QVBoxLayout(listPanel);
        list.SetContentsMargins(18, 18, 18, 16);
        list.SetSpacing(14);
        var listHeading = new QHBoxLayout();
        listHeading.AddWidget(Label("mount.instances", "sectionTitle"));
        listHeading.AddStretch(1);
        _listCount = PlainLabel("", "muted");
        listHeading.AddWidget(_listCount);
        list.AddLayout(listHeading);
        var tools = new QHBoxLayout();
        _search = new QLineEdit();
        I18n.Bind(_search.SetPlaceholderText, "search.placeholder");
        _search.SetClearButtonEnabled(true);
        _search.TextChanged += _ => FillTable();
        tools.AddWidget(_search, 1);
        _filter = ContinuousScrolling.Combo();
        UpdateFilters();
        _filter.CurrentIndexChanged += _ => FillTable();
        tools.AddWidget(_filter);
        list.AddLayout(tools);
        _table = ContinuousScrolling.Table();
        _table.SetColumnCount(3);
        _table.SetEditTriggers(QAbstractItemView.EditTriggers.NoEditTriggers);
        _table.SetSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows);
        _table.SetSelectionMode(QAbstractItemView.SelectionMode.SingleSelection);
        _table.SetShowGrid(false);
        _table.SetWordWrap(false);
        _table.SetFrameShape(QFrame.Shape.NoFrame);
        _table.VerticalHeader().Hide();
        _table.HorizontalHeader().SetSectionResizeMode(0, QHeaderView.ResizeMode.Stretch);
        for (int i = 1; i < 3; i++) _table.HorizontalHeader().SetSectionResizeMode(i, QHeaderView.ResizeMode.ResizeToContents);
        _table.ItemSelectionChanged += SelectMount;
        _table.CellDoubleClicked += (_, _) => OpenSelected();
        list.AddWidget(_table, 1);
        _empty = PlainLabel("", "emptyState");
        _empty.SetAlignment(Qt.Alignment.AlignCenter);
        list.AddWidget(_empty);
        list.AddWidget(Label("mount.list.hint", "muted"));
        workspace.AddWidget(listPanel);

        _detailPanel = Panel("surface");
        var details = _detailPanel;
        details.SetMinimumWidth(306);
        details.SetMaximumWidth(390);
        var detail = new QVBoxLayout(details);
        detail.SetContentsMargins(20, 20, 20, 20);
        detail.SetSpacing(12);
        detail.AddWidget(Label("detail", "eyebrow"));
        _detailName = PlainLabel("", "detailTitle");
        detail.AddWidget(_detailName);
        _detailState = PlainLabel("", "muted");
        detail.AddWidget(_detailState);
        var scroll = new QScrollArea();
        scroll.SetWidgetResizable(true);
        scroll.SetFrameShape(QFrame.Shape.NoFrame);
        _detailBody = new QWidget();
        var body = new QVBoxLayout(_detailBody);
        body.SetContentsMargins(0, 8, 2, 0);
        body.SetSpacing(10);
        // The instance's own switch comes first, because switching it is what a reader comes to
        // this panel to do. One button covers the three states it has — switched off in the
        // configuration, enabled but not running, and mounted — because "this instance is off" is
        // one idea to a reader. It used to take two controls in two windows to say it, and neither
        // of them could reach the state the other one set.
        //
        // One to a row, not two: the panel is as narrow as 306 pixels, and a button's label is the
        // one thing here that cannot be shortened for it — in English the longest of these runs to
        // "Clear instance cache", which with a second button beside it does not fit.
        _toggleMount = Button("mount", ToggleSelected, "primary", "play");
        body.AddWidget(_toggleMount);
        _open = Button("open.folder", OpenSelected, icon: "folder");
        body.AddWidget(_open);
        _performance = Button("mount.performance", OpenPerformance, icon: "chart");
        body.AddWidget(_performance);
        _edit = Button("mount.manage", () => OpenEditor(Selected?.Name), icon: "settings");
        body.AddWidget(_edit);
        _inspect = Button("layers.inspect", () => { if (Selected is { } m) LayerVisualizer.Open(m.MountPoint); }, icon: "layers");
        body.AddWidget(_inspect);
        // Last, and after the entries above rather than beside the switch: these two are the
        // deliberate ones. Stopping the instance now is not the same as stopping it coming back,
        // so the one that writes the configuration has a button of its own and says which it is.
        _disable = Button("disable", ToggleEnabled, icon: "stop");
        body.AddWidget(_disable);
        _clearCache = Button("cache.clear", ClearSelectedCache, icon: "disk-cache");
        body.AddWidget(_clearCache);

        _mountPath = DetailField(body, "mount.location");
        // The service publishes this sentence in both languages, the same way provider
        // details are published, so it needs no catalog entry of its own.
        _warmup = PlainLabel("", "detailValue");
        body.AddWidget(_warmup);
        body.AddWidget(Label("layers.heading", "eyebrow"));
        _layers = PlainLabel("", "layerChain");
        body.AddWidget(_layers);
        _detailError = PlainLabel("", "errorBox");
        body.AddWidget(_detailError);
        body.AddStretch(1);
        scroll.SetWidget(_detailBody);
        detail.AddWidget(scroll, 1);
        workspace.AddWidget(details);
        workspace.SetStretchFactor(0, 3);
        workspace.SetStretchFactor(1, 2);
        content.AddWidget(workspace, 1);
        _feedback = PlainLabel(T("connecting"), "feedback");
        content.AddWidget(_feedback);
        var footer = new QHBoxLayout();
        var footerHint = Label("dashboard.footer", "muted");
        footerHint.SetWordWrap(false);
        footer.AddWidget(footerHint);
        footer.AddStretch(1);
        _updated = PlainLabel("", "muted");
        _updated.SetWordWrap(false);
        footer.AddWidget(_updated);
        content.AddLayout(footer);
        _timer = new QTimer(this);
        _timer.Timeout += Tick;
        _timer.Start(100);
        Translate();
        Refresh();
    }

    public void ShowWindow()
    {
        Show();
        Raise();
        ActivateWindow();
        if (_pending == null) Refresh(quiet: true);
    }

    internal void Capture(string path) { using var image = Grab(); image.Save(path); }

    /// <summary>The instance detail panel on its own. The window shot cannot show it: the panel
    /// lives in the workspace, and by the time a smoke run has opened the windows it is testing,
    /// the tabs it opened are lying over it.</summary>
    internal void CaptureDetail(string directory)
    {
        _detailPanel.Layout()?.Activate();
        QCoreApplication.ProcessEvents();
        using var panel = _detailPanel.Grab();
        panel.Save(Path.Combine(directory, "detail.png"));
    }

    internal void SmokeSilentUpdate()
    {
        if (_visible.Count == 0) return;
        var item = _table.Item(0, 0);
        int row = _table.CurrentRow();
        int scroll = _table.VerticalScrollBar().Value;
        FillTable();
        if (_table.Item(0, 0).Handle != item.Handle || _table.CurrentRow() != row || _table.VerticalScrollBar().Value != scroll)
            throw new InvalidOperationException("Silent update replaced table items or changed selection/scroll position");
    }

    private MountStatus? Selected => _mounts.FirstOrDefault(m => m.Name == _selectedName);
    private void Refresh(bool quiet = false) => Run(InstanceView.Status, null, quiet);

    private void Run(Func<ControlResponse> call, string? successKey, bool quiet = false)
    {
        if (_pending != null)
        {
            if (_quietPending && !quiet)
            {
                _queued = () => Run(call, successKey);
                _quietPending = false;
                UpdateActions();
            }
            return;
        }
        _quietPending = quiet;
        if (!quiet) Feedback(T(successKey == null ? "refreshing" : "working"));
        _pending = Task.Run(() =>
        {
            try
            {
                var response = call();
                return new Result(response, successKey, response.Ok ? null : response.Error ?? "Unknown error", quiet);
            }
            catch (Exception ex) { return new Result(null, null, ex.Message, quiet); }
        });
        UpdateActions();
    }

    private void Tick()
    {
        if (_pending is { IsCompleted: true })
        {
            var result = _pending.GetAwaiter().GetResult();
            _pending = null;
            if (result.ServiceAction)
            {
                Feedback(result.Message ?? result.Error ?? "", result.Error != null);
                _nextRefresh = DateTime.UtcNow.AddSeconds(1);
            }
            else
            {
                _connected = result.Response != null;
                if (result.Response is { Ok: true } response)
                {
                    _mounts.Clear();
                    _mounts.AddRange(response.Mounts);
                    UpdateOverview();
                    FillTable();
                    _updated.SetText(I18n.F("updated", DateTime.Now.ToString("HH:mm:ss")));
                    if (!result.Quiet) Feedback(T(result.Message ?? "refreshed"));
                    else if (_feedback.Text.StartsWith(T("connection.failed"), StringComparison.Ordinal)) Feedback(T("connected"));
                }
                else if (result.Error != null)
                    Feedback(T(result.Response == null ? "connection.failed" : "operation.failed") + " " + result.Error, true);
                if (!_connected)
                {
                    foreach (var value in new[] { _total, _active, _issues, _layerTotal }) value.SetText("—");
                    _updated.SetText(T("offline.retry"));
                    FillTable();
                }
                _nextRefresh = DateTime.UtcNow.AddSeconds(10);
            }
            UpdateConnection();
            UpdateActions();
            if (_queued is { } queued) { _queued = null; queued(); }
        }
        bool visible = IsVisible();
        if (visible && _pending == null && (!_wasVisible || DateTime.UtcNow >= _nextRefresh)) Refresh(quiet: true);
        _wasVisible = visible;
    }

    private void Translate()
    {
        UpdateFilters();
        _table.SetHorizontalHeaderLabels(new[] { T("column.instance"), T("state"), T("column.layers") });
        UpdateConnection();
        if (_connected) UpdateOverview();
        FillTable();
        _feedback.SetText(T(_connected ? "connected" : "connecting"));
        _updated.SetText(T("auto.refresh"));
    }
    public override void ChangeEvent(QEvent e)
    {
        base.ChangeEvent(e);
        if (e.type() == QEvent.Type.LanguageChange && _table != null) Translate();
    }

    private void UpdateFilters()
    {
        int selected = Math.Max(0, _filter.CurrentIndex);
        _filter.BlockSignals(true);
        _filter.Clear();
        foreach (var key in new[] { "filter.all", "state.mounted", "state.unmounted", "state.error", "state.disabled" }) _filter.AddItem(T(key));
        _filter.SetCurrentIndex(selected);
        _filter.BlockSignals(false);
    }

    private void UpdateConnection()
    {
        SetText(_service, "●  " + T(_connected ? "service.online" : "service.offline"));
        string style = _connected ? "color: #6ee7b7;" : "color: #fbbf24;";
        if (_service.StyleSheet != style) _service.SetStyleSheet(style);
    }

    private void UpdateOverview()
    {
        SetText(_total, _mounts.Count.ToString());
        SetText(_active, _mounts.Count(m => m.Mounted).ToString());
        int issues = _mounts.Count(m => !string.IsNullOrWhiteSpace(m.Error));
        SetText(_issues, issues.ToString());
        string style = issues > 0 ? "color: #dc2626;" : "color: #17243b;";
        if (_issues.StyleSheet != style) _issues.SetStyleSheet(style);
        SetText(_layerTotal, _mounts.Sum(m => m.Providers.Count).ToString());
    }

    private void FillTable()
    {
        if (_table == null) return;
        string query = _search.Text.Trim();
        var visible = _mounts.Where(m =>
            (query.Length == 0 || $"{m.Name} {m.MountPoint}".Contains(query, StringComparison.OrdinalIgnoreCase)) &&
            (_filter.CurrentIndex switch { 1 => m.Mounted, 2 => !m.Mounted, 3 => !string.IsNullOrWhiteSpace(m.Error), 4 => !m.Enabled, _ => true })).ToList();
        bool structureChanged = !_visible.Select(m => m.Name).SequenceEqual(visible.Select(m => m.Name));
        _visible = visible;
        _filling = true;
        if (structureChanged) _table.SetUpdatesEnabled(false);
        if (structureChanged) _table.SetRowCount(_visible.Count);
        for (int row = 0; row < _visible.Count; row++)
        {
            var m = _visible[row];
            var name = Cell(row, 0, m.Name + "\n" + m.MountPoint);
            name.SetToolTip(m.Name + "\n" + m.MountPoint);
            if (structureChanged) name.SetIcon(TrayIcons.Get("drive"));
            bool stateChanged = structureChanged || _table.Item(row, 1)?.Text() != StateText(m);
            var state = Cell(row, 1, StateText(m));
            if (stateChanged) state.SetForeground(new QBrush(new QColor(StateColor(m))));
            state.SetToolTip(m.Error ?? StateText(m));
            Cell(row, 2, I18n.F("layer.count", m.Providers.Count));
            if (structureChanged) _table.SetRowHeight(row, 68);
        }
        int selected = _visible.FindIndex(m => m.Name == _selectedName);
        if (selected < 0 && _visible.Count > 0) selected = 0;
        bool changed = _selectedName != (selected < 0 ? null : _visible[selected].Name);
        _selectedName = selected < 0 ? null : _visible[selected].Name;
        if (selected >= 0 && (structureChanged || changed)) _table.SelectRow(selected);
        if (structureChanged) _table.SetUpdatesEnabled(true);
        _filling = false;
        SetText(_listCount, I18n.F("list.count", _visible.Count, _mounts.Count) + (_connected ? "" : " · " + T("last.known")));
        _empty.SetVisible(_visible.Count == 0);
        SetText(_empty, T(!_connected ? "empty.offline" : _mounts.Count == 0 ? "empty.new" : "empty.search"));
        UpdateDetails(changed);
    }

    private void SelectMount()
    {
        if (_filling) return;
        int row = _table.CurrentRow();
        _selectedName = row >= 0 && row < _visible.Count ? _visible[row].Name : null;
        UpdateDetails(true);
    }

    private void UpdateDetails(bool changed = false)
    {
        var m = Selected;
        _detailBody.SetVisible(m != null);
        SetText(_detailName, m?.Name ?? T("detail.select"));
        SetText(_detailState, m == null ? T("detail.hint") : (_connected ? StateText(m) : T("detail.offline")));
        string style = m == null || !_connected ? "color: #64748b;" : $"color: {StateColor(m)};";
        if (_detailState.StyleSheet != style) _detailState.SetStyleSheet(style);
        if (m != null)
        {
            SetText(_mountPath, m.MountPoint);
            _warmup.SetVisible(m.WarmupDetail != null);
            if (m.WarmupDetail != null)
                SetText(_warmup, string.Format(I18n.Culture, m.WarmupDetail.Resolve(I18n.Language),
                    m.WarmedDirectories, m.WarmupLimit));
            SetText(_layers, string.Join("\n↓\n", m.Providers.Select((p, i) => $"{i + 1:00}  {(string.IsNullOrEmpty(p.DisplayName) ? p.Name : p.DisplayName)}   ·   {T(p.Writable ? "writable" : "readonly")}")));
            _detailError.SetVisible(!string.IsNullOrWhiteSpace(m.Error));
            SetText(_detailError, T("instance.error") + "\n" + m.Error);
        }
        UpdateActions();
    }

    private void UpdateActions()
    {
        bool idle = _pending == null || _quietPending;
        var m = Selected;
        bool ready = idle && _connected && m != null;
        _refresh.SetEnabled(idle);
        string refreshText = T(idle ? "refresh" : "working.short");
        if (_refresh.Text != refreshText) _refresh.SetText(refreshText);
        _start.SetEnabled(idle && !_connected);
        _stop.SetEnabled(idle && _connected);
        _create.SetEnabled(idle && _connected);
        // One button over the instance's three states. The disabled one is the case that used to be
        // out of reach from here: an instance switched off in the configuration has no process, so
        // there was nothing to mount, and the button was greyed out with a tooltip telling the
        // reader to go and find the checkbox in the editor.
        bool disabled = m?.Enabled == false;
        _toggleMount.SetEnabled(ready);
        string toggleText = T(m == null ? "mount" : disabled ? "enabled" : m.Mounted ? "unmount" : "mount");
        if (_toggleMount.Text != toggleText)
        {
            _toggleMount.SetText(toggleText);
            _toggleMount.SetIcon(TrayIcons.Get(m is { Mounted: true, Enabled: true } ? "stop" : "play", "#ffffff"));
        }
        _toggleMount.SetToolTip(T(disabled ? "enable.tip" : "selected.tip"));
        // Offered only while there is something to switch off. An instance that is already off has
        // the button above it for turning back on, and showing both would read as two ways to do
        // one thing.
        _disable.SetVisible(m?.Enabled == true);
        _disable.SetEnabled(ready);
        _disable.SetToolTip(T("disable.tip"));
        _clearCache.SetEnabled(ready && m!.Mounted);
        _open.SetEnabled(ready && m!.Mounted);
        _edit.SetEnabled(ready);
        _performance.SetEnabled(ready);
        _inspect.SetEnabled(ready && m!.Providers.Count > 0);
        // A recording is part of the performance panel, and this is how a reader finds it again
        // after moving the tab somewhere else: the entry says so rather than looking ordinary.
        bool recording = m?.Recording == true;
        string performanceText = T(recording ? "mount.performance.recording" : "mount.performance");
        if (_performance.Text != performanceText) _performance.SetText(performanceText);
        _performance.SetIcon(TrayIcons.Get("chart", recording && _performance.IsEnabled() ? "#dc2626" : "#526786"));
        _performance.SetToolTip(T(recording ? "record.active.tip" : "mount.performance.tip"));
    }

    private void ToggleSelected()
    {
        if (Selected is not { } m) return;
        // Mounting and unmounting are the supervisor's: it is the one that runs an instance's
        // process. They are also what changes which instances have one, so the addresses the front
        // end holds are asked for again afterwards rather than waited out.
        if (!m.Enabled)
        {
            // There is no process to start, so the first thing to do is put the instance back in
            // the configuration — which starts it in the same step, and is what the button the
            // reader just pressed said it would do. Nothing to confirm: this is the way back.
            Run(() => Lifecycle(SupervisorClient.Default.SetEnabled(m.Name, true)), "mount.done");
            return;
        }
        if (m.Mounted)
        {
            if (!Confirm("unmount", I18n.F("confirm.unmount", m.Name, m.MountPoint))) return;
            Run(() => Lifecycle(SupervisorClient.Default.Unmount(m.Name)), "unmount.done");
        }
        else Run(() => Lifecycle(SupervisorClient.Default.Mount(m.Name)), "mount.done");
    }

    /// <summary>Switches the instance off in the configuration rather than stopping it. What it
    /// stops is the service starting it again, which is the whole difference between this and the
    /// unmount button beside it: that one stops the instance now and leaves the configuration
    /// saying it should run.
    ///
    /// It is confirmed because the effect outlives the session. An instance switched off stays off
    /// across a restart of the service, and nobody watching a mount go away expects that of it
    /// unless they were told.</summary>
    private void ToggleEnabled()
    {
        if (Selected is not { Enabled: true } m) return;
        if (!Confirm("disable", I18n.F("confirm.disable", m.Name))) return;
        Run(() => Lifecycle(SupervisorClient.Default.SetEnabled(m.Name, false)), "disable.done");
    }

    /// <summary>Drops what the instance's cacheable layers hold, so the next read goes back to the
    /// source. The same call, the same confirmation and the same name inside it as the
    /// configuration editor's button: what is cleared is the instance that is running, wherever the
    /// reader asked for it from.</summary>
    private void ClearSelectedCache()
    {
        if (Selected is not { Mounted: true } m || _pending != null) return;
        long bytes = m.Providers.Where(p => p.StorageUsage is { CacheCapacity: > 0 })
            .Sum(p => p.StorageUsage!.CacheBytes);
        if (!Confirm("cache", I18n.F("confirm.cache", m.Name, FormatBytes(bytes)))) return;
        var client = InstanceDirectory.ClientFor(m.Name);
        Run(() => client.ClearCache(), "cache.done");
    }

    /// <summary>An instance's process is the supervisor's to start and to stop, and doing either
    /// changes which instances have one.</summary>
    private static ControlResponse Lifecycle(ControlResponse response)
    {
        if (response.Ok) InstanceDirectory.Invalidate();
        return response;
    }

    private void OpenSelected()
    {
        if (Selected is not { Mounted: true } m || !_connected || (_pending != null && !_quietPending)) return;
        try { Process.Start(new ProcessStartInfo(m.MountPoint) { UseShellExecute = true }); }
        catch (Exception ex) { Feedback(T("folder.failed") + " " + ex.Message, true); }
    }

    internal ConfigEditor? OpenEditor(string? name = null, bool create = false)
    {
        if (!create && name == null) return null;
        var editor = (ConfigEditor)_tabs.Open(create ? "editor:new" : "editor:" + name,
            () => new ConfigEditor(name, create),
            () => create ? T("mount.new") : I18n.F("editor.instance", name!));
        editor.Saved -= OnConfigSaved;
        editor.Saved += OnConfigSaved;
        return editor;
    }

    private void OpenPerformance()
    {
        if (Selected is not { } m) return;
        OpenPerformance(m.Name);
    }

    internal StatsWindow OpenPerformance(string name) => (StatsWindow)_tabs.Open("stats:" + name,
        () => new StatsWindow(name), () => I18n.F("stats.instance", name));


    internal void SmokeTabs(string statsInstance) =>
        _tabs.SmokeInteractions("editor:L10", "stats:" + statsInstance);

    private void OnConfigSaved() => Refresh();

    private void RunService(bool start)
    {
        if (_pending != null)
        {
            if (_quietPending) { _queued = () => RunService(start); _quietPending = false; UpdateActions(); }
            return;
        }
        if (!start && !Confirm("service.stop", T("confirm.service"))) return;
        Feedback(T("working"));
        _quietPending = false;
        _pending = Task.Run(() =>
        {
            try
            {
                string message = start ? ServiceControl.Start() : ServiceControl.Stop();
                return new Result(null, message, null, false, true);
            }
            catch (Exception ex) { return new Result(null, null, ex.Message, false, true); }
        });
        UpdateActions();
    }

    private bool Confirm(string titleKey, string message) => QMessageBox.Question(this, T(titleKey), message,
        T("confirm"), T("cancel"), null, 1, 1) == 0;

    private void Feedback(string text, bool error = false)
    {
        _feedback.SetText(text);
        _feedback.SetStyleSheet(error
            ? "background: #fff1f2; color: #b42335; border: 1px solid #fecdd3; border-radius: 8px; padding: 10px 14px;"
            : "background: #eef4ff; color: #315ca8; border: 1px solid #dae6fc; border-radius: 8px; padding: 10px 14px;");
    }

    private QTableWidgetItem Cell(int row, int col, string text)
    {
        var item = _table.Item(row, col);
        if (item == null) { item = new QTableWidgetItem(text); _table.SetItem(row, col, item); }
        else if (item.Text() != text) item.SetText(text);
        return item;
    }
    private static void SetText(QLabel label, string text) { if (label.Text != text) label.SetText(text); }
    private static string StateText(MountStatus m) => T(!string.IsNullOrWhiteSpace(m.Error) ? "state.error" : m.Mounted ? "state.mounted" : !m.Enabled ? "state.disabled" : "state.unmounted");
    private static string StateColor(MountStatus m) => !string.IsNullOrWhiteSpace(m.Error) ? "#dc2626" : m.Mounted ? "#059669" : "#64748b";

    private static QWidget Panel(string name)
    {
        var widget = new QWidget();
        widget.SetObjectName(name);
        widget.SetAttribute(Qt.WidgetAttribute.WA_StyledBackground);
        return widget;
    }

    private static QLabel Label(string key, string name)
    {
        var label = PlainLabel("", name);
        I18n.Bind(label.SetText, key);
        return label;
    }

    private static QLabel PlainLabel(string text, string name)
    {
        var label = new QLabel(text);
        label.SetObjectName(name);
        label.SetWordWrap(true);
        label.SetTextFormat(Qt.TextFormat.PlainText);
        return label;
    }

    private static QPushButton Button(string key, Action action, string name = "", string? icon = null)
    {
        var button = new QPushButton();
        I18n.Bind(button.SetText, key);
        button.SetObjectName(name);
        button.SetCursor(new QCursor(Qt.CursorShape.PointingHandCursor));
        if (icon != null) button.SetIcon(TrayIcons.Get(icon, name == "primary" ? "#ffffff" : name == "navButton" ? "#c8d4e6" : "#526786"));
        button.Clicked += _ => action();
        return button;
    }

    private static QLabel Metric(QHBoxLayout row, string title, string hint, string icon)
    {
        var card = Panel("surface");
        var layout = new QVBoxLayout(card);
        layout.SetContentsMargins(16, 15, 16, 15);
        layout.SetSpacing(6);
        var header = new QHBoxLayout();
        header.AddWidget(Label(title, "muted"));
        header.AddStretch(1);
        var image = new QLabel();
        image.SetPixmap(TrayIcons.Get(icon, "#7890b2").Pixmap(20, 20));
        header.AddWidget(image);
        layout.AddLayout(header);
        var number = PlainLabel("—", "metricValue");
        layout.AddWidget(number);
        layout.AddWidget(Label(hint, "metricHint"));
        row.AddWidget(card, 1);
        return number;
    }

    private static QLabel DetailField(QVBoxLayout layout, string title)
    {
        layout.AddWidget(Label(title, "eyebrow"));
        var value = PlainLabel("", "detailValue");
        value.SetTextInteractionFlags(Qt.TextInteractionFlags.TextSelectableByMouse);
        layout.AddWidget(value);
        return value;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString("0.#", I18n.Culture) + " " + units[unit];
    }
}
