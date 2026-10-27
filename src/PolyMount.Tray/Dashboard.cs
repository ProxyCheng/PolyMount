using System.Diagnostics;
using System.Globalization;
using PolyMount.Core.Ipc;
using QtSharp.QtCore;
using QtSharp.QtGui;
using QtSharp.QtWidgets;

namespace PolyMount.Tray;

/// <summary>Mount workspace. The Qt timer delivers background IPC results on the UI thread.</summary>
internal sealed class Dashboard : QWidget
{
    private readonly QTableWidget _table;
    private readonly QLineEdit _search;
    private readonly QComboBox _filter, _language, _theme;
    private readonly QLabel _service, _total, _active, _issues, _layerTotal;
    private readonly QLabel _listCount, _empty, _feedback, _updated;
    private readonly QLabel _detailName, _detailState, _mountPath, _layers, _warmup, _detailError;
    private readonly QWidget _detailBody;
    /// <summary>The body's scroll area, kept so that it can go away with the body: a scroll area
    /// whose widget is hidden still draws a scrollbar for it, which is an empty panel offering to
    /// scroll nothing.</summary>
    private readonly QScrollArea _detailScroll;
    /// <summary>The panel the selected instance's details are in, kept because a smoke run
    /// photographs it: it is the part of the workspace a reader acts on, and in a workspace it is
    /// usually covered by whichever tab was opened over it.</summary>
    private readonly QWidget _detailPanel;
    /// <summary>The parts a smoke run measures to check that a short window gives its height to the
    /// panels rather than to a scrollbar around the whole page: the page's scroll area, the splitter
    /// the two panels are in, the overview page itself, and the sidebar with the column inside it
    /// (whose own height is the reason the last two are measured apart).</summary>
    private readonly QScrollArea _overviewScroll;
    private readonly QSplitter _workspace;
    private readonly QWidget _overviewPage;
    private readonly QWidget _sidebar, _sidebarBody;
    /// <summary>The workbench half of the shared page; the plugin manager is the other.</summary>
    private readonly QWidget _workbench;
    private readonly QPushButton _refresh, _start, _stop, _create;
    private readonly QPushButton _navOverview, _navPlugins;
    private readonly PluginManager _plugins;
    private readonly QPushButton _toggleMount, _open, _disable, _clearCache, _edit, _performance, _inspect;
    private readonly QTimer _timer;
    private readonly BusyLine _busy;
    private readonly List<MountStatus> _mounts = new();
    private List<MountStatus> _visible = new();
    private Task<Result>? _pending;
    private bool _connected, _filling, _wasVisible;
    /// <summary>What the last status said the supervisor itself was doing. It goes on answering
    /// while it takes its instances down, so "reachable" alone would call a stopping service
    /// connected.</summary>
    private ServiceState _serviceState = ServiceState.Running;
    private string? _selectedName;
    /// <summary>The destination the side navigation is showing as current. The tab workspace
    /// reports the page it activates; this is what the two entries are painted from.</summary>
    private string _navKey = "overview";
    private DateTime _nextRefresh;
    private readonly TabWorkspace _tabs;
    private bool _quietPending;
    /// <summary>A command went out and the state that proves it took effect has not come back yet.
    ///
    /// A command's own answer is not that state: "disabled" says the configuration was written, not
    /// that the instance is down, and the refresh that follows is what turns it into what the
    /// buttons and the panel show. Until it arrives the buttons stay flat, or a second press sends
    /// the same command again — which is how "disable this instance" could be asked for over and
    /// over, each press opening its own confirmation.</summary>
    private bool _settle;
    /// <summary>For a service command, the state that has to come back before <see cref="_settle"/>
    /// may be cleared: true = reachable, false = gone. Null for every other command, which any
    /// status settles.
    ///
    /// Stopping is the case where a status is not the state yet. The supervisor replies to a stop
    /// and then takes its workers down — each is given ten seconds to unmount — and it goes on
    /// answering "status" the whole time. A reply that still finds it reachable is therefore the
    /// state before the command took effect, and clearing the wait on it put the stop button back
    /// while the service was still there, with the list still saying so until the next status ten
    /// seconds later.</summary>
    private bool? _expectService;
    /// <summary>When to stop waiting for that state and offer the buttons again. A command that was
    /// never going to take effect must not leave the window flat forever.</summary>
    private DateTime _expectServiceUntil;
    /// <summary>How long a service command may take to show its state. Starting is one process
    /// coming up; stopping is that process letting every worker unmount first, each of which is
    /// allowed ten seconds before it is killed.</summary>
    private static readonly TimeSpan StartWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(45);
    private Action? _queued;
    /// <summary>Whether the window has been told it may go: the answer to the pages it asked, kept
    /// so that closing it again does not ask them twice.</summary>
    private bool _allowClose;
    // Connection says this result was asked for to find out whether the service can be reached,
    // so a refusal means it cannot: the merged status answers "unreachable" as a response of its
    // own rather than throwing, and without this a window kept offering actions that could only
    // fail ("Access to the path is denied." under "Operation failed").
    private sealed record Result(ControlResponse? Response, string? Message, string? Error, bool Quiet,
        bool ServiceAction = false, bool Connection = false);
    /// <summary>The theme picker's items, in the order the modes are numbered: its index is the mode
    /// the reader chose, which is why they are read by position and re-lettered where they stand
    /// rather than added again when the language changes.</summary>
    private static readonly string[] ThemeKeys = { "theme.system", "theme.light", "theme.dark" };
    private static string T(string key) => I18n.T(key);

    public Dashboard()
    {
        SetObjectName("dashboard");
        SetWindowIcon(TrayIcons.App);
        Resize(1280, 820);
        
        I18n.Bind(SetWindowTitle, "dashboard.title");
        SetMinimumSize(480, 360);
        var shell = new QVBoxLayout(this);
        shell.SetContentsMargins(0, 0, 0, 0);
        _tabs = new TabWorkspace(this);
        shell.AddWidget(_tabs);
        var overviewPage = new QWidget();
        _tabs.Open("overview", () => overviewPage, () => T("overview"), pinned: true);
        var root = new QHBoxLayout(WindowScrollArea.Create(overviewPage, 1080, out _overviewScroll));
        _overviewPage = overviewPage;
        overviewPage.SetMinimumSize(0, 0);
        overviewPage.SetSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Ignored);
        root.SetContentsMargins(0, 0, 0, 0);
        root.SetSpacing(0);

        // The sidebar scrolls inside itself rather than being part of what the page needs room
        // for. What is in it is a column of controls whose own height comes to more than the
        // shortest window the workspace allows, and the page's minimum height is the largest thing
        // it holds — so leaving the sidebar as ordinary content made every window shorter than the
        // sidebar show a scrollbar for the whole workspace, with the instance panels pushed out of
        // sight behind it. Fixed width, own scroll: the panels get the height, and only a sidebar
        // too short for its own controls scrolls.
        var sidebar = Panel("sidebar");
        _sidebar = sidebar;
        sidebar.SetFixedWidth(204);
        var sidebarScroll = new QScrollArea(sidebar);
        sidebarScroll.SetObjectName("sidebarScroll");
        sidebarScroll.SetWidgetResizable(true);
        sidebarScroll.SetFrameShape(QFrame.Shape.NoFrame);
        sidebarScroll.SetHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff);
        var sidebarBody = new QWidget();
        sidebarBody.SetObjectName("sidebarBody");
        _sidebarBody = sidebarBody;
        var nav = new QVBoxLayout(sidebarBody);
        nav.SetContentsMargins(16, 20, 16, 16);
        nav.SetSpacing(10);
        var brand = new QLabel();
        brand.SetPixmap(TrayIcons.BrandPixmap);
        nav.AddWidget(brand);
        nav.AddWidget(Label("brand.subtitle", "sidebarMuted"));
        nav.AddSpacing(16);
        nav.AddWidget(Label("workspace", "sidebarMuted"));
        // Two destinations, side by side as peers: the mounts and the capabilities they are built
        // from. The plugins used to be a button in the heading, beside refresh and "new instance" —
        // an action among actions, although it opens a place rather than doing something.
        _navOverview = NavItem("overview", "drive", ShowOverview);
        _navPlugins = NavItem("plugins.button", "plugin", ShowPlugins);
        nav.AddWidget(_navOverview);
        nav.AddWidget(_navPlugins);
        UpdateNav("overview");
        nav.AddStretch(1);
        nav.AddWidget(Label("service.connection", "sidebarMuted"));
        _service = PlainLabel("", "serviceBadge");
        nav.AddWidget(_service);
        _start = Button("service.start", () => RunService(true), "navButton", "play");
        _stop = Button("service.stop", () => RunService(false), "navButton", "stop");
        nav.AddWidget(_start);
        nav.AddWidget(_stop);
        nav.AddSpacing(10);
        _language = ContinuousScrolling.Combo();
        // A language picker names each language in its own language, so both entries read
        // the same whichever catalog is active; the platform supplies the endonym instead
        // of writing it into the code, where only the catalogs belong.
        _language.AddItem(CultureInfo.GetCultureInfo("zh").NativeName);
        _language.AddItem(CultureInfo.GetCultureInfo("en").NativeName);
        _language.SetCurrentIndex(I18n.Language == "zh-CN" ? 0 : 1);
        _language.CurrentIndexChanged += i => I18n.SetLanguage(i == 0 ? "zh-CN" : "en");
        _theme = ContinuousScrolling.Combo();
        // Named in the reader's language, unlike the language picker: a scheme is not something
        // that names itself, and "system" has to say what it is following.
        foreach (string key in ThemeKeys) _theme.AddItem(I18n.T(key));
        _theme.SetCurrentIndex((int)TrayTheme.Mode);
        _theme.CurrentIndexChanged += index => TrayTheme.SetMode((TrayTheme.ThemeMode)index);
        // One to a row, each as wide as the sidebar is. Side by side they had seventy-nine pixels
        // each, of which the arrow and the padding keep forty-six, and what a picker has to show is
        // its longest item — "跟随系统", "Follow system" — at fifty-two and eighty-five: it drew the
        // name as far as it fitted and left the last character standing in half, which a combo box
        // does without saying anything, having no ellipsis. A row of its own is what makes them
        // readable, at about thirty pixels of the sidebar's height — which the sidebar now pays for
        // by scrolling itself rather than by making the whole page taller.
        foreach (var (key, combo) in new (string Key, QComboBox Combo)[] { ("language", _language), ("theme", _theme) })
        {
            var column = new QVBoxLayout();
            column.SetSpacing(4);
            column.AddWidget(Label(key, "sidebarMuted"));
            column.AddWidget(combo);
            nav.AddLayout(column);
        }
        nav.AddWidget(Label("tray.keepRunning", "sidebarMuted"));
        sidebarScroll.SetWidget(sidebarBody);
        var sidebarFrame = new QVBoxLayout(sidebar);
        sidebarFrame.SetContentsMargins(0, 0, 0, 0);
        sidebarFrame.AddWidget(sidebarScroll);
        root.AddWidget(sidebar);

        // The right-hand side is the section the sidebar selects: the workbench and the plugin
        // manager are two views of one page, not two pages. Both stay built; one is hidden.
        _workbench = new QWidget();
        var content = new QVBoxLayout(_workbench);
        content.SetContentsMargins(24, 20, 24, 14);
        // Twelve rather than eighteen between the heading, the metrics, the panels and the two
        // status lines: those gaps are part of the page's minimum height, and the panels are what a
        // reader came for. The panels still get every pixel the window has left over — this only
        // decides how small a window can get before the page has to scroll.
        content.SetSpacing(12);
        root.AddWidget(_workbench, 1);
        _plugins = new PluginManager();
        _plugins.Hide();
        root.AddWidget(_plugins, 1);
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
        _create = Button("mount.new", NewMount, "primary", "plus");
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
        _workspace = workspace;
        workspace.SetHandleWidth(14);
        workspace.SetChildrenCollapsible(false);
        var listPanel = Panel("surface");
        var list = new QVBoxLayout(listPanel);
        list.SetContentsMargins(16, 14, 16, 12);
        list.SetSpacing(10);
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
        // The table goes away while there is nothing in it, and the hint takes the stretch it
        // would have had: an empty table is a frame over nothing, and a hint laid out under it
        // reads as a footnote to a list rather than as the state of the list.
        list.AddWidget(_empty, 1);
        list.AddWidget(Label("mount.list.hint", "muted"));
        workspace.AddWidget(listPanel);

        _detailPanel = Panel("surface");
        var details = _detailPanel;
        details.SetMinimumWidth(306);
        details.SetMaximumWidth(390);
        var detail = new QVBoxLayout(details);
        detail.SetContentsMargins(16, 16, 16, 14);
        detail.SetSpacing(12);
        detail.AddWidget(Label("detail", "eyebrow"));
        _detailName = PlainLabel("", "detailTitle");
        detail.AddWidget(_detailName);
        _detailState = PlainLabel("", "muted");
        detail.AddWidget(_detailState);
        var scroll = new QScrollArea();
        _detailScroll = scroll;
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
        // The error is above the switch, not at the bottom of the panel: it is the reason a
        // reader opens the instance at all, and "mount" is the action it is about. The panel
        // scrolls, and the one line that explains a failed mount must not be what is below the
        // fold.
        _detailError = PlainLabel("", "errorBox");
        body.AddWidget(_detailError);
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
        body.AddStretch(1);
        scroll.SetWidget(_detailBody);
        detail.AddWidget(scroll, 1);
        // With no instance selected the body is hidden, and then the three labels above are the
        // only items left: without one that may absorb the panel's height they take it in turns,
        // and "choose an instance" reads as stretched down the whole panel. The spacer receives
        // that space while the body is away; the scroll area's stretch receives it when it is not.
        detail.AddStretch(0);
        workspace.AddWidget(details);
        workspace.SetStretchFactor(0, 3);
        workspace.SetStretchFactor(1, 2);
        content.AddWidget(workspace, 1);
        _feedback = PlainLabel(T("connecting"), "feedback");
        content.AddWidget(_feedback);
        // The line that says the window is waiting for the service: under the message that says
        // what it is waiting for, and above the footer's own two lines.
        _busy = new BusyLine();
        content.AddWidget(_busy);
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
        TrayTheme.OnChanged(Retheme, this);
    }

    /// <summary>A scheme change repaints most of this window by itself: labels and tables ask the
    /// theme what colour they are when they are painted. The icons do not — they are pictures drawn
    /// with a colour baked in — so the actions are put together again with them.</summary>
    private void Retheme()
    {
        UpdateOverview();
        FillTable();
        UpdateActions();
        UpdateConnection();
        UpdateNav(_navKey);
        Update();
    }

    /// <summary>One side-navigation entry. The active state is an inline style rather than an
    /// object name because it changes at runtime and Qt only repaints an object-name change after
    /// an unpolish/polish cycle — a style sheet is the one thing a widget can take without help.</summary>
    private static QPushButton NavItem(string key, string icon, Action action)
    {
        var button = new QPushButton();
        I18n.Bind(button.SetText, key);
        button.SetCursor(new QCursor(Qt.CursorShape.PointingHandCursor));
        button.SetIcon(TrayIcons.Get(icon, TrayTheme.Tokens.SidebarText));
        button.Clicked += _ => action();
        return button;
    }

    private void UpdateNav(string key)
    {
        _navKey = key;
        // Pages opened from the workspace (an instance's settings, its performance) are still the
        // overview's work; only the plugin manager is a destination of its own.
        bool plugins = string.Equals(key, "plugins", StringComparison.Ordinal);
        StyleNav(_navOverview, !plugins, "drive");
        StyleNav(_navPlugins, plugins, "plugin");
    }

    private static void StyleNav(QPushButton button, bool active, string icon)
    {
        var palette = TrayTheme.Tokens;
        string color = active ? palette.SidebarText : palette.SidebarMuted;
        button.SetIcon(TrayIcons.Get(icon, color));
        string style = active
            ? $"QPushButton {{ background: {palette.SidebarActive}; border: none; border-radius: 8px; color: {palette.SidebarText}; text-align: left; padding: 10px 12px; font-weight: 600; }}"
            : $"QPushButton {{ background: transparent; border: none; border-radius: 8px; color: {palette.SidebarMuted}; text-align: left; padding: 10px 12px; }}"
              + $" QPushButton:hover {{ background: {palette.SidebarActive}; color: {palette.SidebarText}; }}";
        if (button.StyleSheet != style) button.SetStyleSheet(style);
    }

    /// <summary>Closing the window closes it. It used to hide itself and stay, which is what a tray
    /// application usually does — the icon is the application and the window is one of its views —
    /// but what that meant here is that the pages went on sampling, recording and holding drafts
    /// while nobody was looking at them, and a reader who had just closed the window had every
    /// reason to think they had stopped. So the window goes, and the tray icon is what keeps the
    /// tray alive.
    ///
    /// What a page would be sorry to lose is asked first — the question a tab close asks, put to
    /// each page in turn — and a page that would rather stay keeps the whole window open. A window
    /// whose pages have nothing to lose is closed without a word, which is most of them.</summary>
    public override void CloseEvent(QCloseEvent e)
    {
        if (_allowClose) { e.Accept(); return; }
        e.Ignore();
        _tabs.RequestCloseAll(() =>
        {
            _tabs.ShutDown();
            _allowClose = true;
            // Hidden and deleted rather than closed. This runs inside the close event the window is
            // already in — a question a page asks is answered inside it too — and Qt refuses a close
            // that re-enters one: the widget is already closing, so a nested Close() here returns
            // saying it worked and does nothing at all. Hiding and deleting is what an accepted
            // close amounts to, and it cannot be swallowed that way.
            Hide();
            DeleteLater();
        });
    }

    public void ShowWindow()
    {
        Show();
        Raise();
        ActivateWindow();
        if (_pending == null) Refresh(quiet: true);
    }

    internal void Capture(string path) { using var image = Grab(); image.Save(path); }

    /// <summary>Self-check for what a short window does with the workspace: the instance list and
    /// the detail panel take the height that is left, and the page around them scrolls only once
    /// they are at their own minimum and cannot give up any more.
    ///
    /// This is what a short window used to get wrong, in two ways. The page asked for the height
    /// its wrapped labels would like rather than what it needed, so it was laid out taller than the
    /// window with blank space to spare and scrolled for the difference; and what it held at its
    /// tallest was the sidebar — a fixed-width column of controls whose own height comes to more
    /// than a laptop's window has to give. The page now asks for its minimum (see the page surface
    /// in <see cref="WindowScrollArea"/>), and the sidebar scrolls inside itself rather than making
    /// the page taller. What the check does is measure both, then ask for a window with exactly
    /// what the page needs and one with a little less.</summary>
    internal void VerifyPanelsTakeTheHeight()
    {
        _tabs.Open("overview", () => _overviewPage, () => T("overview"), pinned: true);
        Resize(1280, 820);
        QCoreApplication.ProcessEvents();
        int sidebarMinimum = _sidebar.MinimumSizeHint.Height();
        int sidebarContent = _sidebarBody.MinimumSizeHint.Height();
        if (sidebarMinimum > 100)
            throw new InvalidOperationException(
                $"The sidebar still asks the page for {sidebarMinimum} px of height instead of scrolling inside itself");
        if (sidebarContent < sidebarMinimum * 3)
            throw new InvalidOperationException(
                $"The sidebar's own column is only {sidebarContent} px tall, so this check would pass without measuring anything");
        int roomy = _workspace.Height;
        if (roomy <= _workspace.MinimumSizeHint.Height())
            throw new InvalidOperationException(
                $"The panels stopped at their minimum ({_workspace.MinimumSizeHint.Height()} px) in a window with {roomy} px to give them");
        if (_overviewScroll.VerticalScrollBar().IsVisible())
            throw new InvalidOperationException(
                $"A window with room to spare scrolled the whole page: {Width}x{Height}, viewport "
                + $"{_overviewScroll.Viewport().Width}x{_overviewScroll.Viewport().Height}, page "
                + $"{_overviewScroll.Widget().Width}x{_overviewScroll.Widget().Height}");
        // What the page needs at least, and how much of the window is not the page (the tabs above
        // it). The window is then asked for exactly that much and a little less.
        int needed = _overviewScroll.Widget().MinimumSizeHint.Height();
        int overhead = Height - _overviewScroll.Viewport().Height;
        Console.WriteLine($"Workspace: the page needs {needed} px; the panels take every pixel above that.");
        Resize(1280, needed + overhead + 40);
        QCoreApplication.ProcessEvents();
        if (_overviewScroll.VerticalScrollBar().IsVisible())
            throw new InvalidOperationException(
                $"A window 40 px taller than what the page needs ({needed} px) still scrolled the whole page");
        Resize(1280, needed + overhead - 80);
        QCoreApplication.ProcessEvents();
        if (!_overviewScroll.VerticalScrollBar().IsVisible())
            throw new InvalidOperationException(
                $"A window shorter than what the page needs ({needed} px) did not scroll: the panels were clipped instead of scrolled to");
        Resize(1280, 820);
        QCoreApplication.ProcessEvents();
        // The waiting line is the window's answer to a click that went out to another process:
        // hidden at rest, moving while a request is out.
        if (_busy.IsVisible())
            throw new InvalidOperationException("The waiting line was drawn with nothing to wait for");
        _busy.SetBusy(true);
        if (!_busy.IsVisible())
            throw new InvalidOperationException("The waiting line did not appear while a request was out");
        _busy.SetBusy(false);
        if (_busy.IsVisible())
            throw new InvalidOperationException("The waiting line stayed after the request was answered");
    }

    /// <summary>Self-check for the buttons a reader presses: while a command is out — and, which
    /// is the part that was missing, while the status that proves it took effect is still on its
    /// way — none of them can be pressed again. A command's answer says nothing about the
    /// instance's state, so the window used to put its buttons back with the state it had before
    /// the command, and a second press re-sent the same thing: "disable this instance" could be
    /// asked for over and over, each press opening its own confirmation.</summary>
    internal void VerifyActionsWaitForTheState()
    {
        if (!_connected || _pending != null) return;
        // The instance's half needs an instance to be selected; the service's half is about the
        // window's own waiting and holds with an empty list.
        if (Selected is { })
        {
            _settle = true;
            UpdateActions();
            if (_toggleMount.IsEnabled())
                throw new InvalidOperationException("The instance's button was live while the state it changed was still on its way");
            if (!_busy.IsVisible())
                throw new InvalidOperationException("The window was waiting for that state without saying so");
            _settle = false;
            UpdateActions();
            if (!_toggleMount.IsEnabled())
                throw new InvalidOperationException("The instance's button stayed flat after the state had arrived");
            Console.WriteLine("Actions: the buttons wait for the state a command changed.");
        }
        else Console.WriteLine("Actions: no instance is selected, so only the service's own wait is checked.");
        // The state a service command waits for is not "any status": a stop is answered by the
        // supervisor before it takes its workers down, so a status that still finds the service
        // reachable is the state from before the command. Only one that finds it gone settles the
        // window — and the deadline hands the buttons back if that status never comes.
        bool wasConnected = _connected;
        try
        {
            _expectService = false;
            _expectServiceUntil = DateTime.UtcNow + StopWait;
            if (!StillWaiting(DateTime.UtcNow))
                throw new InvalidOperationException("A stop settled on a status that still found the service answering");
            _connected = false;
            if (StillWaiting(DateTime.UtcNow))
                throw new InvalidOperationException("A stop the status found gone was still being waited on");
            _connected = wasConnected;
            _expectService = true;
            _expectServiceUntil = DateTime.UtcNow.AddSeconds(-1);
            if (StillWaiting(DateTime.UtcNow))
                throw new InvalidOperationException("A service command kept the buttons flat past its deadline");
        }
        finally { _connected = wasConnected; _expectService = null; }
    }

    /// <summary>Self-check for closing the dashboard, in two steps because the interesting half of
    /// it is asynchronous: closing a window makes it disappear now and deletes it when the event
    /// loop gets its turn, so the window this one asks about is kept between the two calls.
    ///
    /// What is checked is that it goes rather than hiding itself: a window that had only hidden
    /// would pass the first step and fail the second, which is exactly the difference between the
    /// behaviour this replaced and the behaviour it wants. The pages it holds are what is asked
    /// before it may go, and a page with something to lose asks a reader a question — which a smoke
    /// run has nobody to answer, so this window keeps none: what that path does is what the pages'
    /// own answers do, which their own steps check.</summary>
    private static Dashboard? _closing;
    private static bool _closingDestroyed;

    internal static void CloseWindowForCheck()
    {
        var window = new Dashboard();
        _closing = window;
        _closingDestroyed = false;
        // Whether it was destroyed is remembered as a flag rather than asked of the field: a
        // destroyed widget reads as null (the binding's own `==`, so that `w != null` is the guard
        // against using one), which makes `_closing == null` true for the very window being asked
        // about. `is null` is the reference test — what a wrapper is, not what it holds.
        window.Destroyed += _ => _closingDestroyed = true;
        window.ShowWindow();
        if (!window.IsVisible())
            throw new InvalidOperationException("A dashboard that was just opened is not on screen");
        // A page that would rather stay keeps the window open — asked first, and one page at a time.
        var stubborn = new CheckPage { Refuses = true };
        window._tabs.Open("check-window", () => stubborn, () => "check-window");
        window.Close();
        if (!window.IsVisible())
            throw new InvalidOperationException("A page that asked to stay was closed anyway with the window it was in");
        // And once it agrees, the window goes.
        stubborn.Refuses = false;
        window.Close();
        if (window.IsVisible())
            throw new InvalidOperationException("Closing the dashboard left it on screen");
    }

    internal static void VerifyWindowClosed()
    {
        if (_closing is null) throw new InvalidOperationException("No dashboard was closed to check");
        if (!_closingDestroyed)
            throw new InvalidOperationException("The dashboard hid itself rather than closing: it is still here");
        _closing = null;
    }

    /// <summary>Self-check for the sidebar's two pickers: each is at least as wide as the widest
    /// thing it can show, in either language. A picker narrower than its own items draws the name as
    /// far as it fits and stops — a combo box has no ellipsis, so what a reader sees is a word with
    /// its last character cut in half, and nothing anywhere says so.</summary>
    internal void VerifyPickers()
    {
        string original = I18n.Language;
        try
        {
            foreach (string language in new[] { "zh-CN", "en" })
            {
                I18n.SetLanguage(language);
                // The labels follow the language through a binding and the picker's items through
                // this window's own answer to the change, which is an event: both have to have
                // arrived before either can be measured.
                QCoreApplication.ProcessEvents();
                foreach (var (name, combo) in new (string Name, QComboBox Combo)[] { ("language", _language), ("theme", _theme) })
                {
                    int wanted = combo.SizeHint.Width();
                    if (combo.Width < wanted)
                        throw new InvalidOperationException(
                            $"The {name} picker is {combo.Width} px wide in {language} and its own items want {wanted} of them");
                }
            }
        }
        finally
        {
            I18n.SetLanguage(original);
            QCoreApplication.ProcessEvents();
        }
    }


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
    private void Refresh(bool quiet = false) => Run(InstanceView.Status, null, quiet, connection: true);

    private void Run(Func<ControlResponse> call, string? successKey, bool quiet = false, bool connection = false)
    {
        if (_pending != null)
        {
            if (_quietPending && !quiet)
            {
                _queued = () => Run(call, successKey, connection: connection);
                _quietPending = false;
                UpdateActions();
            }
            return;
        }
        _quietPending = quiet;
        if (!quiet) _settle = true;
        if (!quiet) Feedback(T(successKey == null ? "refreshing" : "working"));
        _pending = Task.Run(() =>
        {
            try
            {
                var response = call();
                return new Result(response, successKey, response.Ok ? null : response.Error ?? "Unknown error", quiet, Connection: connection);
            }
            catch (Exception ex) { return new Result(null, null, ex.Message, quiet, Connection: connection); }
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
                // A request that never went out is not one to wait on: whatever the service is
                // doing, it is not what this command would have changed.
                if (result.Error != null) { _settle = false; _expectService = null; }
                _nextRefresh = DateTime.UtcNow.AddSeconds(1);
            }
            else
            {
                // A status refresh that came back refused means the service cannot be reached:
                // the merged view reports an unreachable supervisor as a response of its own, so
                // "there is a response" is not the same question as "the service answered".
                _connected = result.Connection ? result.Response is { Ok: true } : result.Response != null;
                if (result.Response is { Ok: true } response)
                {
                    if (result.Connection) _serviceState = response.State;
                    _mounts.Clear();
                    _mounts.AddRange(response.Mounts);
                    UpdateOverview();
                    FillTable();
                    _updated.SetText(I18n.F("updated", DateTime.Now.ToString("HH:mm:ss")));
                    if (!result.Quiet) Feedback(T(result.Message ?? "refreshed"));
                    else if (_feedback.Text.StartsWith(T("connection.failed"), StringComparison.Ordinal)) Feedback(T("connected"));
                }
                else if (result.Error != null)
                    Feedback(T(result.Response == null || result.Connection ? "connection.failed" : "operation.failed") + " " + result.Error, true);
                if (!_connected)
                {
                    foreach (var value in new[] { _total, _active, _issues, _layerTotal }) value.SetText("—");
                    _updated.SetText(T("offline.retry"));
                    FillTable();
                }
                _nextRefresh = DateTime.UtcNow.AddSeconds(10);
            }
            // What the answer to a command proves, and what it does not: a status is the state
            // itself and settles the window; anything else leaves the buttons flat and asks for
            // the state at once, which is the refresh the reader would otherwise wait up to ten
            // seconds for with the old state still on screen.
            //
            // A service command is the one case where a status is not yet that state (see
            // _expectService), so it keeps the window waiting until a status says what was asked
            // for — and is asked again a second later rather than ten, so the wait ends when the
            // service does rather than when the slow poll comes round.
            if (result.Connection)
            {
                _settle = StillWaiting(DateTime.UtcNow);
                if (_settle) _nextRefresh = DateTime.UtcNow.AddSeconds(1);
            }
            else if (!result.Quiet) _nextRefresh = DateTime.UtcNow;
            UpdateConnection();
            UpdateActions();
            if (_queued is { } queued) { _queued = null; queued(); }
        }
        bool visible = IsVisible();
        if (visible && _pending == null && (!_wasVisible || DateTime.UtcNow >= _nextRefresh)) Refresh(quiet: true);
        _wasVisible = visible;
    }

    /// <summary>Whether a command is still waiting for the state it changed, now that a status has
    /// come back. Any status settles an instance's own command — the supervisor carries it out
    /// before it answers, so the next status is already the result. A service command is settled
    /// only by the reachability it asked for, and gives up at its deadline: a command that never
    /// takes effect must hand the buttons back rather than hold them flat.</summary>
    private bool StillWaiting(DateTime now)
    {
        if (_expectService is not { } reachable) return false;
        if (reachable != _connected && now < _expectServiceUntil) return true;
        _expectService = null;
        return false;
    }

    private void Translate()
    {
        UpdateFilters();
        // The theme picker is named in the reader's language as well, and its items were lettered
        // once, at the language the tray was started in: switching to English left the scheme
        // choices in Chinese beside a label that had changed. They are re-lettered where they
        // stand, because the index is the mode and re-adding them would move the reader's choice.
        for (int index = 0; index < _theme.Count && index < ThemeKeys.Length; index++)
            _theme.SetItemText(index, T(ThemeKeys[index]));
        _table.SetHorizontalHeaderLabels(new[] { T("column.instance"), T("state"), T("column.layers") });
        UpdateConnection();
        if (_connected) UpdateOverview();
        FillTable();
        // Through Feedback rather than SetText: the colour is part of the message, and this used
        // to set the words and leave whatever style was on the label — which is how a service that
        // had come back was still announcing itself in the red of the failure before it.
        Feedback(T(_connected ? "connected" : "connecting"));
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

    /// <summary>The service as the window shows it. Two of these four are the backend's own to
    /// say — running, and stopping while it takes its instances down — and two are the absence of
    /// an answer: a service that has not come up yet and one that has gone cannot report anything
    /// about themselves, so the window adds what it itself asked for.</summary>
    private enum ServiceView { Stopped, Starting, Running, Stopping }
    private ServiceView Service =>
        !_connected
            ? (_expectService == true ? ServiceView.Starting : ServiceView.Stopped)
            : _serviceState == ServiceState.Stopping || _expectService == false
                ? ServiceView.Stopping
                : ServiceView.Running;

    private void UpdateConnection()
    {
        var state = Service;
        string key = state switch
        {
            ServiceView.Starting => "service.starting",
            ServiceView.Stopping => "service.stopping",
            ServiceView.Running => "service.online",
            _ => "service.offline"
        };
        SetText(_service, "●  " + T(key));
        string style = state == ServiceView.Running ? "color: #6ee7b7;" : "color: #fbbf24;";
        if (_service.StyleSheet != style) _service.SetStyleSheet(style);
    }

    private void UpdateOverview()
    {
        SetText(_total, _mounts.Count.ToString());
        SetText(_active, _mounts.Count(m => m.Mounted).ToString());
        int issues = _mounts.Count(m => !string.IsNullOrWhiteSpace(m.Error));
        SetText(_issues, issues.ToString());
        string style = issues > 0 ? "color: " + TrayTheme.Tokens.DangerStrong + ";" : "color: " + TrayTheme.Tokens.Text + ";";
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
        _table.SetVisible(_visible.Count > 0);
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
        _detailScroll.SetVisible(m != null);
        SetText(_detailName, m?.Name ?? T("detail.select"));
        SetText(_detailState, m == null ? T("detail.hint") : (_connected ? StateText(m) : T("detail.offline")));
        string style = m == null || !_connected ? "color: " + TrayTheme.Tokens.Muted + ";" : $"color: {StateColor(m)};";
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
            SetText(_detailError, FailureText(m));
        }
        UpdateActions();
    }

    private void UpdateActions()
    {
        bool idle = (_pending == null || _quietPending) && !_settle;
        // An automatic refresh is not something a reader asked for and must not flash a waiting
        // line every ten seconds; a button press is.
        _busy.SetBusy(_settle || (_pending != null && !_quietPending));
        var m = Selected;
        bool ready = idle && _connected && m != null;
        _refresh.SetEnabled(idle);
        string refreshText = T(idle ? "refresh" : "working.short");
        if (_refresh.Text != refreshText) _refresh.SetText(refreshText);
        // A service that has said it is stopping is not one to ask again: the command is already
        // out and being carried out where nothing here can see its progress. Without this a
        // window opened during the shutdown (or one that never asked) offers "stop" for as long
        // as the supervisor is still answering — which is exactly the window this all began with.
        bool stopping = _connected && _serviceState == ServiceState.Stopping;
        _start.SetEnabled(idle && !_connected && !stopping);
        _stop.SetEnabled(idle && _connected && !stopping);
        _create.SetEnabled(idle && _connected && !stopping);
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
            _toggleMount.SetIcon(TrayIcons.Get(m is { Mounted: true, Enabled: true } ? "stop" : "play", TrayTheme.Tokens.OnPrimary));
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
        _performance.SetIcon(TrayIcons.Get("chart", recording && _performance.IsEnabled() ? TrayTheme.Tokens.DangerStrong : TrayTheme.Tokens.Icon));
        _performance.SetToolTip(T(recording ? "record.active.tip" : "mount.performance.tip"));
    }

    private void ToggleSelected()
    {
        // A second press while the first is still being carried out — or while the state it changed
        // is on its way back — would send the same command again. The button is drawn flat for
        // that window, and this is the same rule where the press lands.
        if (_settle || (_pending != null && !_quietPending) || Selected is not { } m) return;
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
        if (_settle || (_pending != null && !_quietPending) || Selected is not { Enabled: true } m) return;
        if (!Confirm("disable", I18n.F("confirm.disable", m.Name))) return;
        Run(() => Lifecycle(SupervisorClient.Default.SetEnabled(m.Name, false)), "disable.done");
    }

    /// <summary>Drops what the instance's cacheable layers hold, so the next read goes back to the
    /// source. The same call, the same confirmation and the same name inside it as the
    /// configuration editor's button: what is cleared is the instance that is running, wherever the
    /// reader asked for it from.</summary>
    private void ClearSelectedCache()
    {
        if (Selected is not { Mounted: true } m || _pending != null || _settle) return;
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
        // The supervisor calls a running worker "mounted"; a worker that has not got its mount
        // up yet — or one that has just gone away — leaves nothing at this path, and the raw
        // process error ("cannot find the file") does not say why.
        if (!Directory.Exists(m.MountPoint))
        {
            Feedback(T("folder.failed") + " " + T("state.unmounted") + " · " + m.MountPoint, true);
            return;
        }
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

    /// <summary>Creating an instance starts with what it mounts, not with an empty stack: the
    /// dialog offers the presets, then asks only for the values the chosen one cannot know. Its
    /// last card is the way back to the editor, for a chain that is built by hand. The instance is
    /// written by the dialog — it is the one holding the values, and a refusal belongs beside the
    /// field that caused it — so all that is left here is to show it.</summary>
    private void NewMount()
    {
        if (_settle || (_pending != null && !_quietPending) || !_connected || _serviceState == ServiceState.Stopping) return;
        var dialog = new NewMountDialog(this);
        try
        {
            if (dialog.Exec() != 1) return;
            if (dialog.Custom) { OpenEditor(create: true); return; }
            if (dialog.Created is not { Length: > 0 } created) return;
            _selectedName = created;
            Feedback(I18n.F("newmount.created", created));
            // The instance's own state is the next question, and it is answered by the service,
            // not by the save: ask once rather than wait out the ten-second poll, and hold the
            // buttons flat until that answer is in — the same rule every other command follows.
            _settle = true;
            if (_pending == null) Refresh(quiet: true);
            else _queued = () => Refresh(quiet: true);
            UpdateActions();
        }
        finally
        {
            dialog.Hide();
            dialog.DeleteLater();
        }
    }

    private void OpenPerformance()
    {
        if (Selected is not { } m) return;
        OpenPerformance(m.Name);
    }

    internal StatsWindow OpenPerformance(string name) => (StatsWindow)_tabs.Open("stats:" + name,
        () => new StatsWindow(name), () => I18n.F("stats.instance", name));

    /// <summary>The plugin manager view, for the smoke run: the page is part of the workbench and
    /// has no address of its own.</summary>
    internal PluginManager PluginPage => _plugins;

    /// <summary>Shows the mounts, and the workbench they are managed from.</summary>
    internal void ShowOverview()
    {
        _tabs.Open("overview", () => _overviewPage, () => T("overview"), pinned: true);
        ShowSection(plugins: false);
    }

    /// <summary>Shows the plugin manager in the same page. The sidebar is the switch: the tab,
    /// its bar and the window are the workbench either way.</summary>
    internal void ShowPlugins()
    {
        _tabs.Open("overview", () => _overviewPage, () => T("overview"), pinned: true);
        ShowSection(plugins: true);
    }

    private void ShowSection(bool plugins)
    {
        _workbench.SetVisible(!plugins);
        _plugins.SetVisible(plugins);
        UpdateNav(plugins ? "plugins" : "overview");
    }


    /// <summary>The tab interactions are driven against the instance the run is looking at, which is
    /// the live one when there is one: the editor's key carries its name, so a name written here
    /// that the run did not open is a key that is not there — which is what a machine with an
    /// instance other than the one this used to say hit.</summary>
    internal void SmokeTabs(string instance) =>
        _tabs.SmokeInteractions("editor:" + instance, "stats:" + instance, () => new StatsWindow(instance));

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
        // Starting or stopping the service changes what can be pressed next: the window waits for
        // a status that says whether it is reachable before it offers its buttons again — and, for
        // a stop, for one that says it is actually gone.
        _settle = true;
        _expectService = start;
        _expectServiceUntil = DateTime.UtcNow + (start ? StartWait : StopWait);
        UpdateConnection();
        _pending = Task.Run(() =>
        {
            try
            {
                var outcome = start ? ServiceControl.Start() : ServiceControl.Stop();
                return outcome.Ok
                    ? new Result(null, outcome.Message, null, false, ServiceAction: true)
                    : new Result(null, null, outcome.Message, false, ServiceAction: true);
            }
            catch (Exception ex) { return new Result(null, null, ex.Message, false, ServiceAction: true); }
        });
        UpdateActions();
    }

    private bool Confirm(string titleKey, string message) => QMessageBox.Question(this, T(titleKey), message,
        T("confirm"), T("cancel"), null, 1, 1) == 0;

    private void Feedback(string text, bool error = false)
    {
        // Every failure this window shows is written down as well: a status line is read once and
        // then replaced by the next thing, and "it failed yesterday" is a question only a log
        // answers. Successes are not: they are the normal state of the world.
        if (error) Serilog.Log.Warning("workspace: {Message}", text);
        _feedback.SetText(text);
        _feedback.SetStyleSheet(error
            ? $"background: {TrayTheme.Tokens.DangerSoft}; color: {TrayTheme.Tokens.Danger}; border: 1px solid {TrayTheme.Tokens.DangerBorder}; border-radius: 8px; padding: 6px 14px;"
            : $"background: {TrayTheme.Tokens.InfoSoft}; color: {TrayTheme.Tokens.Info}; border: 1px solid {TrayTheme.Tokens.InfoBorder}; border-radius: 8px; padding: 6px 14px;");
    }

    private QTableWidgetItem Cell(int row, int col, string text)
    {
        var item = _table.Item(row, col);
        if (item == null) { item = new QTableWidgetItem(text); _table.SetItem(row, col, item); }
        else if (item.Text() != text) item.SetText(text);
        return item;
    }
    private static void SetText(QLabel label, string text) { if (label.Text != text) label.SetText(text); }
    private static string StateText(MountStatus m) => T(m.Failed ? "state.failed"
        : !string.IsNullOrWhiteSpace(m.Error) ? "state.error" : m.Mounted ? "state.mounted" : !m.Enabled ? "state.disabled" : "state.unmounted");
    private static string StateColor(MountStatus m) => !string.IsNullOrWhiteSpace(m.Error) ? TrayTheme.Tokens.DangerStrong : m.Mounted ? TrayTheme.Tokens.Success : TrayTheme.Tokens.Muted;

    /// <summary>What the panel says about an instance that is not mounted: the reason whoever
    /// refused gave, then the action that answers it, chosen by kind. The reason on its own was all
    /// this showed, and "the credential may only be presented to …" is a sentence a reader cannot
    /// act on without knowing that the list it names is a field this product has.</summary>
    private static string FailureText(MountStatus m)
    {
        if (string.IsNullOrWhiteSpace(m.Error)) return "";
        var lines = new List<string> { T("instance.error"), m.Error };
        if (RemedyKey(m) is { } key) { lines.Add(""); lines.Add(T(key)); }
        // Said only once the supervisor has actually stopped: an instance it is still restarting
        // needs no button pressed, and a line telling the reader to press one would be wrong.
        if (m.Failed) lines.Add(T("instance.remedy.retry"));
        return string.Join("\n", lines);
    }

    private static string? RemedyKey(MountStatus m) => m.ErrorKind switch
    {
        FailureKinds.Auth => "instance.remedy.auth",
        FailureKinds.Origin => "instance.remedy.origin",
        _ => null
    };

    /// <summary>Self-check for what a failed instance says, against fixtures: the reason, the remedy
    /// for the kind it reported, and the retry line only when the supervisor has given up. It needs
    /// no service, which is the point — the states it asserts on cannot be produced on demand.</summary>
    internal static void VerifyFailureTexts()
    {
        var gaveUp = new MountStatus
        {
            Name = "check", Error = "the credential was entered for https://a", ErrorKind = FailureKinds.Origin, Failed = true
        };
        string text = FailureText(gaveUp);
        if (!text.Contains(gaveUp.Error, StringComparison.Ordinal))
            throw new InvalidOperationException("A failed instance does not show why: " + text);
        if (!text.Contains(T("instance.remedy.origin"), StringComparison.Ordinal))
            throw new InvalidOperationException("An instance refused on a credential's origins does not say how to allow the address: " + text);
        if (!text.Contains(T("instance.remedy.retry"), StringComparison.Ordinal))
            throw new InvalidOperationException("An instance the supervisor stopped restarting does not say so: " + text);
        if (StateText(gaveUp) != T("state.failed"))
            throw new InvalidOperationException("An instance that stopped being restarted is listed as '" + StateText(gaveUp) + "'");
        var retrying = new MountStatus { Name = "check", Error = "the store is in use", ErrorKind = FailureKinds.Startup };
        if (FailureText(retrying).Contains(T("instance.remedy.retry"), StringComparison.Ordinal))
            throw new InvalidOperationException("An instance that is still being restarted already says it gave up");
        if (StateText(retrying) != T("state.error"))
            throw new InvalidOperationException("A failed instance that is being restarted is listed as '" + StateText(retrying) + "'");
        var signed = new MountStatus { Name = "check", Error = "sign in", ErrorKind = FailureKinds.Auth };
        if (!FailureText(signed).Contains(T("instance.remedy.auth"), StringComparison.Ordinal))
            throw new InvalidOperationException("An instance that needs an account does not say where accounts are: " + FailureText(signed));
        if (FailureText(new MountStatus { Name = "check", Mounted = true }) != "")
            throw new InvalidOperationException("An instance with no failure has a failure paragraph");
    }

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
        if (icon != null) button.SetIcon(TrayIcons.Get(icon, name == "primary" ? TrayTheme.Tokens.OnPrimary : name == "navButton" ? TrayTheme.Tokens.SidebarText : TrayTheme.Tokens.Icon));
        button.Clicked += _ => action();
        return button;
    }

    private static QLabel Metric(QHBoxLayout row, string title, string hint, string icon)
    {
        var card = Panel("surface");
        var layout = new QVBoxLayout(card);
        // Ten of padding rather than fifteen, and a tighter gap: the card's own height is part of
        // what the page needs before its panels get any, and a metric card is three short lines.
        layout.SetContentsMargins(16, 10, 16, 10);
        layout.SetSpacing(4);
        var header = new QHBoxLayout();
        header.AddWidget(Label(title, "muted"));
        header.AddStretch(1);
        var image = new QLabel();
        image.SetPixmap(TrayIcons.Get(icon, TrayTheme.Tokens.SidebarMuted).Pixmap(20, 20));
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
