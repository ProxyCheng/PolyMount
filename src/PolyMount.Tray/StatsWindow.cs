using PolyMount.Core;
using PolyMount.Core.Ipc;
using QtSharp.Api;
using System.Diagnostics;
using System.Text.Json;

namespace PolyMount.Tray;

/// <summary>Task-manager style performance charts. The page samples asynchronously at a rate the
/// reader chooses; every figure on it is a rate, so the line reads the same at any of them.</summary>
internal sealed class StatsWindow : QWidget, ICloseGuard
{
    private readonly QWidget _page;
    private readonly QComboBox _mount;
    private readonly string? _instanceName;
    /// <summary>The instance this window charts. A window opened on one instance holds the process
    /// that serves it; the "all instances" view names none and asks the supervisor, which is the
    /// only process that can answer for all of them.</summary>
    private readonly ControlClient _client;
    private readonly RecordingPanel? _recording;
    private readonly QPushButton _pause;
    private readonly QTableWidget _table;
    private readonly QLabel _status;
    private readonly LayerAccessPie _accessPie;
    private readonly QLabel _accessLegend;
    private readonly QLabel _responseCost;
    private readonly Queue<(double Request, double Dispatch, double Ui, DateTime At)> _responseSamples = new();
    private readonly QComboBox _refresh, _range;
    private List<MountMetrics> _accessMetrics = new();
    private readonly QTimer _timer;
    private readonly PerformanceChart[] _charts;
    /// <summary>What the instance's own process cost, one row per sample. A process is not
    /// a layer: there is nothing to break down, so these are drawn as plain series rather than as
    /// contributions that stack.</summary>
    private readonly List<ProcessSecond> _processHistory = new();
    /// <summary>What each layer holds — its memory and its on-disk footprint — one row per second.
    /// A process's memory cannot be split between layers, but this can: it is our own count of what
    /// each layer keeps, which is that layer's own share of the cost.</summary>
    private readonly List<HeldSecond> _heldHistory = new();
    /// <summary>What the layers hold, in two cards: the memory they keep and the bytes they take on
    /// disk. It was one card that added the two together, which hid which of them had moved — they
    /// move for different reasons, a memory budget evicting against a disk budget filling up, and
    /// the sum of them is a number with no budget behind it.</summary>
    private readonly PerformanceChart _heldMemory, _heldDisk;
    private readonly QLabel _reconcile;
    private DateTime _lastHeld = DateTime.MinValue;
    private readonly PerformanceChart[] _processCharts;
    private readonly QLabel _processFigures;
    /// <summary>The parts of the page under the charts, each folded away with a line of its own
    /// numbers left in the header. What the page is watched for is the charts; this is what a
    /// chart sends a reader looking for, and reading it is a thing one does deliberately.
    ///
    /// The division is the one a monitoring surface is drawn along: what tells you something is
    /// wrong stays visible, and what tells you why is one click away. It is deliberately not a
    /// matter of hiding things that matter — a folded section keeps the few numbers that would
    /// make a reader open it, so folding something away does not also hide whether it is worth
    /// opening.</summary>
    private readonly CollapsibleSection _layersSection, _processSection, _accountSection;
    /// <summary>The bodies those sections fold. They are held apart from the sections themselves
    /// because a smoke run photographs them, and a widget that is not on screen has nothing to
    /// photograph; opening the section is how the run gets one back.</summary>
    private readonly QWidget _layerBody, _processBody, _accountBody;
    private readonly InstanceProcess _process;
    private readonly Dictionary<string, List<LayerSampler.Sample>> _history = new();
    /// <summary>Last-seen waste counter per layer, with the time it was read: the charts plot
    /// a rate, so a step only means something next to the interval it was measured over.</summary>
    private readonly Dictionary<string, LayerSampler.Counters> _wasteSteps = new();
    /// <summary>A step spanning more than this is a pause in observation rather than a rate. A
    /// floor: a page refreshing slower than once a second spaces its observations wider than this
    /// by design, and the gap it tolerates has to follow the rate.</summary>
    private const double SamplingGapSeconds = 3;
    /// <summary>The window the instance is asked about for the table, the pie and the legend. It is
    /// not the reader's time range: the charts draw this page's own samples, whatever span they
    /// cover, while a windowed question is a query over the instance's own record of recent reads
    /// and that record is finite.</summary>
    private const int AccessSeconds = 60;
    /// <summary>How many points one series is drawn with. A long range holds more observations
    /// than a screen can show, and past this the drawing folds them into buckets - the figures
    /// still come from every observation.</summary>
    private const int MaxPoints = 1200;
    /// <summary>Where the two choices below are remembered, beside the other preferences this tray
    /// keeps for a reader. Declared before them: a static initializer runs in the order it is
    /// written, and one that read this after it was set up would find it empty.</summary>
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PolyMount", "stats.json");
    /// <summary>The refresh rates a reader may choose between, in samples per second. The window
    /// asks for the time that elapsed since its previous sample, so a faster rate is a shorter
    /// window and not a denser reading of the same one.</summary>
    private static readonly double[] RefreshRates = { 0.5, 1, 2, 4, 10 };
    /// <summary>The spans a reader may choose between, in seconds: how much time the charts show.
    /// The page only has what it watched, so a long span starts empty and fills.</summary>
    private static readonly double[] RangeLadder = { 30, 60, 120, 300, 900 };
    /// <summary>The choices this page remembers, read once at startup and validated against the
    /// ladders above whichever they name.</summary>
    private static readonly (double RefreshHz, double RangeSeconds, Dictionary<string, bool> Sections) Remembered = LoadStats();
    /// <summary>Which of the sections under the charts the reader left open, by the key each is
    /// remembered under. Folded is where a section starts: a reader who has never touched one is
    /// shown the charts and three lines saying what is under them, which is the page's subject and
    /// nothing else.</summary>
    private readonly Dictionary<string, bool> _sections = new(Remembered.Sections);
    /// <summary>The height of one row of the layer table, which the table is sized from.</summary>
    private const int TableRowHeight = 36;
    private List<MountMetrics> _latest = new();
    private Task<(List<MountMetrics>? Trends, List<MountMetrics>? Access, List<HeldRow>? Held, ProcessReading Process, double RequestMs, long Completed, double Seconds)>? _pending;
    private bool _paused, _requested, _wasVisible;
    private int _epoch, _requestEpoch;
    private DateTime _next;
    /// <summary>When the last accepted sample was taken, which is what the next window starts
    /// from, and when the 60-second access window was last fetched — it keeps its own once-a-second
    /// schedule so that refreshing the charts faster does not multiply the expensive call.
    /// <c>MinValue</c> means "no previous sample": the next window is the nominal interval.</summary>
    private DateTime _lastSample = DateTime.MinValue, _lastAccess = DateTime.MinValue;
    /// <summary>How often the charts are refreshed, in samples per second.</summary>
    private double _refreshHz = Remembered.RefreshHz, _rangeSeconds = Remembered.RangeSeconds;
    private static string T(string key) => I18n.T(key);

    public StatsWindow(string? instanceName = null)
    {
        _instanceName = instanceName;
        _client = instanceName == null ? SupervisorClient.Default : InstanceDirectory.ClientFor(instanceName);
        _process = new InstanceProcess(instanceName);
        SetObjectName("dashboard");
        SetStyleSheet(TrayTheme.StyleSheet);
        SetWindowIcon(TrayIcons.App);
        I18n.Bind(SetWindowTitle, "stats.title");
        Resize(1180, 960);
        // The page itself, kept because it is what a capture of the whole page is taken of: the
        // window shows the part of it that fits, and the scroll area is what shows the rest.
        _page = WindowScrollArea.Create(this, 900);
        var root = new QVBoxLayout(_page);
        root.SetContentsMargins(24, 22, 24, 20);
        root.SetSpacing(16);
        var header = new QHBoxLayout();
        var titles = new QVBoxLayout();
        var heading = new QLabel();
        heading.SetObjectName("pageTitle");
        I18n.Changed += () => heading.SetText(instanceName == null ? T("stats.heading") : I18n.F("stats.instance", instanceName));
        heading.SetText(instanceName == null ? T("stats.heading") : I18n.F("stats.instance", instanceName));
        titles.AddWidget(heading);
        titles.AddWidget(Label("stats.subtitle", "muted"));
        header.AddLayout(titles);
        header.AddStretch(1);
        _mount = ContinuousScrolling.Combo();
        _mount.SetMinimumWidth(180);
        _mount.AddItem(T("stats.all"));
        _mount.CurrentIndexChanged += _ => Render();
        header.AddWidget(_mount);
        _mount.SetVisible(instanceName == null);
        // The refresh rate belongs to the reader, not to the mount: it is how closely the line is
        // drawn, and every figure on it is a rate so the value does not change with it.
        _refresh = ContinuousScrolling.Combo();
        _refresh.SetMinimumWidth(96);
        foreach (double rate in RefreshRates) _refresh.AddItem(FormatRate(rate));
        _refresh.SetCurrentIndex(Array.IndexOf(RefreshRates, _refreshHz));
        _refresh.SetToolTip(T("stats.refresh.hint"));
        _refresh.CurrentIndexChanged += index =>
        {
            if (index < 0 || index >= RefreshRates.Length) return;
            _refreshHz = RefreshRates[index];
            SaveStats();
            // Take effect now rather than at the end of the interval that was already running.
            _next = DateTime.MinValue;
            _requested = true;
        };
        header.AddWidget(_refresh);
        // The range is the reader's too, and it is only this page's: the charts draw the samples
        // this window collected, so a longer span is more of its own history and nothing is asked
        // of the instance that was not asked before.
        _range = ContinuousScrolling.Combo();
        _range.SetMinimumWidth(96);
        foreach (double span in RangeLadder) _range.AddItem(PerformanceChart.SpanText(span));
        _range.SetCurrentIndex(Array.IndexOf(RangeLadder, _rangeSeconds));
        _range.SetToolTip(T("stats.range.hint"));
        _range.CurrentIndexChanged += index =>
        {
            if (index < 0 || index >= RangeLadder.Length) return;
            _rangeSeconds = RangeLadder[index];
            SaveStats();
            // A shorter span stops showing what it no longer covers, and the axis is redrawn from
            // what is left rather than from a sample that has not been taken yet. The one the
            // shorter window's left edge falls inside stays, as it does when a sample ages out:
            // that is what the series is drawn from to reach the edge.
            DateTime cutoff = DateTime.UtcNow.AddSeconds(-_rangeSeconds);
            foreach (var samples in _history.Values) LayerSampler.Trim(samples, cutoff, kept => kept.Time, BreakSeconds);
            _next = DateTime.MinValue;
        };
        header.AddWidget(_range);
        _pause = new QPushButton();
        _pause.Clicked += _ => TogglePause();
        header.AddWidget(_pause);
        root.AddLayout(header);

        // Recording is a mode of this panel rather than a page beside it: it is the same instance,
        // the same layers and the same seconds, only kept instead of forgotten after a minute. It
        // needs an instance to belong to, which is what the mount selector is for.
        if (instanceName != null)
        {
            _recording = new RecordingPanel(instanceName);
            root.AddWidget(_recording);
        }

        _charts = new[]
        {
            new PerformanceChart("stats.reads", "#2563eb", "/s", ChartSummary.Volume),
            new PerformanceChart("stats.layerBytes", "#059669", "B", ChartSummary.Volume),
            new PerformanceChart("stats.latency", "#8b5cf6", "ms", ChartSummary.Latency),
            new PerformanceChart("stats.bytes", "#0891b2", "B", ChartSummary.Volume)
        };
        var grid = new QGridLayout();
        grid.SetSpacing(16);
        for (int i = 0; i < _charts.Length; i++) grid.AddWidget(_charts[i], i / 2, i % 2);
        // The next two cards for what the layers keep: the other four are what they did, these are
        // what they hold. Two of them, because memory and disk have budgets of their own and it is
        // worth seeing which one is filling — a single stack of both answers neither question.
        _heldMemory = new PerformanceChart("stats.layerHeld.memory", "#7c3aed", "B", ChartSummary.Volume);
        _heldDisk = new PerformanceChart("stats.layerHeld.disk", "#c026d3", "B", ChartSummary.Volume);
        grid.AddWidget(_heldMemory, _charts.Length / 2, 0);
        grid.AddWidget(_heldDisk, _charts.Length / 2, 1);
        // A recording's edges are worth seeing the moment they happen, not at the next sample.
        if (_recording != null) _recording.MarksChanged += Render;
        root.AddLayout(grid, 1);

        // What the layers did, and what they hold, one section: the table and the pie are the same
        // 60-second window read two ways, and they answer one question between them.
        _layerBody = new QWidget();
        var layerBody = new QVBoxLayout(_layerBody);
        layerBody.SetContentsMargins(0, 0, 0, 0);
        layerBody.SetSpacing(14);
        var access = new QHBoxLayout();
        _accessPie = new LayerAccessPie();
        access.AddWidget(_accessPie);
        _accessLegend = new QLabel();
        _accessLegend.SetTextFormat(Qt.TextFormat.RichText);
        _accessLegend.SetWordWrap(true);
        access.AddWidget(_accessLegend, 1);
        layerBody.AddLayout(access);
        _table = ContinuousScrolling.Table();
        _table.SetColumnCount(8);
        _table.SetEditTriggers(QAbstractItemView.EditTriggers.NoEditTriggers);
        _table.SetShowGrid(false);
        _table.SetSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows);
        _table.VerticalHeader().Hide();
        _table.HorizontalHeader().SetSectionResizeMode(QHeaderView.ResizeMode.Stretch);
        _table.HorizontalHeader().SetDefaultAlignment(Qt.Alignment.AlignLeft | Qt.Alignment.AlignVCenter);
        // The layer list is shown whole: the window's own scroll area is what scrolls, so a list
        // of layers is never hidden behind a second scrollbar.
        _table.SetVerticalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff);
        layerBody.AddWidget(_table);
        layerBody.AddWidget(Label("stats.layerBytes.definition", "muted"));
        // What the two held-bytes cards above are counting. It belongs here, with the layer table,
        // rather than under the cards themselves: a chart card carries a number and a line of
        // legend, and a paragraph under it would be read once and then be in the way every second
        // after that.
        layerBody.AddWidget(Label("stats.layerHeld.definition", "muted"));

        // The machine's own account of the instance, next to VFS's account of it. The unit of these
        // is the process: one instance is one process, so nothing has to be attributed to a mount
        // for them to be per instance.
        _processBody = new QWidget();
        var processBody = new QVBoxLayout(_processBody);
        processBody.SetContentsMargins(0, 0, 0, 0);
        processBody.SetSpacing(12);
        _processCharts = new[]
        {
            new PerformanceChart("stats.process.cpu", "#0f766e", "cores", ChartSummary.Volume),
            new PerformanceChart("stats.process.memory", "#b45309", "B", ChartSummary.Volume),
            new PerformanceChart("stats.process.threads", "#475569", "", ChartSummary.Volume),
            new PerformanceChart("stats.process.handles", "#64748b", "", ChartSummary.Volume)
        };
        var processGrid = new QGridLayout();
        processGrid.SetSpacing(16);
        for (int i = 0; i < _processCharts.Length; i++) processGrid.AddWidget(_processCharts[i], i / 2, i % 2);
        processBody.AddLayout(processGrid);
        _processFigures = new QLabel();
        _processFigures.SetObjectName("muted");
        _processFigures.SetWordWrap(true);
        _processFigures.SetTextFormat(Qt.TextFormat.PlainText);
        processBody.AddWidget(_processFigures);
        processBody.AddWidget(Label("stats.process.definition", "muted"));

        // What the layers served and what the process moved are two different counts, and the
        // difference is the part nobody has attributed yet: this is that arithmetic, said out loud.
        // The overhead of answering this page belongs beside it because it is spent in the same
        // place — talking to the instance — and it is what the other numbers here are worth.
        _accountBody = new QWidget();
        var accountBody = new QVBoxLayout(_accountBody);
        accountBody.SetContentsMargins(0, 0, 0, 0);
        accountBody.SetSpacing(10);
        _reconcile = new QLabel();
        _reconcile.SetObjectName("muted");
        _reconcile.SetWordWrap(true);
        _reconcile.SetTextFormat(Qt.TextFormat.PlainText);
        accountBody.AddWidget(_reconcile);
        accountBody.AddWidget(Label("stats.reconcile.definition", "muted"));
        _responseCost = new QLabel();
        _responseCost.SetObjectName("muted");
        _responseCost.SetWordWrap(true);
        accountBody.AddWidget(_responseCost);

        _layersSection = Section("layers", "stats.section.layers", _layerBody);
        _processSection = Section("process", "stats.process", _processBody);
        _accountSection = Section("account", "stats.section.account", _accountBody);
        root.AddWidget(_layersSection);
        root.AddWidget(_processSection);
        root.AddWidget(_accountSection);
        _status = new QLabel();
        _status.SetObjectName("muted");
        _status.SetTextFormat(Qt.TextFormat.PlainText);
        root.AddWidget(_status);
        _timer = new QTimer(this);
        _timer.Timeout += Tick;
        _timer.Start(100);
        Translate();
    }

    public void ShowWindow()
    {
        Show();
        Raise();
        ActivateWindow();
        // Watching starts here: the first sample measures the nominal interval rather than the
        // time the window spent closed, so a hidden window is not billed to the point after it.
        _lastSample = DateTime.MinValue;
        _requested = true;
    }

    internal void Capture(string path) { using var image = Grab(); image.Save(path); }

    /// <summary>The whole page rather than the part of it that fits. The window shot is what a
    /// reader has on screen; this is everything on the page, laid out at its own height, which is
    /// the only way one picture can show a page that scrolls — the sections under the charts
    /// included.</summary>
    internal void CapturePage(string path)
    {
        // A grab draws the layout as it stands, so a page that was just rearranged — sections
        // opened, say — has to be laid out before it is drawn, or the picture is the old geometry
        // with the new widgets on top of it. Activating runs the layout now; the events are for
        // whatever it queued behind that.
        _page.Layout()?.Activate();
        QCoreApplication.ProcessEvents();
        using var image = _page.Grab();
        image.Save(path);
    }

    /// <summary>Opens every section, through the section itself rather than through its toggle, so
    /// that a smoke run arranging the page to photograph it does not also write that arrangement
    /// down: the reader's own page would come back rearranged. Everything below the charts is
    /// folded by default, so a run that means to photograph it has to open it first — a widget that
    /// is not on screen has nothing to photograph.</summary>
    internal void ExpandAll()
    {
        _layersSection.SetExpanded(true);
        _processSection.SetExpanded(true);
        _accountSection.SetExpanded(true);
    }

    /// <summary>The process section on its own, for a smoke run: it starts folded, and once it is
    /// open the window is taller than a 960 pixel screen, so a capture of the window would not show
    /// it at all.</summary>
    internal void CaptureProcess(string directory)
    {
        _processSection.SetExpanded(true);
        using var section = _processBody.Grab();
        section.Save(Path.Combine(directory, "process.png"));
    }

    /// <summary>The two held-bytes cards, for a smoke run: they sit below the fold of a 960 pixel
    /// window, the same way the process section does, and the byte account below them.</summary>
    internal void CaptureHeld(string directory)
    {
        _accountSection.SetExpanded(true);
        using var memory = _heldMemory.Grab();
        memory.Save(Path.Combine(directory, "layer-held.png"));
        using var disk = _heldDisk.Grab();
        disk.Save(Path.Combine(directory, "layer-held-disk.png"));
        using var line = _reconcile.Grab();
        line.Save(Path.Combine(directory, "reconcile.png"));
    }

    /// <summary>The trend charts on their own: a window shot on a small screen stops above the time
    /// axis, and the axis is where the range the reader chose is written.</summary>
    internal void CaptureCharts(string directory)
    {
        string[] files = { "chart-reads.png", "chart-bytes.png", "chart-latency.png", "chart-writes.png" };
        for (int i = 0; i < _charts.Length; i++)
        {
            using var chart = _charts[i].Grab();
            chart.Save(Path.Combine(directory, files[i]));
        }
    }

    /// <summary>The recording's own checks, driven from here because the panel belongs to this
    /// window and a smoke run has no other handle on it.</summary>
    internal void VerifyRecording(string? capture = null)
    {
        _recording?.Verify();
        // The marks are drawn by hand, in a paint path nothing else exercises while no recording is
        // running: this forces one with both edges on the axis.
        if (_recording == null) return;
        var end = DateTime.UtcNow;
        // Placed as fractions of the span rather than at fixed seconds: the marks that matter are
        // the ones just inside the axis, the one on its way out that must fade rather than be cut
        // off, and the one that has already left and must not be drawn at all. At a fixed -90 the
        // last of those would sit inside a fifteen-minute axis and quietly stop testing anything.
        double span = _rangeSeconds;
        foreach (var chart in _charts)
        {
            chart.SpanSeconds = span;
            chart.SetMarkers(new[]
            {
                new PerformanceChart.ChartMarker(end.AddSeconds(-span * 0.33), "record.marker.start", "#dc2626"),
                new PerformanceChart.ChartMarker(end.AddSeconds(-3), "record.marker.stop", "#64748b"),
                new PerformanceChart.ChartMarker(end.AddSeconds(-span * 0.95), "record.marker.start", "#dc2626"),
                new PerformanceChart.ChartMarker(end.AddSeconds(-span * 1.5), "record.marker.start", "#dc2626")
            });
            // A second apart where the span allows it, the way real samples arrive: a chart draws
            // breaks for gaps, so sparse data would leave the marks over an empty plot and prove
            // nothing. A very long span is sampled a little wider than that, as a smoke run does
            // not have fifteen minutes to wait for real samples.
            //
            // Started one sampling interval outside the window rather than on its edge, which is
            // where a history that has watched for longer than a window begins: it keeps the one
            // observation the window's left edge falls inside, and the series is drawn from it to
            // reach the edge. Started on the edge the capture would show the shortcut, not the
            // rule.
            var cache = new List<(DateTime Time, double Value)>();
            var svn = new List<(DateTime Time, double Value)>();
            double step = Math.Max(1, span / 600);
            for (double back = span + step; back >= 0; back -= step)
            {
                double wave = Math.Sin(back / 7.0);
                cache.Add((end.AddSeconds(-back), 6.0 + wave * 3.0));
                svn.Add((end.AddSeconds(-back), 2.0 + Math.Cos(back / 5.0) * 1.5));
            }
            chart.SetSeries(new List<ChartSeries>
            {
                new("cache", "#059669", cache),
                new("svn", "#2563eb", svn)
            }, end);
            using var painted = chart.Grab();
            if (capture != null) painted.Save(capture);
        }
    }
    /// <summary>Self-check for the process cards. Their numbers come from the operating system
    /// rather than from anything PolyMount counts, so what is checked is that a reading was really
    /// taken: a card drawn from an empty history looks exactly like a card drawn from a flat one,
    /// and an instance that reads as having no process at all would chart as zeroes for ever.
    /// </summary>
    internal void VerifyProcess()
    {
        if (_processCharts.Length != 4) throw new InvalidOperationException("The process section does not carry its four cards");
        foreach (var chart in _processCharts)
            if (chart.Figures.Count == 0) throw new InvalidOperationException("A process card carries no figures");
        if (_processHistory.Count == 0) throw new InvalidOperationException("No process reading was taken");
        // The history is bounded by time, so how many rows that is depends on the refresh rate:
        // what must hold is that it reaches back over the window its charts are drawn against, and
        // that the only row outside it is the one the window's left edge falls inside.
        VerifyHistory("The process history", _processHistory, second => second.Time);
        if (_process.Processes == 0) throw new InvalidOperationException("The instance reads as having no process behind it");
        var last = _processHistory[^1];
        if (last.WorkingSet <= 0 || last.Threads <= 0 || last.Handles <= 0)
            throw new InvalidOperationException($"A process reading came back empty: {last}");
        if (!_processFigures.Text.Contains(last.Threads.ToString(I18n.Culture), StringComparison.Ordinal))
            throw new InvalidOperationException("The process figures do not report what was just read");
    }

    /// <summary>Self-check for the rule every chart's history follows: a window's left edge falls
    /// inside the segment between two rows, so the history keeps the older of them and the series
    /// is drawn from it to reach the edge. One row outside the window, never more; a page that has
    /// not watched for a whole window yet has none, and its series begin where its rows do.
    ///
    /// The histories the page is running on are only as old as the page, so a smoke run's own are
    /// almost always the second case. That the rule holds when a history *is* long enough is
    /// checked on a built one, in VerifyResponseSampling.</summary>
    private void VerifyHistory<T>(string name, IReadOnlyList<T> rows, Func<T, DateTime> at)
    {
        if (rows.Count == 0) return;
        int outside = rows.Count(row => at(row) < at(rows[^1]).AddSeconds(-_rangeSeconds));
        if (outside > 1)
            throw new InvalidOperationException(
                $"{name} carries {outside} rows older than its {_rangeSeconds:0.#} second window, where the series is drawn from one");
    }

    internal void VerifyHistories()
    {
        foreach (var (name, samples) in _history) VerifyHistory("The history of " + name, samples, sample => sample.Time);
        VerifyHistory("The held history", _heldHistory, row => row.Time);
    }

    /// <summary>The page has to be given at least the room its own layout asks for.
    ///
    /// It is checked because of how this fails: silently, and looking like a styling problem. A
    /// scroll area sizes the page to the minimum it was told, and one told a number *below* what the
    /// layout wants answers by squeezing the layout rather than scrolling. Measured before this
    /// check existed: the page was pinned at 900 pixels against a layout that wanted more, and drew
    /// chart cards 173 pixels tall inside a 210 pixel minimum — which put each card's axis labels
    /// inside its own plot, while the top of the page, where the header and the recording bar are,
    /// looked perfectly normal in a screenshot.
    /// </summary>
    internal void VerifyLayout()
    {
        if (_page.Layout() is not { } layout) return;
        // Only the height is compared against what the layout asks for, and that comparison is the
        // one that found a real defect: a page pinned at 900 pixels tall against a layout that
        // wanted more, drawing chart cards 173 pixels tall inside a 210 pixel minimum, which put
        // each card's axis labels inside its own plot.
        //
        // The width is deliberately not compared the same way. Measured while the sections were
        // open, this page reported a minimum width of 2851 pixels in a state where nothing was
        // horizontally squeezed: the layer table asks for the width of its contents while stretching
        // into whatever width it is given — in the same run it drew 852 pixels wide and read
        // perfectly. A number that is not a floor is not something to check against; what a real
        // horizontal squeeze would look like is a card narrower than its own minimum, and that is
        // checked below.
        var needed = layout.MinimumSize();
        if (_page.Height < needed.Height())
            throw new InvalidOperationException(
                $"The page is {_page.Height} pixels tall where its layout needs at least {needed.Height()}");
        // The cards are where a squeeze shows, on either axis: nothing can be laid out smaller than
        // its own minimum, so a card that is, is one the layout had no room for.
        foreach (var chart in _charts.Concat(_processCharts).Append(_heldMemory).Append(_heldDisk))
            if (chart.Height < chart.MinimumHeight || chart.Width < chart.MinimumWidth)
                throw new InvalidOperationException(
                    $"{chart.TitleKey} is {chart.Width}x{chart.Height}, under its own " +
                    $"{chart.MinimumWidth}x{chart.MinimumHeight} minimum");
    }
    /// <summary>The two byte cards draw an extra slice — whatever no layer accounts for — so that
    /// their stack adds up to what the operating system says the process moved. That is an invariant
    /// of the data rather than of the picture, so it is checked on the data: every period of every
    /// series in the resolved-bytes and written-bytes cards, added up, has to be the process's own
    /// rate for that period. It holds trivially when nothing moved, which is still the invariant.
    /// </summary>
    internal void VerifyByteAccount()
    {
        var reads = TransferRates(reads: true);
        var writes = TransferRates(reads: false);
        foreach (var (chart, rates) in new[] { (_charts[1], reads), (_charts[3], writes) })
        {
            var series = chart.Series;
            if (series.Count == 0 || rates.Count == 0)
                throw new InvalidOperationException("A byte card is not drawing the process's own transfer rate");
            if (!series.Any(item => item.Name == T("stats.layerUnknown")))
                throw new InvalidOperationException("A byte card is missing the slice no layer accounts for: " + string.Join(", ", series.Select(item => item.Name)));
            int points = series[0].Samples.Count;
            for (int i = 0; i < points; i++)
            {
                DateTime at = series[0].Samples[i].Time;
                double stacked = series.Sum(item => item.Samples[i].Value);
                double moved = At(rates, at);
                if (Math.Abs(stacked - moved) > Math.Max(1, moved * 0.001))
                    throw new InvalidOperationException(
                        $"The byte card under {chart.TitleKey} stacks to {stacked:0.#} where the process moved {moved:0.#} at {at:HH:mm:ss.fff}");
            }
        }
    }

    internal void SmokeRecordingStartAndDiscard(string? capture = null) => _recording?.SmokeStartAndDiscard(capture);
    internal void CaptureRecording(string path) => _recording?.Capture(path);

    /// <summary>The recording belongs to this panel, so closing the panel is the moment to ask
    /// about it — the recording keeps collecting while the tab is parked, and a tab close is both
    /// a plausible way of saying "stop" and a plausible accident.</summary>
    void ICloseGuard.RequestClose(Action proceed)
    {
        if (_recording == null) proceed();
        else _recording.RequestClose(proceed);
    }

    /// <summary>The layer table on its own: the window capture stops above it, and one row per
    /// layer is the whole point of that table.</summary>
    internal void CaptureTable(string path)
    {
        _layersSection.SetExpanded(true);
        using var image = _table.Grab();
        image.Save(path);
    }
    internal void VerifyResponseSampling()
    {
        if (_responseSamples.Count == 0 || _responseCost.Text == T("stats.response.waiting"))
            throw new InvalidOperationException("Performance window did not collect response overhead samples");
        var mounts = new[] { new MountMetrics { Mount = "test", Layers = new()
        {
            new() { Name = "a", Hits = 2, AvgCostMs = 10, Bytes = 1024 },
            new() { Name = "b", Hits = 3, AvgCostMs = 20, Bytes = 2048, WriteBytes = 4096 },
            new() { Name = "idle" }
        } } };
        var now = DateTime.UtcNow;
        var sample = CaptureLayers(now, mounts, WasteSteps(now, mounts), 1);
        if (sample.Layers.Count != 3 || sample.Layers.Values.Sum(l => l.Reads) != 5 ||
            sample.Layers.Values.Sum(l => l.Cost) != 16 || sample.Layers.Values.Sum(l => l.Bytes) != 3072 ||
            sample.Layers.Values.Sum(l => l.WriteBytes) != 4096)
            throw new InvalidOperationException("Layer chart contributions do not sum to the overall metrics");
        // The same observation over a quarter of a second is four times the rate - and the same
        // duration, because a cost is not divided by anything.
        var quarter = CaptureLayers(now, mounts, WasteSteps(now, mounts), 0.25);
        if (LayerSampler.ChartValue(quarter.Layers["test/a"], 0, 0.25) != 8 ||
            LayerSampler.ChartValue(quarter.Layers["test/a"], 2, 0.25) != quarter.Layers["test/a"].Cost)
            throw new InvalidOperationException("Chart values are not rates over the interval they were measured across");
        // A block cache reports lifetime byte totals, so its window figure is the step between
        // observations rather than the counters themselves.
        var ram = new MountMetrics { Mount = "m", Layers = new() { new() { Name = "ram", Accelerating = true } } };
        var history = new List<LayerSampler.Sample>();
        foreach (var (served, fetched) in new[] { (0L, 0L), (3000L, 1000L), (6000L, 2000L) })
        {
            ram.Layers[0].AcceleratedBytes = served;
            ram.Layers[0].FetchedBytes = fetched;
            history.Add(CaptureLayers(now, new[] { ram }, WasteSteps(now, new[] { ram }), 1));
        }
        if (Accelerated(history, "m/ram") != (75.0, 6000.0))
            throw new InvalidOperationException("Accelerator bytes are not measured over the window");
        // The same traffic observed four times as often, in windows a quarter as long: every
        // amount is a quarter of the size and every interval is too, so the rate is the same
        // figure. This is the whole point of the refresh-rate control.
        var fine = new List<LayerSampler.Sample>();
        foreach (var (served, fetched) in new[] { (0L, 0L), (750L, 250L), (1500L, 500L) })
        {
            ram.Layers[0].AcceleratedBytes = served;
            ram.Layers[0].FetchedBytes = fetched;
            fine.Add(CaptureLayers(now, new[] { ram }, WasteSteps(now, new[] { ram }), 0.25));
        }
        if (Accelerated(fine, "m/ram") != (75.0, 6000.0))
            throw new InvalidOperationException("A rate changed with the refresh rate it was measured at");
        if (ByteUnits.Select(1023).Unit != "B" || ByteUnits.Select(1024).Unit != "KiB" ||
            ByteUnits.Select(1048576).Unit != "MiB" || ByteUnits.Select(1073741824).Unit != "GiB")
            throw new InvalidOperationException("Byte unit thresholds are incorrect");
        // A range long enough holds more observations than a screen can show, so the line is drawn
        // from fewer points than there were. A bucket carries the sum of what it folded, and the
        // window's total is an amount, so the two must add up to the same figure - that is the whole
        // reason the drawing may thin and the numbers may not.
        var held = new List<LayerSampler.Sample>();
        var start = DateTime.UtcNow;
        for (int second = 0; second < 300; second++)
            held.Add(new LayerSampler.Sample(start.AddSeconds(second), 1,
                new() { ["m/a"] = new LayerSampler.LayerSample(2, 4, 1024, 0) }));
        var folded = LayerSampler.Bucket(held, 60);
        if (folded.Count is < 2 or > 60)
            throw new InvalidOperationException($"Folding 300 observations into at most 60 points produced {folded.Count}");
        double Window(List<LayerSampler.Sample> samples) => ChartStatistics
            .Of(samples.Select(s => s.Layers["m/a"].Reads / s.Seconds), samples.Select(s => s.Seconds).ToArray()).Total;
        if (folded.Sum(s => s.Seconds) != 300 || folded.Sum(s => s.Layers["m/a"].Reads) != 600)
            throw new InvalidOperationException("Folding observations lost the interval or the amounts they covered");
        if (Window(folded) != Window(held))
            throw new InvalidOperationException($"A folded window reads {Window(folded)} where the window it was folded from reads {Window(held)}");
        // A history older than its window keeps the window's own observations and, where the
        // window's left edge falls between two of them, the one before the edge as well: the series
        // is drawn from it, so the line enters the edge instead of beginning a sampling interval
        // inside it. What is counted is unchanged — the window's own observations are the ones the
        // trim kept before this rule, so no figure moves with the sampling.
        var ledger = new Dictionary<string, List<LayerSampler.Sample>>();
        for (int step = 0; step < 12; step++)
            LayerSampler.Add(ledger, "m/a", new LayerSampler.Sample(start.AddSeconds(step * 7), 7,
                new() { ["m/a"] = new LayerSampler.LayerSample(2, 4, 1024, 0) }), 30, 7);
        var aged = ledger["m/a"];                              // stamped every 7 s to +77, window [47, 77]
        if (aged.Count != 6 || aged[0].Time != start.AddSeconds(42))
            throw new InvalidOperationException(
                $"A 30 second window kept {aged.Count} observations spanning {(aged[^1].Time - aged[0].Time).TotalSeconds:0.#} seconds");
        var counted = Covered(aged, aged[^1].Time, 30, sample => sample.Time);
        if (counted.Count != 5 || counted[0].Time != start.AddSeconds(49))
            throw new InvalidOperationException(
                $"A 30 second window counts {counted.Count} observations, from {counted[0].Time - start} seconds in");
        // A page that has watched for less than a window keeps every observation it took: there is
        // nothing before the edge to draw from, and drawing one from the edge would assert a value
        // over a stretch nothing was measured across.
        var young = new Dictionary<string, List<LayerSampler.Sample>>();
        for (int step = 0; step < 4; step++)
            LayerSampler.Add(young, "m/a", new LayerSampler.Sample(start.AddSeconds(step * 7), 7,
                new() { ["m/a"] = new LayerSampler.LayerSample(2, 4, 1024, 0) }), 30, 7);
        if (young["m/a"].Count != 4 || Covered(young["m/a"], start.AddSeconds(21), 30, sample => sample.Time).Count != 4)
            throw new InvalidOperationException("A window younger than its span lost observations of it");
        // And an observation the drawing would break the line at — a page that was hidden for
        // longer than a window holds nothing from the far side of the pause — is not kept either:
        // no segment is drawn from it, and one stale level sitting at the edge is what the axis
        // would be scaled to.
        var paused = new Dictionary<string, List<LayerSampler.Sample>>();
        foreach (double at in new[] { 0.0, 40.0, 47.0 })
            LayerSampler.Add(paused, "m/a", new LayerSampler.Sample(start.AddSeconds(at), 7,
                new() { ["m/a"] = new LayerSampler.LayerSample(2, 4, 1024, 0) }), 30, 7);
        if (paused["m/a"][0].Time != start.AddSeconds(40))
            throw new InvalidOperationException(
                $"A history kept a reading {paused["m/a"][0].Time - start} seconds old in a 30 second window");
        // The time axis has to name its span and its ticks in the unit each makes readable: a
        // fifteen-minute range divided into a hundred-second steps would be unreadable, and a
        // caption still saying "60 seconds ago" would be wrong.
        if (PerformanceChart.TimeStep(900 / 8.0) != 120 || PerformanceChart.TimeTick(300, 120) != "5m" ||
            PerformanceChart.TimeStep(12) != 15 || PerformanceChart.TimeTick(45, 15) != "45s")
            throw new InvalidOperationException("The time axis does not name its ticks in the unit that fits them");
        if (!PerformanceChart.SpanText(900).Contains("15", StringComparison.Ordinal) ||
            !PerformanceChart.SpanText(30).Contains("30", StringComparison.Ordinal))
            throw new InvalidOperationException("The axis caption does not name the span it is drawn over");
    }

    /// <summary>A paint costs a line and a label per interval, so the vertical domain has to
    /// stay bounded whatever the data does. Waste is the sample that can break that: a
    /// read-ahead layer reports its whole budget evicted at once, against a read axis scaled
    /// to a few kilobytes, and the chart used to answer with six hundred million intervals.</summary>
    internal void VerifyChartScale()
    {
        var time = DateTime.UtcNow;
        foreach (string unit in new[] { "B", "/s", "ms" })
            foreach (double read in new[] { 0.0, 40960.0 })
            {
                var domain = ChartDomain.Of(new List<ChartSeries>
                {
                    new("svn", "#64748B", new() { (time.AddSeconds(-60), read), (time, read) }),
                    new("predict", "#94A3B8", new() { (time.AddSeconds(-60), -316368879), (time, 0) })
                }, unit, 2.1);
                if (domain.GridLines > 64 || domain.Floor > 0 || domain.Max <= 0 || !double.IsFinite(domain.Floor))
                    throw new InvalidOperationException(
                        $"Chart axis is not bounded for {unit} at {read}: {domain.GridLines} intervals, floor {domain.Floor}");
            }
    }

    /// <summary>Smoke check: the chart summaries describe their samples, the charts carry the
    /// figures their metric is described by, and the layer table fits the rows it holds. None of
    /// it needs a running service.</summary>
    internal void VerifyCharts()
    {
        var stats = ChartStatistics.Of(new[] { 1.0, 2.0, 3.0, 4.0, 10.0 });
        if (stats.Current != 10 || stats.Average != 4 || stats.Peak != 10 || stats.P95 != 10 || stats.Total != 20 || stats.Samples != 5)
            throw new InvalidOperationException($"Chart statistics do not describe their samples: {stats}");
        if (ChartStatistics.Of(Array.Empty<double>()).Samples != 0)
            throw new InvalidOperationException("An empty window reported samples");
        // A rate series weighs each observation by the interval it covered, so the total stays the
        // amount the window counted: two quarter-second observations of 2/s are one request, not
        // four, while the average and the peak stay the rates they are.
        var quarter = ChartStatistics.Of(new[] { 2.0, 2.0 }, new[] { 0.25, 0.25 });
        if (quarter.Total != 1 || quarter.Average != 2 || quarter.Peak != 2)
            throw new InvalidOperationException($"Weighted chart statistics are not an amount over the window: {quarter}");
        if (ByteUnits.Split(1536) != ("1.5", "KiB"))
            throw new InvalidOperationException("Byte figures no longer split into a number and a unit");
        // Milliseconds are summed by nothing: a latency chart reports its tail, not a total.
        if (_charts.Length != 4 || _charts[2].Figures.Contains(ChartFigure.Total))
            throw new InvalidOperationException("Charts do not carry the figures their metric is described by");
        // A card too narrow for every figure keeps the first ones instead of clipping one.
        if (PerformanceChart.Fitted(new[] { 0, 60, 120, 180 }, 150) != 2 ||
            PerformanceChart.Fitted(new[] { 0, 60, 120, 180 }, 180) != 3 ||
            PerformanceChart.Fitted(new[] { 0, 60, 120, 180 }, 59) != 0)
            throw new InvalidOperationException("Chart figures do not degrade by importance when the card is narrow");
        // The layer table carries every layer: no second scrollbar and no row outside the widget.
        if (_table.RowCount > 0 && (_table.Height < _table.HorizontalHeader().Height + _table.RowCount * TableRowHeight ||
            _table.VerticalScrollBarPolicy != Qt.ScrollBarPolicy.ScrollBarAlwaysOff))
            throw new InvalidOperationException($"Layer table shows {_table.RowCount} rows in {_table.Height} px");

    }

    /// <summary>Self-check for a smoke run of the bundle a recording writes. The window's own snapshot
    /// export is one thing; this is the part a reader actually receives, and it has one shape
    /// nothing else produces: a window that is the session's own length, samples carried rather
    /// than rebuilt, the copy of the log moved in rather than taken at the end, and a second file
    /// per second. A recording of a fast workload also has to rank its paths, or the bundle
    /// answers nothing about which ones cost the time.</summary>
    internal void VerifyRecordingBundle(string directory)
    {
        var started = DateTime.UtcNow.AddSeconds(-3);
        var ended = DateTime.UtcNow;
        string work = Path.Combine(directory, "recording-fixture");
        string logs = Path.Combine(work, "logs", "TX4Package");
        Directory.CreateDirectory(logs);
        // Two seconds of a log in the same format the service writes: one traced root and one
        // probe the sampling rule would have dropped, marked by its negative id.
        var lines = new List<string>();
        foreach (var (at, id) in new[] { (started, 1L), (started.AddSeconds(1), -1L) })
        {
            string stamp = at.ToString("O");
            lines.Add($"{stamp} INF BEGIN id={id} parent=0 thread=1 pid=1 op=VFS.Lookup path='a' elapsedMs=0 stage='running'");
            lines.Add($"{stamp} INF END id={id} parent=0 thread=1 pid=1 op=VFS.Lookup path='a' elapsedMs=12 stage='resolved'");
        }
        File.WriteAllLines(Path.Combine(logs, "requests-000.log"), lines);
        // Two seconds of one layer, so the rows are the product of the two counts a reader
        // divides back out.
        File.WriteAllText(Path.Combine(work, "samples.csv"), StatsExport.SamplesCsvHeader
            + SampleLine(started, 0) + "\n" + SampleLine(started.AddSeconds(1), 1) + "\n");

        var snapshot = new StatsSnapshot
        {
            Scope = "TX4Package",
            Kind = "recording",
            CapturedUtc = ended,
            WindowSeconds = 3,
            SamplesFile = Path.Combine(work, "samples.csv"),
            LogsDirectory = Path.Combine(work, "logs"),
            Seconds = new List<SecondRecord> { new(ended, 2, 4096, 1.5, 0, 4, 0, 0, 1, 2, 3, 4, 5, 0, 0, 0) },
            ResponseRequest = new List<double> { 4 },
            Recording = new RecordingRecord
            {
                SessionId = "fixture", StartedUtc = started.ToString("O"), EndedUtc = ended.ToString("O"),
                StoppedReason = "saved", Seconds = 3, SampleTicks = 2, SampleRows = 2,
                Layers = new List<LayerTotalRecord> { new("TX4Package", "svn", "SVN", "#2563eb", false, 2, 0, 0, 6, 12, 0, 0) }
            }
        };
        var outcome = StatsExport.Write(directory, snapshot);
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(outcome.Directory, "summary.json")));
        var root = summary.RootElement;
        if (root.GetProperty("export").GetProperty("kind").GetString() != "recording")
            throw new InvalidOperationException("A recording bundle does not say it is one");
        var export = root.GetProperty("export");
        if (export.GetProperty("ticks").GetInt32() != 2 || export.GetProperty("layers").GetInt32() != 1 ||
            export.GetProperty("ticks").GetInt32() * export.GetProperty("layers").GetInt32() != export.GetProperty("rows").GetInt32())
            throw new InvalidOperationException($"A recording bundle describes {export.GetProperty("rows")} rows as "
                + $"{export.GetProperty("ticks")} seconds of {export.GetProperty("layers")} layers");
        if (root.GetProperty("recording").GetProperty("sampleTicks").GetInt32() != 2)
            throw new InvalidOperationException("A recording bundle does not carry the session's own account of itself");
        if (root.GetProperty("response").GetProperty("samples").GetInt32() != 1)
            throw new InvalidOperationException("A recording bundle does not report its own polling cost");
        // The log was moved in rather than copied, so the fixture no longer has it.
        string moved = Path.Combine(outcome.Directory, "logs", "TX4Package", "requests-000.log");
        if (!File.Exists(moved) || File.Exists(Path.Combine(logs, "requests-000.log")))
            throw new InvalidOperationException("A recording's log copy was not moved into the bundle");
        if (File.ReadAllLines(moved).Length != lines.Count)
            throw new InvalidOperationException("A recording's log copy lost lines on the way");
        if (File.ReadAllLines(Path.Combine(outcome.Directory, "seconds.csv")).Length != 2)
            throw new InvalidOperationException("A recording bundle's per-second file does not match the seconds it measured");
        using var digest = JsonDocument.Parse(File.ReadAllText(Path.Combine(outcome.Directory, "requests-summary.json")));
        var window = digest.RootElement.GetProperty("window");
        if (window.GetProperty("kind").GetString() != "recording" || !window.GetProperty("filtered").GetBoolean())
            throw new InvalidOperationException("A recording's request digest does not describe the session's window");
        // The window has to start where the recording did, which is how a reader that only knows
        // `windowSeconds` still computes the session without knowing anything new.
        if (DateTimeOffset.Parse(window.GetProperty("fromUtc").GetString()!) != new DateTimeOffset(started, TimeSpan.Zero))
            throw new InvalidOperationException("A recording's request digest starts somewhere other than the recording did");
        // A fast workload still has to be ranked: the floor that thins a sample would empty this.
        var paths = digest.RootElement.GetProperty("paths").EnumerateArray().ToArray();
        if (paths.Length == 0 || paths.All(path => path.GetProperty("ends").GetInt64() == 0))
            throw new InvalidOperationException("A recording's request digest ranked no paths");
        if (digest.RootElement.GetProperty("coverage").GetArrayLength() != outcome.Logs)
            throw new InvalidOperationException("A recording's digest does not account for every log it carries");
    }

    private static string SampleLine(DateTime at, int index) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{at:O},TX4Package,svn,{index + 1},0,0,0,0,0,0,0,1.5,12");

    private void TogglePause()
    {
        _paused = !_paused;
        _epoch++;
        _pause.SetText(T(_paused ? "stats.resume" : "stats.pause"));
        _status.SetText(T(_paused ? "stats.paused" : "stats.live"));
        if (!_paused) { _requested = true; _next = DateTime.MinValue; _lastSample = DateTime.MinValue; }
    }

    private void Tick()
    {
        bool visible = IsVisible();
        if (!visible && _wasVisible) _epoch++;
        if (_pending is { IsCompleted: true })
        {
            var task = _pending;
            _pending = null;
            try
            {
                var sample = task.GetAwaiter().GetResult();
                AcceptProcess(sample.Process, DateTime.UtcNow);
                if (_requestEpoch == _epoch && !_paused && visible)
                {
                    double dispatch = Stopwatch.GetElapsedTime(sample.Completed).TotalMilliseconds;
                    var ui = Stopwatch.StartNew();
                    if (sample.Access is { } access) AcceptAccess(access);
                    if (sample.Held is { } held)
                    {
                        // A reading that names no layer is not a reading of zero held. It is what
                        // the first status call after a mount comes back with, before the layers
                        // have opened their stores and have a footprint to report — and drawn, it
                        // put a point at nothing at the left end of both held cards and a vertical
                        // jump from there to the first real level. The clock moves either way, so
                        // a page that got one of these does not start asking every tick.
                        _lastHeld = DateTime.UtcNow;
                        if (held.Count > 0)
                        {
                            _heldHistory.Add(new HeldSecond(DateTime.UtcNow, held));
                            LayerSampler.Trim(_heldHistory, DateTime.UtcNow.AddSeconds(-_rangeSeconds),
                                row => row.Time, HeldBreakSeconds);
                        }
                    }
                    // Only a request that carried the trends is an observation of them: one that
                    // went out for the access window alone leaves the history and the overhead
                    // label untouched.
                    if (sample.Trends is { } trends)
                    {
                        // How much time the instance really watched, rather than the time the
                        // request asked about: a rate divided by a window the instance never saw
                        // is an invented number, and a process a second old does not have a
                        // minute of history to average over. The shortest of them, because the
                        // "all instances" view answers several mounts from one request and no
                        // amount may be divided by a longer span than it covers.
                        double measured = trends.Where(m => m.MeasuredSeconds > 0)
                            .Select(m => m.MeasuredSeconds).DefaultIfEmpty(sample.Seconds).Min();
                        // The table and legend show the recent average; the trend sample
                        // only feeds the chart history, where a gap would otherwise
                        // read as "every layer served nothing".
                        Accept(trends, measured);
                        _responseSamples.Enqueue((sample.RequestMs, dispatch, ui.Elapsed.TotalMilliseconds, DateTime.UtcNow));
                        DateTime horizon = DateTime.UtcNow.AddSeconds(-_rangeSeconds);
                        while (_responseSamples.Count > 0 && _responseSamples.Peek().At < horizon) _responseSamples.Dequeue();
                        RenderResponseCost();
                    }
                    else Render();
                    ui.Stop();
                }
            }
            catch (Exception ex)
            {
                if (!_paused && visible) _status.SetText(T("stats.gap") + " · " + ex.Message);
            }
        }
        if (visible && !_paused && _pending == null)
        {
            DateTime now = DateTime.UtcNow;
            // Watching starts here, resumes here, or follows a rate the reader just changed: the
            // next sample then measures the nominal interval rather than however long the window
            // spent hidden, paused or on the old rate.
            bool fresh = _requested || !_wasVisible || _lastSample == DateTime.MinValue;
            bool trendsDue = fresh || now >= _next;
            // The 60-second access window is the expensive half of a request and its readers are the
            // table and the pie, so it keeps a once-a-second schedule of its own instead of riding
            // the trend rate - which is also what keeps it at a second when the charts run slower.
            bool accessDue = _accessMetrics.Count == 0 || (now - _lastAccess).TotalSeconds >= 1;
            // What the layers hold is a status question, and status is the expensive one: it walks
            // every layer's storage. It rides the access schedule for the same reason.
            bool heldDue = _heldHistory.Count == 0 || (now - _lastHeld).TotalSeconds >= 1;
            if (trendsDue || accessDue || heldDue)
            {
                // The window is the time since the previous sample, so consecutive windows tile
                // the timeline: a tick that ran late - only one request may be in flight, so they
                // do run late - covers what it missed instead of leaving a hole or counting it
                // twice. The clamp keeps a long gap from billing a minute of traffic to one point.
                double seconds = fresh ? RefreshSeconds : Math.Clamp((now - _lastSample).TotalSeconds, 0.1, 5);
                _requested = false;
                _requestEpoch = _epoch;
                if (trendsDue) { _next = now.AddSeconds(RefreshSeconds); _lastSample = now; }
                _pending = Task.Run(() =>
                {
                    var watch = Stopwatch.StartNew();
                    // The process is read here rather than on the thread that draws: it is the
                    // operating system being asked about a process, and it can be gone by the time the
                    // question is asked.
                    var process = _process.Read();
                    var trends = trendsDue ? Window(seconds) : null;
                    var access = accessDue ? Window(AccessSeconds) : null;
                    var held = heldDue ? Held() : null;
                    watch.Stop();
                    return (trends, access, held, process, watch.Elapsed.TotalMilliseconds, Stopwatch.GetTimestamp(), seconds);
                });
            }
        }
        _wasVisible = visible;
    }


    /// <summary>What every layer of this window's instances holds. The metrics describe what the
    /// layers did; what they keep is a status question, and for the "all instances" view it takes
    /// every instance's own answer rather than the supervisor's summary of them.</summary>
    private List<HeldRow> Held()
    {
        var status = _instanceName == null ? InstanceView.Status() : _client.Status();
        var layers = new List<HeldRow>();
        foreach (var mount in status.Mounts.Where(m => _instanceName == null || m.Name == _instanceName))
            foreach (var provider in mount.Providers)
                if (provider.StorageUsage is { } usage)
                    layers.Add(new HeldRow(mount.Name + "/" + provider.Name,
                        string.IsNullOrEmpty(provider.DisplayName) ? provider.Name : provider.DisplayName,
                        provider.Color, usage.MemoryBytes, usage.DiskBytes));
        return layers;
    }

    /// <summary>What the layers are holding, in two cards: the memory they keep and the bytes they
    /// take on disk. Both are drawn the way the other layer cards are — one series per layer, in
    /// that layer's own colour, from this window's own once-a-second readings — because they are one
    /// reading split by where it lives, and two cards is what makes it visible which of the two has
    /// just moved.</summary>
    private void RenderHeld(string selected)
    {
        DateTime end = _heldHistory.Count == 0 ? DateTime.UtcNow : _heldHistory[^1].Time;
        // Drawn from the whole history, the reading the window's left edge falls inside included,
        // so the fill starts on the edge; counted over the window's own readings only.
        var rows = _heldHistory;
        var covered = Covered(rows, end, _rangeSeconds, row => row.Time);
        var drawn = new[] { new List<ChartSeries>(), new List<ChartSeries>() };
        if (_heldHistory.Count > 0)
            foreach (var (key, name, color, _, _) in _heldHistory[^1].Layers)
            {
                if (selected.Length > 0 && !key.StartsWith(selected + "/", StringComparison.Ordinal)) continue;
                string label = selected.Length == 0 ? key : name;
                string seriesColor = string.IsNullOrEmpty(color) ? "#7c3aed" : color;
                drawn[0].Add(new ChartSeries(label, seriesColor,
                    rows.Select(row => (row.Time, (double)row.Layers.FirstOrDefault(layer => layer.Key == key).Memory)).ToList()));
                drawn[1].Add(new ChartSeries(label, seriesColor,
                    rows.Select(row => (row.Time, (double)row.Layers.FirstOrDefault(layer => layer.Key == key).Disk)).ToList()));
            }
        var cards = new[] { _heldMemory, _heldDisk };
        for (int card = 0; card < cards.Length; card++)
        {
            int which = card;
            double[] totals = covered.Select(row => (double)row.Layers
                .Where(layer => selected.Length == 0 || layer.Key.StartsWith(selected + "/", StringComparison.Ordinal))
                .Sum(layer => which == 0 ? layer.Memory : layer.Disk)).ToArray();
            cards[card].SpanSeconds = _rangeSeconds;
            cards[card].GapSeconds = HeldBreakSeconds;
            cards[card].SetSeries(drawn[card], end, ChartStatistics.Of(totals));
            cards[card].SetMarkers(_recording?.Marks ?? Array.Empty<PerformanceChart.ChartMarker>());
        }
    }

    /// <summary>What the process moved per second, as a step function of the readings taken once a
    /// second: a chart point between two readings is answered with the rate of the interval it is
    /// in. A counter that went backwards is a worker that was replaced, and that interval has no
    /// rate to report.</summary>
    private List<(DateTime Time, double Rate)> TransferRates(bool reads)
    {
        var rates = new List<(DateTime, double)>();
        for (int i = 1; i < _processHistory.Count; i++)
        {
            var (previous, current) = (_processHistory[i - 1], _processHistory[i]);
            double elapsed = (current.Time - previous.Time).TotalSeconds;
            long now = reads ? current.ReadBytes : current.WriteBytes;
            long before = reads ? previous.ReadBytes : previous.WriteBytes;
            if (elapsed <= 0 || now < before) continue;
            rates.Add((current.Time, (now - before) / elapsed));
        }
        return rates;
    }

    /// <summary>The rate in force at a moment: the last one measured at or before it.</summary>
    /// <summary>The rate in effect at <paramref name="time"/>: the last one measured at or before it.
    ///
    /// A time older than every measurement takes the earliest measurement rather than zero. That
    /// case is not rare at the end this matters: the rates come from the process readings, which
    /// arrive ten times a second, while the chart's points come from the trend samples, which
    /// arrive at the reader's rate — so the chart's oldest point is regularly older than the first
    /// process reading by a little, and answering zero there drew one byte of "no layer accounts
    /// for it" the size of everything the layers served, at the left end. That is the value
    /// jumping to nothing and back as the window slides.</summary>
    private static double At(List<(DateTime Time, double Rate)> rates, DateTime time)
    {
        if (rates.Count == 0) return 0;
        double value = rates[0].Rate;
        foreach (var (at, rate) in rates)
        {
            if (at > time) break;
            value = rate;
        }
        return value;
    }

    /// <summary>The two counts that describe bytes, and the difference between them. The layers
    /// report the sizes of the files they resolved, which is our own bookkeeping; the process
    /// reports what it actually moved, which is the operating system's. They answer different
    /// questions, so the gap between them is not an error — it is everything neither of them
    /// attributes: IO that has nothing to do with a mount (logs, the cache database, the pipe to
    /// this window), and the source's own fetching, which happens in a process of its own and so
    /// never appears here at all.
    /// </summary>
    private void RenderReconcile(IReadOnlyList<LayerSampler.Sample> samples, string selected)
    {
        long served = 0;
        foreach (var sample in samples)
            foreach (var (key, layer) in sample.Layers)
                if (selected.Length == 0 || key.StartsWith(selected + "/", StringComparison.Ordinal))
                    served += (long)layer.Bytes;

        // The wall-clock row: what the requests took, against what the layers were charged for.
        // A request no layer answered for is charged to nobody, so the difference is made of those
        // — the negative lookups an application produces by asking for files it may not have — and
        // of the paths whose time is not recorded per layer.
        var access = _accessMetrics.Where(m => selected.Length == 0 || m.Mount == selected).ToList();
        var lines = new List<string>();
        if (access.Count > 0)
        {
            double requests = access.Sum(m => m.RequestMs);
            double layers = access.Sum(m => m.Layers.Sum(l => l.AvgCostMs * l.Hits));
            lines.Add(I18n.F("stats.reconcile.wall", access.Max(m => m.MeasuredSeconds),
                requests, layers, requests - layers));
        }

        if (_processHistory.Count > 1)
        {
            var first = _processHistory[0];
            var last = _processHistory[^1];
            long moved = Math.Max(0, last.ReadBytes + last.WriteBytes - first.ReadBytes - first.WriteBytes);
            double seconds = Math.Max(1, (last.Time - first.Time).TotalSeconds);
            lines.Add(I18n.F("stats.reconcile.bytes", (long)Math.Round(seconds),
                ByteUnits.Format(moved), ByteUnits.Format(served), ByteUnits.Format(moved - served)));
        }
        _reconcile.SetText(string.Join("\n", lines));
    }

    /// <summary>One window of read analytics: the instance's own process answers for itself, and
    /// the "all instances" view — which no single process owns — is every one of them asked and put
    /// together.</summary>
    private List<MountMetrics> Window(double windowSeconds) => _instanceName == null
        ? InstanceView.Metrics(windowSeconds)
        : _client.Metrics(windowSeconds, _instanceName).Metrics;

    /// <summary>A layer key is "mount/layer"; the records carry the two apart.</summary>
    private static string Relative(string key, string mount) =>
        key.StartsWith(mount + "/", StringComparison.Ordinal) ? key[(mount.Length + 1)..] : key;

    /// <summary>One sample of what the instance's own process cost. CPU is a cumulative reading
    /// divided by the interval, so a row is that interval's share of a core.</summary>
    private readonly record struct ProcessSecond(DateTime Time, double Cores, long WorkingSet, long Private,
        int Threads, int Handles, long ReadBytes, long WriteBytes);

    /// <summary>One reading of what every layer holds, with the names and colors the layer charts
    /// already use, so the card reads as part of that family rather than as its own thing.</summary>
    /// <summary>One layer's holdings at one reading, split by where they live: the memory the layer
    /// keeps and the bytes it takes on disk. Two numbers rather than one, because they are two
    /// budgets — a memory budget evicting and a disk budget filling up are different things
    /// happening, and their sum is a number nothing is bounded by.</summary>
    private readonly record struct HeldRow(string Key, string Name, string Color, long Memory, long Disk);

    private readonly record struct HeldSecond(DateTime Time, List<HeldRow> Layers);

    /// <summary>Adds a reading to the window and repaints the process charts. A reading taken while
    /// nothing is running the instance is still a reading — zeroes say the process is not there,
    /// which is worth seeing rather than a gap the chart would draw as an absence of observation.
    /// The history is bounded by time rather than by count, because the refresh rate decides how
    /// many readings fall inside the window.
    /// </summary>
    private void AcceptProcess(ProcessReading reading, DateTime now)
    {
        _processHistory.Add(new ProcessSecond(now, reading.Cores, reading.WorkingSetBytes, reading.PrivateBytes,
            reading.Threads, reading.Handles, reading.ReadBytes, reading.WriteBytes));
        LayerSampler.Trim(_processHistory, now.AddSeconds(-_rangeSeconds), second => second.Time, BreakSeconds);
        RenderProcess();
    }

    /// <summary>The four process cards: the CPU the instance's process used, the memory it holds,
    /// and the threads and handles it has open. Their figures are the window's readings — the one
    /// the window's left edge falls inside is drawn, so that the line starts on the edge, but it is
    /// not counted, which is what keeps the header describing the window the line is drawn over.</summary>
    private void RenderProcess()
    {
        DateTime end = _processHistory.Count == 0 ? DateTime.UtcNow : _processHistory[^1].Time;
        var covered = Covered(_processHistory, end, _rangeSeconds, second => second.Time);
        var series = new List<ChartSeries>[]
        {
            new() { new(T("stats.process.cpu"), "#0f766e", _processHistory.Select(second => (second.Time, second.Cores)).ToList()) },
            new() { new(T("stats.process.memory"), "#b45309", _processHistory.Select(second => (second.Time, (double)second.WorkingSet)).ToList()) },
            new() { new(T("stats.process.threads"), "#475569", _processHistory.Select(second => (second.Time, (double)second.Threads)).ToList()) },
            new() { new(T("stats.process.handles"), "#64748b", _processHistory.Select(second => (second.Time, (double)second.Handles)).ToList()) }
        };
        double[][] figures =
        {
            covered.Select(second => second.Cores).ToArray(),
            covered.Select(second => (double)second.WorkingSet).ToArray(),
            covered.Select(second => (double)second.Threads).ToArray(),
            covered.Select(second => (double)second.Handles).ToArray()
        };
        for (int i = 0; i < _processCharts.Length; i++)
        {
            _processCharts[i].GapSeconds = BreakSeconds;
            _processCharts[i].SpanSeconds = _rangeSeconds;
            _processCharts[i].SetSeries(series[i], end, ChartStatistics.Of(figures[i]));
            _processCharts[i].SetMarkers(_recording?.Marks ?? Array.Empty<PerformanceChart.ChartMarker>());
        }
        if (_processHistory.Count == 0)
        {
            _processFigures.SetText(T("stats.process.waiting"));
            _processSection.SetSummary(T("stats.process.waiting"));
            return;
        }
        var last = _processHistory[^1];
        _processFigures.SetText(I18n.F("stats.process.figures",
            _process.Processes, last.Cores, ByteUnits.Format(last.WorkingSet), ByteUnits.Format(last.Private),
            last.Threads, last.Handles));
        // What the folded section says of itself: the processes behind the instance and what the
        // one reading in hand cost. The sentence above names more than a header has room for.
        _processSection.SetSummary(I18n.F("stats.section.process.summary",
            _process.Processes, last.Cores.ToString("0.00", I18n.Culture), ByteUnits.Format(last.WorkingSet)));
    }

    /// <summary>Takes a 60-second window: the table, the pie, the legend, and the layer list the
    /// trend series are named and coloured from all read this rather than the trend sample.</summary>
    private void AcceptAccess(List<MountMetrics> access)
    {
        _accessMetrics = access;
        _latest = access;
        _lastAccess = DateTime.UtcNow;
        var alive = access.Select(m => m.Mount).Append("").ToHashSet();
        foreach (var stale in _history.Keys.Where(k => !alive.Contains(k)).ToArray()) _history.Remove(stale);
    }

    private void Accept(List<MountMetrics> trends, double seconds)
    {
        DateTime now = DateTime.UtcNow;
        var names = _latest.Select(m => m.Mount).ToArray();
        string selected = _mount.CurrentIndex <= 0 ? "" : _mount.CurrentText;
        // One step per layer for the whole tick, shared with the combined sample: taking it
        // again for the combined view would find the counters already advanced and report zero.
        var wasteSteps = WasteSteps(now, trends);
        foreach (var mount in trends) Add(mount.Mount, CaptureLayers(now, new[] { mount }, wasteSteps, seconds));
        Add("", CaptureLayers(now, trends, wasteSteps, seconds));
        if (_mount.Count != names.Length + 1 || names.Where((name, i) => _mount.ItemText(i + 1) != name).Any())
        {
            _mount.BlockSignals(true);
            _mount.Clear();
            _mount.AddItem(T("stats.all"));
            foreach (var name in names) _mount.AddItem(name);
            int index = _latest.FindIndex(m => m.Mount == selected);
            _mount.SetCurrentIndex(index < 0 ? 0 : index + 1);
            _mount.BlockSignals(false);
        }
        Render();
        _status.SetText(T("stats.live") + " · " + DateTime.Now.ToString("HH:mm:ss"));
    }

    /// <summary>Takes the step for this tick of every counter a layer reports as a lifetime
    /// total, one entry per layer, through the rule the recording page shares. The gap that counts
    /// as a pause follows the refresh rate: at half a hertz two observations are two seconds
    /// apart by design, and a fixed three-second rule would zero the waste series whenever one
    /// of them ran late.</summary>
    private Dictionary<string, LayerSampler.WasteStep> WasteSteps(DateTime time, IEnumerable<MountMetrics> mounts) =>
        LayerSampler.Steps(time, mounts, _wasteSteps, Math.Max(SamplingGapSeconds, RefreshSeconds * 1.5));

    private LayerSampler.Sample CaptureLayers(DateTime time, IEnumerable<MountMetrics> mounts,
        Dictionary<string, LayerSampler.WasteStep> wasteSteps, double seconds) =>
        LayerSampler.Capture(time, mounts, wasteSteps, seconds);

    private void Add(string name, LayerSampler.Sample sample) =>
        LayerSampler.Add(_history, name, sample, _rangeSeconds, BreakSeconds);

    /// <summary>What an accelerating layer kept out of the streams underneath over the window.</summary>
    private static (double Percent, double Bytes) Accelerated(IEnumerable<LayerSampler.Sample> samples, string key) =>
        LayerSampler.Accelerated(samples, key);

    /// <summary>The rows a window covers, which is what anything counted per window counts.
    ///
    /// A history may keep one row older than the window — the one its left edge falls inside,
    /// which is where the drawn series enters the edge (see <see cref="LayerSampler.Trim"/>). That
    /// row is not part of the window: it covers an interval that ended before the window began,
    /// and after a pause it can cover seconds of it, so a total that included it would be a total
    /// that changed with the sampling rather than with the traffic. The line is drawn from it; the
    /// figures beside the line are the window's own.</summary>
    private static List<T> Covered<T>(IEnumerable<T> rows, DateTime end, double span, Func<T, DateTime> at) =>
        rows.Where(row => at(row) >= end.AddSeconds(-span)).ToList();

    private void Render()
    {
        string selected = _instanceName ?? (_mount.CurrentIndex <= 0 ? "" : _mount.CurrentText);
        var samples = _history.GetValueOrDefault(selected) ?? new();
        DateTime end = samples.LastOrDefault()?.Time ?? DateTime.UtcNow;
        // What the figures, the table and the reconciliation count, against what the charts draw:
        // the history carries one observation more than the window covers, and it is the one the
        // charts need to reach the window's left edge. See Covered.
        var covered = Covered(samples, end, _rangeSeconds, sample => sample.Time);
        RenderHeld(selected);
        RenderReconcile(covered, selected);
        var mounts = _latest.Where(m => selected == "" || m.Mount == selected).ToList();
        var rows = mounts.SelectMany(m => m.Layers.Select(l => (m, l))).ToList();
        var predictive = rows.FirstOrDefault(row => row.l.Predictive);
        string? predictiveKey = predictive.l == null ? null : predictive.m.Mount + "/" + predictive.l.Name;
        // Waste belongs to the read-ahead layer, so it keeps that layer's colour and is
        // told apart by hatch and dash instead.
        (string Name, string Color)? outcomes = predictive.l == null
            ? null
            : ((predictive.m.PredictiveWasted?.Resolve(I18n.Language) ?? "") + " · " +
               (string.IsNullOrEmpty(predictive.l.DisplayName) ? predictive.l.Name : predictive.l.DisplayName),
               string.IsNullOrEmpty(predictive.l.Color) ? "#64748B" : predictive.l.Color);
        // The line is drawn from at most MaxPoints observations: a long range holds more than a
        // screen can show, and past that the older ones are folded into buckets. Each point is
        // still an amount over the interval it covers, so the shape it draws is the same one, and
        // the figures below are taken from every observation rather than from these points.
        var points = LayerSampler.Bucket(samples, MaxPoints);
        double spacing = points.Count > 1 ? (points[^1].Time - points[0].Time).TotalSeconds / (points.Count - 1) : RefreshSeconds;
        for (int chart = 0; chart < _charts.Length; chart++)
        {
            int metric = chart;
            // An observation is an amount over the interval it was measured across, and the reader
            // chooses how long that is, so every count and byte is divided by its own interval
            // here: the same mount reads the same on the same chart whatever rate is selected.
            // Costs are durations and are left alone.
            double Rate(LayerSampler.Sample s) => s.Seconds > 0 ? s.Seconds : 1;
            var drawn = rows.Select(row => new ChartSeries(
                selected == "" ? row.m.Mount + "/" + row.l.Name : string.IsNullOrEmpty(row.l.DisplayName) ? row.l.Name : row.l.DisplayName,
                string.IsNullOrEmpty(row.l.Color) ? "#64748B" : row.l.Color,
                points.Select(s =>
                {
                    var layer = s.Layers.GetValueOrDefault(row.m.Mount + "/" + row.l.Name);
                    return (s.Time, layer == null ? 0 : LayerSampler.ChartValue(layer, metric, s.Seconds));
                }).ToList())).ToList();
            // A guess that had to be evicted unread is drawn below the zero line, so a
            // read-ahead layer that is guessing badly pulls its own chart downward.
            if (metric <= 1 && outcomes != null)
            {
                var (name, color) = outcomes.Value;
                drawn.Add(new ChartSeries(name, color, points.Select(s =>
                {
                    var layer = s.Layers.GetValueOrDefault(predictiveKey!);
                    double step = layer == null ? 0 : metric == 0 ? layer.Wasted : layer.WastedBytes;
                    return (s.Time, -step / Rate(s));
                }).ToList()) { Hatched = true });
            }
            // The transfer counters are a third opinion on the same window, and the layer account
            // is short of them by everything no layer answered for: the request log and the cache
            // database, the pipe this window reads, and bytes this account does not model. That
            // shortfall is drawn as a layer of its own, so the stack adds up to what the operating
            // system recorded. It is signed: a file whose size was resolved but never read leaves
            // the layers accounting for more than the process moved, and then the slice is the
            // correction rather than an addition.
            if (metric is 1 or 3 && drawn.Count > 0)
            {
                var transfer = TransferRates(reads: metric == 1);
                if (transfer.Count > 0)
                {
                    var unknown = points.Select((point, i) =>
                        (point.Time, At(transfer, point.Time) - drawn.Sum(series => series.Samples[i].Value))).ToList();
                    drawn.Add(new ChartSeries(T("stats.layerUnknown"), "#94a3b8", unknown) { Hatched = true });
                }
            }
            _charts[chart].SpanSeconds = _rangeSeconds;
            // A gap is what the drawing cannot join: bucketed points are further apart than the
            // observations they were folded from, so the threshold follows the point spacing too.
            _charts[chart].GapSeconds = Math.Max(BreakSeconds, spacing * 1.5);
            _charts[chart].SetSeries(drawn, end, Figures(covered, rows, metric, predictiveKey));
            _charts[chart].SetMarkers(_recording?.Marks ?? Array.Empty<PerformanceChart.ChartMarker>());
        }
        var accessRows = _accessMetrics.Where(m => selected == "" || m.Mount == selected)
            .SelectMany(m => m.Layers.Select(l => (Mount: m.Mount, Layer: l))).ToList();
        long total = accessRows.Sum(r => r.Layer.Hits + r.Layer.Writes);
        var slices = accessRows.Select(r => new AccessSlice(selected == "" ? r.Mount + "/" + r.Layer.Name
            : string.IsNullOrEmpty(r.Layer.DisplayName) ? r.Layer.Name : r.Layer.DisplayName,
            string.IsNullOrEmpty(r.Layer.Color) ? "#64748B" : r.Layer.Color, r.Layer.Hits, r.Layer.Writes)).ToList();
        _accessPie.SetSlices(slices);
        string accessLegend = "<b>" + T("stats.access.title") + "</b><br>" + string.Join("<br>", slices.Select(s =>
            $"<span style='color:{s.Color}'>●</span> " + System.Net.WebUtility.HtmlEncode(s.Name) + " · " +
            I18n.F("stats.access.counts", s.Reads, s.Writes, total == 0 ? 0 : 100.0 * (s.Reads + s.Writes) / total)));
        if (_accessLegend.Text != accessLegend) _accessLegend.SetText(accessLegend);
        // The line the layer section keeps while it is folded: how many layers there are and what
        // they served between them, which is enough to tell an instance that is working from one
        // that is not, without opening anything. The chart cards carry their own legend — the same
        // names, in the chart's own unit — so the line that used to repeat them here is gone.
        _layersSection.SetSummary(I18n.F("stats.section.layers.summary",
            rows.Count, total.ToString("N0", I18n.Culture)));
        bool structureChanged = _table.RowCount != rows.Count;
        if (structureChanged) { _table.SetUpdatesEnabled(false); _table.SetRowCount(rows.Count); }
        for (int row = 0; row < rows.Count; row++)
        {
            var (mount, layer) = rows[row];
            string accelerated = "";
            if (layer.Accelerating)
            {
                var (percent, bytes) = Accelerated(covered, mount.Mount + "/" + layer.Name);
                accelerated = I18n.F("stats.accelerated.cell", percent, ByteUnits.Format(bytes));
            }
            string[] values = { mount.Mount, string.IsNullOrEmpty(layer.DisplayName) ? layer.Name : layer.DisplayName, layer.Hits.ToString("N0", I18n.Culture), layer.SharePercent.ToString("0.#", I18n.Culture),
                layer.AvgCostMs.ToString("0.##", I18n.Culture), layer.MaxCostMs.ToString("0.##", I18n.Culture), ByteUnits.Format(layer.Bytes), accelerated };
            for (int col = 0; col < values.Length; col++)
            {
                var item = _table.Item(row, col);
                if (item == null)
                {
                    item = new QTableWidgetItem(values[col]);
                    item.SetTextAlignment(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter);
                    _table.SetItem(row, col, item);
                }
                else if (item.Text() != values[col]) item.SetText(values[col]);
            }
            if (layer.Color.Length > 0) _table.Item(row, 1).SetForeground(new QBrush(new QColor(layer.Color)));
            _table.SetRowHeight(row, TableRowHeight);
        }
        if (structureChanged) _table.SetUpdatesEnabled(true);
        FitTable();
    }

    /// <summary>What one chart's window adds up to, taken over every observation the page holds
    /// rather than over the points its line was drawn from: a long range folds observations into
    /// fewer points, and the figures beside the line must not follow the drawing. The stack is
    /// summed the way the series are built, so the two cannot disagree.</summary>
    private static ChartStatistics Figures(IReadOnlyList<LayerSampler.Sample> samples,
        IReadOnlyList<(MountMetrics M, LayerMetric L)> rows, int metric, string? predictiveKey)
    {
        var totals = new double[samples.Count];
        var seconds = new double[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            double value = 0;
            foreach (var row in rows)
                if (sample.Layers.TryGetValue(row.M.Mount + "/" + row.L.Name, out var layer))
                    value += LayerSampler.ChartValue(layer, metric, sample.Seconds);
            if (metric <= 1 && predictiveKey != null && sample.Layers.TryGetValue(predictiveKey, out var waste))
                value -= (metric == 0 ? waste.Wasted : waste.WastedBytes) / (sample.Seconds > 0 ? sample.Seconds : 1);
            totals[i] = value;
            seconds[i] = sample.Seconds;
        }
        return ChartStatistics.Of(totals, seconds);
    }

    /// <summary>Gives the table exactly the height of its rows and header. Its rows are all the
    /// layers there are, so a fixed height is what makes the list readable at a glance; the
    /// scrollbar is off, so a miscalculation would clip rather than hide a row.</summary>
    private void FitTable()
    {
        int header = _table.HorizontalHeader().Height;
        _table.SetFixedHeight(header + _table.RowCount * TableRowHeight + 2 * _table.FrameWidth);
    }

    private void Translate()
    {
        _pause.SetText(T(_paused ? "stats.resume" : "stats.pause"));
        _pause.SetIcon(TrayIcons.Get(_paused ? "play" : "stop"));
        _recording?.Translate();
        _layersSection.Translate();
        _processSection.Translate();
        _accountSection.Translate();
        _mount.SetItemText(0, T("stats.all"));
        _refresh.SetToolTip(T("stats.refresh.hint"));
        _range.SetToolTip(T("stats.range.hint"));
        _table.SetHorizontalHeaderLabels(new[] { T("mount.instances"), T("layer"), T("hits"), T("share"), T("avg"), T("max"), T("bytes"), T("stats.accelerated") });
        _status.SetText(T(_paused ? "stats.paused" : "stats.live"));
        Render();
        RenderProcess();
        RenderResponseCost();
    }

    /// <summary>The interval between samples: what the reader chose, inverted.</summary>
    private double RefreshSeconds => 1.0 / (_refreshHz > 0 ? _refreshHz : 1);

    /// <summary>How far apart two observations may be and still be drawn as one curve. A rate
    /// slower than one hertz spaces them wider on purpose, so this follows the rate rather than
    /// sitting at a fixed two and a half seconds.</summary>
    private double BreakSeconds => Math.Max(2.5, RefreshSeconds * 1.5);

    /// <summary>The same for the held cards, which no rate spaces: what the layers hold is a status
    /// question, asked once a second whatever the charts are set to, so their threshold takes the
    /// slower of the two rather than following a rate they do not have.</summary>
    private double HeldBreakSeconds => Math.Max(BreakSeconds, 3);

    /// <summary>A rate, as the control shows it. "Hz" is the same word in both languages, so only
    /// the number is formatted for the reader's culture.</summary>
    private static string FormatRate(double rate) => rate.ToString("0.##", I18n.Culture) + " Hz";

    /// <summary>The choices this page remembers: the two on the toolbar, and which of the sections
    /// under the charts the reader left open. Read once, and validated against the ladders: a rate
    /// or a span this build does not offer is not one it knows how to draw. Best effort in both
    /// directions, like the other preferences this tray keeps - a missing or unreadable file means
    /// the defaults, and a failed write is not worth telling the reader about.</summary>
    private static (double RefreshHz, double RangeSeconds, Dictionary<string, bool> Sections) LoadStats()
    {
        try
        {
            // A file written by an editor that marks its encoding starts with a byte-order mark,
            // and JSON does not: without dropping it the whole file would read as invalid and the
            // reader's choices would silently not stick.
            using var json = JsonDocument.Parse(File.ReadAllText(SettingsPath).TrimStart('﻿'));
            double rate = json.RootElement.GetProperty("RefreshHz").GetDouble();
            double span = json.RootElement.GetProperty("RangeSeconds").GetDouble();
            var sections = new Dictionary<string, bool>();
            // Missing from a file written before the sections folded, and missing is the right
            // answer then too: nothing open. A key this build does not have a section for is kept
            // rather than dropped, so a page that gains one back does not find its choice gone.
            if (json.RootElement.TryGetProperty("Sections", out var stored) && stored.ValueKind == JsonValueKind.Object)
                foreach (var entry in stored.EnumerateObject())
                    if (entry.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        sections[entry.Name] = entry.Value.GetBoolean();
            return (Array.Exists(RefreshRates, known => known == rate) ? rate : 1,
                Array.Exists(RangeLadder, known => known == span) ? span : 60, sections);
        }
        catch { return (1, 60, new Dictionary<string, bool>()); }
    }

    /// <summary>Writes everything this page remembers. Called from whichever choice changed, and
    /// writes all of them, because they share one file and a partial write would drop the rest.</summary>
    private void SaveStats()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new
            {
                RefreshHz = _refreshHz,
                RangeSeconds = _rangeSeconds,
                Sections = _sections
            }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>One folded part of the page: the widget that holds it, the key it is remembered
    /// under, and the header line that stands in for it while it is folded. Built here rather than
    /// in a helper because the body's own widgets are fields of this window, and a field of the
    /// readonly kind can only be given a value in the constructor it belongs to.</summary>
    private CollapsibleSection Section(string key, string titleKey, QWidget body)
    {
        var section = new CollapsibleSection(key, titleKey, body, _sections.GetValueOrDefault(key));
        section.Toggled += changed =>
        {
            _sections[changed.Key] = changed.Expanded;
            SaveStats();
        };
        return section;
    }
    private void RenderResponseCost()
    {
        if (_responseSamples.Count == 0)
        {
            _responseCost.SetText(T("stats.response.waiting"));
            _accountSection.SetSummary(T("stats.response.waiting"));
            return;
        }
        var requests = _responseSamples.Select(s => s.Request).Order().ToArray();
        double average = requests.Average();
        double p95 = ChartStatistics.Percentile(requests, 0.95);
        _responseCost.SetText(I18n.F("stats.response.cost", average, p95,
            _responseSamples.Average(s => s.Dispatch), _responseSamples.Average(s => s.Ui), _responseSamples.Count));
        // What the folded section says of itself: what it costs to answer this page, which is the
        // one figure here that a reader can act on without opening anything.
        _accountSection.SetSummary(I18n.F("stats.section.account.summary",
            average.ToString("0.00", I18n.Culture), p95.ToString("0.00", I18n.Culture)));
    }
    public override void ChangeEvent(QEvent e)
    {
        base.ChangeEvent(e);
        if (e.type() == QEvent.Type.LanguageChange && _table != null) Translate();
    }

    private static QLabel Label(string key, string name)
    {
        var label = new QLabel();
        label.SetObjectName(name);
        I18n.Bind(label.SetText, key);
        return label;
    }
}

/// <summary>Time-scaled stacked layer areas. Missing observations remain gaps.</summary>
internal sealed record ChartSeries(string Name, string Color, List<(DateTime Time, double Value)> Samples)
{
    /// <summary>Same colour as the layer, drawn as a hatched and dashed variation so a second
    /// series for one layer reads as that layer rather than as a different one.</summary>
    public bool Hatched { get; init; }
}

/// <summary>What a chart's window adds up to, over the same samples the plot draws: an idle
/// instance still reports the peak it reached earlier in the window.</summary>
internal readonly record struct ChartStatistics(double Current, double Average, double Peak, double P95, double Total, int Samples)
{
    /// <summary><paramref name="weights"/> are the seconds each value was measured over, for the
    /// charts whose line is a rate. The window's total is an amount rather than a rate, so it is
    /// the sum of each value carried back over its own interval; every other figure is a property
    /// of the rate series itself. Without weights — a gauge, or a fixture — each value counts once.</summary>
    public static ChartStatistics Of(IEnumerable<double> values, IReadOnlyList<double>? weights = null)
    {
        var samples = values.ToArray();
        if (samples.Length == 0) return default;
        var sorted = samples.Order().ToArray();
        double total = 0;
        for (int i = 0; i < samples.Length; i++)
            total += samples[i] * (weights == null || i >= weights.Count ? 1 : weights[i]);
        return new(samples[^1], samples.Average(), sorted[^1], Percentile(sorted, 0.95), total, samples.Length);
    }

    /// <summary>Nearest-rank percentile: the value the given fraction of the sorted set falls
    /// at or below. Used for the response overhead and the latency chart's tail alike.</summary>
    internal static double Percentile(IReadOnlyList<double> sorted, double fraction)
        => sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * fraction) - 1, 0, sorted.Count - 1)];
}

/// <summary>Which summary numbers a chart carries. A rate is worth knowing the average, the peak
/// and the window's volume of; a latency is worth knowing its average and its tail.</summary>
internal enum ChartSummary
{
    Volume,
    Latency
}

/// <summary>One secondary figure in a chart's header.</summary>
internal enum ChartFigure
{
    Average,
    Peak,
    Total,
    P95
}

/// <summary>The vertical domain of one chart, in that chart's own display unit.</summary>
internal readonly record struct ChartDomain(double Scale, string Unit, double Floor, double Max, double Step, int GridLines)
{
    /// <summary>Domain for a series set: the tallest stack above the line, and as far below it
    /// as anything actually reached.</summary>
    public static ChartDomain Of(IEnumerable<ChartSeries> series, string unit, double targetIntervals)
    {
        var samples = series.ToList();
        var times = samples.SelectMany(s => s.Samples.Select(sample => sample.Time)).Distinct().Order().ToArray();
        var values = samples.Select(s => s.Samples.ToDictionary(sample => sample.Time, sample => sample.Value)).ToArray();
        double peak = times.Select(time => values.Sum(s => Math.Max(0, s.GetValueOrDefault(time)))).DefaultIfEmpty(0).Max();
        double trough = times.Select(time => values.Sum(s => Math.Min(0, s.GetValueOrDefault(time)))).DefaultIfEmpty(0).Min();
        var axis = unit == "B" ? ByteUnits.Select(peak) : (Scale: 1.0, Unit: unit);
        double max = Math.Max(unit is "ms" or "B" or "cores" ? 1 : 5, peak / axis.Scale * 1.15);
        double step = 0;
        int intervals = 0;
        // D3 nice: round the domain outward, then recompute until the step stabilizes.
        for (int pass = 0; pass < 10; pass++)
        {
            double next = NiceStep(max / targetIntervals);
            if (unit != "ms" && unit != "B") next = Math.Max(1, next);
            if (next == step) break;
            step = next;
            intervals = (int)Math.Ceiling(step < 1 ? max * Math.Round(1 / step) : max / step);
            max = TickValue(intervals, step);
        }
        // The below-zero side is capped at the height of the positive side: every interval
        // costs a line and a label, so an uncapped floor lets one sample decide how long a
        // frame takes. Waste is the sample that can do it — a layer evicting its whole budget
        // in a second, on a chart scaled to a few kilobytes, asked for six hundred million
        // grid lines. What overflows the floor is drawn clipped to it.
        double depth = step <= 0 ? 0 : Math.Ceiling(-trough / axis.Scale / step);
        int below = (int)Math.Clamp(depth, 0, Math.Max(1, intervals));
        return new(axis.Scale, axis.Unit, -below * step, max, step, intervals + below);
    }

    /// <summary>D3 tickStep: nearest 1/2/5 decade, using geometric-mean thresholds.
    /// https://d3js.org/d3-array/ticks</summary>
    internal static double NiceStep(double target)
    {
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(target)));
        double fraction = target / magnitude;
        return magnitude * (fraction < Math.Sqrt(2) ? 1 : fraction < Math.Sqrt(10) ? 2 : fraction < Math.Sqrt(50) ? 5 : 10);
    }

    internal static double TickValue(int index, double step) => step < 1 ? index / Math.Round(1 / step) : index * step;
}

internal sealed class PerformanceChart : QWidget
{
    /// <summary>Text baselines in the card's header, shared by the headline, its figures and the
    /// per-layer values so the three rows read as one column.</summary>
    private const int HeadlineBaseline = 63, SeriesBaseline = 80;
    /// <summary>Gap between the headline and the first figure, and between figures.</summary>
    private const int FigureGap = 20;
    private readonly string _titleKey, _color, _unit;
    private readonly ChartFigure[] _figures;
    private DateTime _end;
    private List<ChartSeries> _series = new();

    /// <summary>What this card is drawing, for the smoke run's check that the byte cards add up to
    /// what the operating system recorded.</summary>
    internal IReadOnlyList<ChartSeries> Series => _series;
    private ChartStatistics _stats;
    public PerformanceChart(string titleKey, string color, string unit, ChartSummary summary)
    {
        _titleKey = titleKey;
        _color = color;
        _unit = unit;
        _figures = summary == ChartSummary.Latency
            ? new[] { ChartFigure.Average, ChartFigure.P95, ChartFigure.Peak }
            : new[] { ChartFigure.Average, ChartFigure.Peak, ChartFigure.Total };
        SetMinimumSize(330, 210);
    }
    public override void ChangeEvent(QEvent e)
    { base.ChangeEvent(e); if (e.type() == QEvent.Type.LanguageChange) Update(); }
    /// <summary>A moment on the axis rather than a measurement of one — the edge of a recording.
    /// It travels with the data and leaves the window the way a sample does.</summary>
    internal readonly record struct ChartMarker(DateTime Time, string LabelKey, string Color);

    private IReadOnlyList<ChartMarker> _markers = Array.Empty<ChartMarker>();

    /// <summary>Marks the axis at those moments. They are annotation rather than data: drawn over
    /// the fills, in the recording's own colour, and never part of the stack or its statistics.</summary>
    public void SetMarkers(IReadOnlyList<ChartMarker> markers)
    {
        _markers = markers;
        RefreshToolTip();
        Update();
    }

    /// <summary>What the card says on hover: the layers, the window's figures, and when a recording
    /// this window covers began or ended.</summary>
    private void RefreshToolTip()
    {
        var lines = _series.Select(s => s.Name + ": " + FormatValue(s.Samples.LastOrDefault().Value)).ToList();
        if (_stats.Samples > 0) lines.Add(string.Join(" · ", _figures.Select(f => I18n.T(FigureKey(f)) + " " + FormatFigure(f))));
        foreach (var marker in _markers)
            lines.Add(I18n.T(marker.LabelKey) + " " + marker.Time.ToLocalTime().ToString("HH:mm:ss", I18n.Culture));
        SetToolTip(string.Join("\n", lines));
    }

    /// <summary>How much time the axis spans, set by the page from the range its reader chose.</summary>
    public double SpanSeconds { get; set; } = 60;

    /// <summary>The stretches a time axis is divided into: a clock's own steps rather than the
    /// 1/2/5 ladder a value axis uses, which would divide a minute into 100 seconds.</summary>
    private static readonly double[] TimeSteps = { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 };

    /// <summary>The smallest clock step at least as wide as the one asked for.</summary>
    internal static double TimeStep(double target)
    {
        foreach (double step in TimeSteps)
            if (step >= target) return step;
        return TimeSteps[^1];
    }

    /// <summary>A time tick, in the unit its step makes readable. The unit is a symbol like the
    /// value axis's "ms" and "B" rather than a translated word, so it stays out of the catalog.</summary>
    internal static string TimeTick(double seconds, double step) => step >= 60
        ? (seconds / 60).ToString(seconds % 60 == 0 ? "0" : "0.#", I18n.Culture) + "m"
        : seconds.ToString("0", I18n.Culture) + "s";

    /// <summary>How much time the chart shows, as a reader reads it: the number is formatted for
    /// their culture and the unit is a catalog entry, because "min" is a word.</summary>
    internal static string SpanText(double seconds) => seconds >= 60
        ? I18n.F("stats.unit.minutes", (seconds / 60).ToString("0.#", I18n.Culture))
        : I18n.F("stats.unit.seconds", seconds.ToString("0", I18n.Culture));

    /// <summary>How much time between two observations is still the same stretch of curve rather
    /// than a gap in watching. The page that feeds this sets it from its refresh rate: half a
    /// hertz spaces its observations two seconds apart on purpose, and the fixed threshold this
    /// used to be would break the line between every pair of them.</summary>
    public double GapSeconds { get; set; } = 2.5;

    /// <summary><paramref name="seconds"/> carries the interval each observation was measured
    /// over, keyed by its time, so the window's total stays the amount it was rather than the sum
    /// of the per-second rates the line is drawn from. A gauge has none and passes none.</summary>
    /// <summary><paramref name="figures"/> are the chart's window figures, computed by the page
    /// over every observation it holds. Drawing folds observations into fewer points when a range
    /// is long, and the numbers beside the line must not follow the drawing; without them, the
    /// series are all there is and they are described as they are.</summary>
    public void SetSeries(List<ChartSeries> series, DateTime end, ChartStatistics? figures = null)
    {
        _series = series;
        _end = end;
        if (figures is { } given) _stats = given;
        else
        {
            // Every series is built from the same sample list, so the window's totals are the sum
            // of the series at each observation.
            var times = series.SelectMany(s => s.Samples.Select(sample => sample.Time)).Distinct().Order().ToArray();
            var values = series.Select(s => s.Samples.ToDictionary(sample => sample.Time, sample => sample.Value)).ToArray();
            _stats = ChartStatistics.Of(times.Select(time => values.Sum(s => s.GetValueOrDefault(time))));
        }
        RefreshToolTip();
        Update();
    }
    private string FormatValue(double value) => _unit switch
    {
        "B" => ByteUnits.Format(value),
        "" => value.ToString("0.##", I18n.Culture),
        _ => value.ToString("0.##", I18n.Culture) + " " + _unit
    };

    /// <summary>A figure in the chart's own unit. The window total is an amount rather than a
    /// rate, so the per-second charts drop their suffix there.</summary>
    private string FormatFigure(ChartFigure figure)
    {
        double value = figure switch
        {
            ChartFigure.Average => _stats.Average,
            ChartFigure.Peak => _stats.Peak,
            ChartFigure.P95 => _stats.P95,
            _ => _stats.Total
        };
        if (figure == ChartFigure.Total && _unit == "/s") return value.ToString("0.##", I18n.Culture);
        return _unit == "B" ? ByteUnits.Format(value) : value.ToString("0.##", I18n.Culture) + " " + _unit;
    }

    private static string FigureKey(ChartFigure figure) => figure switch
    {
        ChartFigure.Average => "stats.figure.average",
        ChartFigure.Peak => "stats.figure.peak",
        ChartFigure.P95 => "stats.figure.p95",
        _ => "stats.figure.total"
    };

    /// <summary>The current value as the card's figure, with the rest of the window's summary
    /// beside it. Figures are dropped from the least important end when the card is too narrow
    /// to hold them, so a narrow card keeps the average instead of clipping a value.</summary>
    private void DrawHeadline(QPainter p, int width, bool hasData)
    {
        using var numberFont = new QFont("Segoe UI", 20);
        numberFont.SetBold(true);
        using var unitFont = new QFont("Segoe UI", 11);
        using var numberMetrics = new QFontMetrics(numberFont);
        using var unitMetrics = new QFontMetrics(unitFont);
        var (number, unit) = _unit == "B" ? ByteUnits.Split(_stats.Current) : (_stats.Current.ToString("0.##", I18n.Culture), _unit);
        string text = hasData ? number : "—";
        p.SetFont(numberFont);
        p.SetPen(new QPen(new QColor(_color)));
        p.DrawText(18, HeadlineBaseline, text);
        int headRight = 18 + numberMetrics.HorizontalAdvance(text);
        if (hasData && unit.Length > 0)
        {
            p.SetFont(unitFont);
            p.SetPen(new QPen(new QColor("#718096")));
            p.DrawText(headRight + 5, HeadlineBaseline, unit);
            headRight += 5 + unitMetrics.HorizontalAdvance(unit);
        }
        if (!hasData) return;

        using var labelFont = new QFont("Segoe UI", 8);
        using var figureFont = new QFont("Segoe UI", 11);
        figureFont.SetBold(true);
        using var labelMetrics = new QFontMetrics(labelFont);
        using var figureMetrics = new QFontMetrics(figureFont);
        var labels = _figures.Select(FigureKey).Select(I18n.T).ToArray();
        var values = _figures.Select(FormatFigure).ToArray();
        int[] widths = labels.Select((label, i) => labelMetrics.HorizontalAdvance(label) + 6 + figureMetrics.HorizontalAdvance(values[i])).ToArray();
        int right = width - 18;
        int[] ends = new int[widths.Length + 1];
        for (int i = 0; i < widths.Length; i++) ends[i + 1] = ends[i] + (i == 0 ? 0 : FigureGap) + widths[i];
        int keep = Fitted(ends, right - FigureGap - headRight);
        int x = right - ends[keep];
        for (int i = 0; i < keep; i++)
        {
            p.SetFont(labelFont);
            p.SetPen(new QPen(new QColor("#718096")));
            p.DrawText(x, HeadlineBaseline, labels[i]);
            x += labelMetrics.HorizontalAdvance(labels[i]) + 6;
            p.SetFont(figureFont);
            p.SetPen(new QPen(new QColor("#334155")));
            p.DrawText(x, HeadlineBaseline, values[i]);
            x += figureMetrics.HorizontalAdvance(values[i]) + FigureGap;
        }
    }

    private static string TickText(double value, double step)
    {
        int decimals = Math.Clamp(-(int)Math.Floor(Math.Log10(step)), 0, 15);
        return value.ToString("F" + decimals, I18n.Culture);
    }

    /// <summary>How many summary figures fit in the room left of the headline, counted from the
    /// most important. The group is right-aligned, so a card too narrow to hold them all keeps the
    /// first ones rather than clipping a value. <paramref name="ends"/> is each figure's width
    /// added up, gaps included.</summary>
    internal static int Fitted(int[] ends, int room)
    {
        for (int count = ends.Length - 1; count > 0; count--)
            if (ends[count] <= room) return count;
        return 0;
    }

    /// <summary>What the card is showing, for the export and its own smoke check.</summary>
    internal string TitleKey => _titleKey;
    internal string Unit => _unit;
    internal IReadOnlyList<ChartFigure> Figures => _figures;
    internal ChartStatistics Statistics => _stats;

    public override void PaintEvent(QPaintEvent e)
    {
        using var p = new QPainter(AsQPaintDevice());
        p.SetRenderHint(QPainter.RenderHint.Antialiasing);
        try
        {
            int width = Width, height = Height;
            p.SetPen(new QPen(new QColor("#e2e8f0")));
            p.SetBrush(new QBrush(new QColor("#ffffff")));
            p.DrawRoundedRect(1, 1, width - 2, height - 2, 12, 12);
            using var titleFont = new QFont("Segoe UI", 11);
            titleFont.SetBold(true);
            p.SetFont(titleFont);
            p.SetPen(new QPen(new QColor("#334155")));
            p.DrawText(18, 29, I18n.T(_titleKey));
            var times = _series.SelectMany(s => s.Samples.Select(sample => sample.Time)).Distinct().Order().ToArray();
            var valuesBySeries = _series.Select(s => s.Samples.ToDictionary(sample => sample.Time, sample => sample.Value)).ToArray();
            DrawHeadline(p, width, times.Length > 0);

            using var detailFont = new QFont("Segoe UI", 8);
            using var detailMetrics = new QFontMetrics(detailFont);
            var summaries = _series.Select(s => (s.Color, Text: s.Name + " " + FormatValue(s.Samples.LastOrDefault().Value))).ToArray();
            // The row is drawn as far as it fits rather than all of it or none of it. What is in it
            // changes length from one sample to the next — "0 B" beside "465.64 MiB", and a byte
            // card gains the hatched slice no layer accounts for — so a card within a few pixels of
            // the edge used to lose the whole line on a busy sample, get it back on the next quiet
            // one, and take the top of the plot with it: the plot starts 20 pixels lower when the
            // row is not there. Nothing is lost by dropping the tail — the tooltip carries every
            // series already, and the layer table below carries them at length.
            bool legend = summaries.Length > 0;
            if (legend)
            {
                p.SetFont(detailFont);
                int x = 18, room = width - 18;
                foreach (var summary in summaries)
                {
                    int advance = detailMetrics.HorizontalAdvance(summary.Text);
                    int available = room - (x + 14);
                    if (available < 30) break;
                    // The one that will not fit is cut to the room left rather than left out, and
                    // the row ends there. Leaving it out would put the tail of the legend in and out
                    // of the picture as the values change length — "0 B" and "465.64 MiB" are fifty
                    // pixels apart — and an ellipsis that moves is a quieter thing to watch than a
                    // name that comes and goes.
                    bool clipped = advance > available;
                    string text = clipped
                        ? detailMetrics.ElidedText(summary.Text, Qt.TextElideMode.ElideRight, available)
                        : summary.Text;
                    // The dot carries the layer's colour and the value stays in ink, so a light
                    // layer hue cannot leave its own number unreadable.
                    p.SetPen(new QPen(Qt.PenStyle.NoPen));
                    p.SetBrush(new QBrush(new QColor(summary.Color)));
                    p.DrawEllipse(x + 1, SeriesBaseline - 6, 6, 6);
                    p.SetPen(new QPen(new QColor("#334155")));
                    p.DrawText(x + 14, SeriesBaseline, text);
                    if (clipped) break;
                    x += advance + 28;
                }
            }

            int top = legend ? 108 : 88, right = width - 18, bottom = height - 46;
            var domain = ChartDomain.Of(_series, _unit, Math.Max(2, (bottom - top) / 36.0));
            double max = domain.Max, valueStep = domain.Step, floor = domain.Floor;
            int gridLines = domain.GridLines;
            using var smallFont = new QFont("Segoe UI", 8);
            p.SetFont(smallFont);
            using var fontMetrics = new QFontMetrics(smallFont);
            int labelWidth = Enumerable.Range(0, gridLines + 1)
                .Max(i => fontMetrics.HorizontalAdvance(TickText(floor + ChartDomain.TickValue(i, valueStep), valueStep)));
            int left = 18 + labelWidth + 10;
            int timeLabelWidth = fontMetrics.HorizontalAdvance("−" + TimeTick(SpanSeconds, SpanSeconds)) + 24;
            double timeStep = TimeStep(SpanSeconds / Math.Max(2, (right - left) / (double)Math.Max(70, timeLabelWidth)));
            p.SetPen(new QPen(new QColor("#718096")));
            string maxLabel = TickText(max, valueStep) + " " + domain.Unit;
            p.DrawText(right - fontMetrics.HorizontalAdvance(maxLabel), top - 8, maxLabel);
            p.DrawText(left, height - 10, I18n.F("stats.history", SpanText(SpanSeconds)));
            p.DrawText(right - fontMetrics.HorizontalAdvance(I18n.T("stats.now")), height - 10, I18n.T("stats.now"));
            using var gridPen = new QPen(new QColor("#e7edf5"));
            using var labelPen = new QPen(new QColor("#718096"));
            for (int i = 0; i <= gridLines; i++)
            {
                int y = bottom - (int)Math.Round((bottom - top) * i / (double)gridLines);
                p.SetPen(gridPen);
                p.DrawLine(left, y, right, y);
                string label = TickText(floor + ChartDomain.TickValue(i, valueStep), valueStep);
                p.SetPen(labelPen);
                p.DrawText(left - 10 - fontMetrics.HorizontalAdvance(label), y + 4, label);
            }
            for (double seconds = 0; seconds <= SpanSeconds; seconds += timeStep)
            {
                int x = right - (int)Math.Round((right - left) * seconds / SpanSeconds);
                p.SetPen(gridPen);
                p.DrawLine(x, top, x, bottom);
                string label = seconds == 0 ? "0" : "−" + TimeTick(seconds, timeStep);
                int textWidth = fontMetrics.HorizontalAdvance(label);
                p.SetPen(labelPen);
                p.DrawText(Math.Clamp(x - textWidth / 2, left, right - textWidth), bottom + 17, label);
            }

            double X(DateTime time) => left + Math.Clamp(1 - (_end - time).TotalSeconds / SpanSeconds, 0, 1) * (right - left);
            // Clamped to the domain, so a sample reaching past the floor rests on it rather
            // than somewhere off the canvas.
            double Y(double number) => bottom - (Math.Clamp(number / domain.Scale, floor, max) - floor) / (max - floor) * (bottom - top);
            var baseline = new double[times.Length];
            // The last provider is the bottom of the chain and of the area stack.
            for (int layer = _series.Count - 1; layer >= 0; layer--)
            {
                var series = _series[layer];
                // A negative contribution hangs below the line instead of cutting into the stack.
                var lower = times.Select((time, index) => valuesBySeries[layer].GetValueOrDefault(time) < 0 ? 0 : baseline[index]).ToArray();
                var upper = times.Select((time, index) => lower[index] + valuesBySeries[layer].GetValueOrDefault(time)).ToArray();
                int start = 0;
                while (start < times.Length)
                {
                int finish = start + 1;
                while (finish < times.Length && (times[finish] - times[finish - 1]).TotalSeconds <= GapSeconds) finish++;
                using var fill = new QPainterPath();
                fill.MoveTo(X(times[start]), Y(lower[start]));
                for (int index = start; index < finish; index++) fill.LineTo(X(times[index]), Y(upper[index]));
                for (int index = finish - 1; index >= start; index--) fill.LineTo(X(times[index]), Y(lower[index]));
                fill.CloseSubpath();
                using var fillColor = new QColor(series.Color);
                fillColor.SetAlpha(155);
                p.SetPen(new QPen(Qt.PenStyle.NoPen));
                if (series.Hatched)
                {
                    using var hatch = new QBrush(Qt.BrushStyle.BDiagPattern);
                    hatch.SetColor(fillColor);
                    p.SetBrush(hatch);
                }
                else p.SetBrush(new QBrush(fillColor));
                p.DrawPath(fill);
                using var line = new QPainterPath();
                line.MoveTo(X(times[start]), Y(upper[start]));
                for (int index = start + 1; index < finish; index++) line.LineTo(X(times[index]), Y(upper[index]));
                using var pen = new QPen(new QColor(series.Color));
                if (series.Hatched) pen.SetStyle(Qt.PenStyle.DashLine);
                pen.SetWidthF(1.8);
                p.SetPen(pen);
                p.SetBrush(new QBrush(Qt.BrushStyle.NoBrush));
                p.DrawPath(line);
                start = finish;
                }
                for (int index = 0; index < times.Length; index++)
                    if (upper[index] > baseline[index]) baseline[index] = upper[index];
            }
            if (times.Length == 0)
            {
                p.SetPen(new QPen(new QColor("#94a3b8")));
                p.DrawText(left + 12, top + 30, I18n.T("stats.noData"));
            }
            DrawMarkers(p, left, right, top, bottom);
        }
        finally { p.End(); }
    }

    /// <summary>A recording's edges, drawn as annotation rather than as data: a hairline dashed in
    /// the recording's own colour, dashed so it never reads as the boundary of a fill, and faint
    /// enough that the series it crosses stay the subject. Each carries a small pill at the top of
    /// the plot — white enough to be read over any fill, outlined and lettered in the same colour.
    /// Both fade over the last stretch of the window, so a mark leaves the way it arrived instead
    /// of being cut off at the edge. Two marks that would overlap keep their lines and drop the
    /// label: the axis position is the information, the words are the convenience.</summary>
    private void DrawMarkers(QPainter p, int left, int right, int top, int bottom)
    {
        if (_markers.Count == 0) return;
        double fadeFrom = left + (right - left) * 0.12;
        using var labelFont = new QFont("Segoe UI", 8);
        using var metrics = new QFontMetrics(labelFont);
        var placed = new List<(int Left, int Right, int Row)>();
        foreach (var marker in _markers.OrderBy(mark => mark.Time))
        {
            if (marker.Time < _end.AddSeconds(-SpanSeconds) || marker.Time > _end) continue;
            int x = (int)Math.Round(left + Math.Clamp(1 - (_end - marker.Time).TotalSeconds / SpanSeconds, 0, 1) * (right - left));
            int alpha = x >= fadeFrom ? 255 : (int)Math.Clamp(255.0 * (x - left) / (fadeFrom - left), 0, 255);
            using var color = new QColor(marker.Color);
            using var hairline = new QColor(marker.Color);
            hairline.SetAlpha(Math.Min(alpha, 110));
            using var pen = new QPen(hairline);
            pen.SetStyle(Qt.PenStyle.DashLine);
            pen.SetWidthF(1.2);
            p.SetPen(pen);
            p.SetBrush(new QBrush(Qt.BrushStyle.NoBrush));
            p.DrawLine(x, top, x, bottom);

            string text = I18n.T(marker.LabelKey);
            int width = metrics.HorizontalAdvance(text) + 14, height = 16;
            // The label sits beside its line, on the side with room, and drops to a second row when
            // a mark nearby already claimed the first: two edges of one recording are close together
            // by nature, and their words are the part a reader came for — the line alone would say
            // where, not what.
            int pill = x + 5;
            if (pill + width > right) pill = x - 5 - width;
            pill = Math.Clamp(pill, left, Math.Max(left, right - width));
            int row = 0;
            while (row < 2 && placed.Any(other => other.Row == row && pill < other.Right + 6 && pill + width > other.Left - 6)) row++;
            if (row == 2) continue;
            placed.Add((pill, pill + width, row));
            int bodyTop = top + 3 + row * (height + 3);
            using var body = new QColor("#ffffff");
            body.SetAlpha(alpha * 5 / 6);
            p.SetPen(new QPen(Qt.PenStyle.NoPen));
            p.SetBrush(new QBrush(body));
            p.DrawRoundedRect(pill, bodyTop, width, height, 8, 8);
            color.SetAlpha(alpha);
            using var edge = new QPen(color);
            edge.SetWidthF(1.0);
            p.SetPen(edge);
            p.SetBrush(new QBrush(Qt.BrushStyle.NoBrush));
            p.DrawRoundedRect(pill, bodyTop, width, height, 8, 8);
            p.SetPen(new QPen(color));
            p.SetFont(labelFont);
            p.DrawText(pill + 7, bodyTop + height - 5, text);
        }
    }
}
