using PolyMount.Core;
using PolyMount.Core.Ipc;
using QtSharp.Api;
using QtSharp.Charts;
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
    private readonly QLabel _responseCost;
    private readonly Queue<(double Request, double Dispatch, double Ui, DateTime At)> _responseSamples = new();
    private readonly QComboBox _refresh, _range;
    private List<MountMetrics> _accessMetrics = new();
    /// <summary>The instance's answers about its own traffic, one per once-a-second reading, each
    /// covering the interval since the one before it. The section adds them up over the range the
    /// reader chose — the instance's own record is only a minute deep, so this is where a longer
    /// window comes from. See <see cref="AccessTotals"/>.</summary>
    private readonly List<(DateTime At, List<MountMetrics> Entries)> _accessHistory = new();
    /// <summary>When the last access reading's request went out, which is what the next answer's
    /// window starts from. <c>MinValue</c> means "no previous reading": its window is then the
    /// nominal second rather than however long the page has existed.</summary>
    private DateTime _lastAccessAt = DateTime.MinValue;
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
    /// <summary>Every card the page draws, in one list: what a frame of the movement between two
    /// samples hands a later moment to, all of them at that one moment.</summary>
    private readonly PerformanceChart[] _cards;
    private readonly QLabel _reconcile;
    /// <summary>The moment every card is drawn up to: the newest observation of the window the page
    /// samples at the reader's rate, and between samples whatever moment the movement of the picture
    /// has reached — see <see cref="Animate"/>. The other two histories are read at cadences of their
    /// own — what the layers hold once a second, what the process costs with every request — and a
    /// card fed by one of them used to be drawn against that history's own newest row, so the six
    /// cards disagreed about where "now" was by up to a second and drifted apart as the cadences
    /// drifted. A series whose own rows stop short of this moment ends there; see
    /// PerformanceChart.Windowed.</summary>
    private DateTime _clock = DateTime.UtcNow;
    private readonly PerformanceChart[] _processCharts;
    /// <summary>What the instance's own process moved, read and written: the operating system's
    /// account of it, which is what its counters are for. It shares the cards' unit and shape but
    /// not their feed — the rates come from the counters rather than from a layer — and its two
    /// directions overlap rather than stack: each is drawn from the zero line, so where both are
    /// present the reader sees the two colours mixed, and nothing is added into a total that is
    /// neither of them.</summary>
    private readonly PerformanceChart _processIo;
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
    /// <summary>How many points one series is drawn with. A long range holds more observations
    /// than a screen can show, and past this the drawing folds them into buckets - the figures
    /// still come from every observation.</summary>
    private const int MaxPoints = 1200;
    /// <summary>How far the picture may move between two frames of the movement between samples,
    /// in pixels. Two is where a step sideways starts to be visible as one, so it is both the
    /// reason to draw a frame and the size of the step: a sample that moves the curve less than
    /// this does not need anything drawn between it and the last one.</summary>
    private const double AnimationPixels = 2;
    /// <summary>Draws every card on the page whether or not it is on screen, for the checks and the
    /// pictures: what they are about is the cards, not what the reader happens to have scrolled to.
    /// </summary>
    internal void DrawEverything()
    {
        foreach (var card in _charts.Concat(_processCharts).Append(_heldMemory).Append(_heldDisk).Append(_processIo))
            card.DrawAnyway();
        Render();
    }

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
    private static readonly (double RefreshHz, double RangeSeconds, Dictionary<string, bool> Sections, bool AccessAligned) Remembered = LoadStats();
    /// <summary>Which of the sections under the charts the reader left open, by the key each is
    /// remembered under. Folded is where a section starts: a reader who has never touched one is
    /// shown the charts and three lines saying what is under them, which is the page's subject and
    /// nothing else.</summary>
    private readonly Dictionary<string, bool> _sections = new(Remembered.Sections);
    /// <summary>The height of one row of the layer table, which the table is sized from.</summary>
    private const int TableRowHeight = 36;
    private List<MountMetrics> _latest = new();
    /// <summary>What one request is carrying back. <c>At</c> is the moment it went out: the moment
    /// the windows it covers end, and the moment an observation from it is stamped with.</summary>
    private Task<(List<MountMetrics>? Trends, List<MountMetrics>? Access, List<HeldRow>? Held, ProcessReading Process, double RequestMs, long Completed, double Seconds, DateTime At)>? _pending;
    private bool _paused, _requested;
    private int _epoch, _requestEpoch;
    /// <summary>When the next sample is due, and when the next once-a-second reading — the access
    /// window, what the layers hold, the process — is due. Both are moments on a lattice rather
    /// than a countdown from the tick that happened to carry the last one; see <see cref="NextDue"/>.
    /// <c>MinValue</c> means there is no schedule yet: a page starting, resuming, or following a rate
    /// the reader just changed.</summary>
    private DateTime _next, _nextSecond;
    /// <summary>When the last accepted sample's request went out, which is what the next window
    /// starts from. <c>MinValue</c> means "no previous sample": the next window is the nominal
    /// interval.</summary>
    private DateTime _lastSample = DateTime.MinValue;
    /// <summary>How often the charts are refreshed, in samples per second.</summary>
    private double _refreshHz = Remembered.RefreshHz, _rangeSeconds = Remembered.RangeSeconds;
    /// <summary>Whether the access cards are drawn with the layers in columns or one bar per
    /// caller. Both are readings of the same table, so the choice is the reader's and is
    /// remembered like the rest of this page's.</summary>
    private bool _accessAligned = Remembered.AccessAligned;
    private static string T(string key) => I18n.T(key);

    public StatsWindow(string? instanceName = null)
    {
        _instanceName = instanceName;
        _client = instanceName == null ? SupervisorClient.Default : InstanceDirectory.ClientFor(instanceName);
        _process = new InstanceProcess(instanceName);
        SetObjectName("dashboard");
        
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
        I18n.OnChanged(() => heading.SetText(instanceName == null ? T("stats.heading") : I18n.F("stats.instance", instanceName)), this);
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
            // Take effect now rather than at the end of the interval that was already running —
            // both schedules together, because they share the moment they are anchored on. See
            // RestartSchedules.
            RestartSchedules();
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
            // The access section is a window of this page's own answers too, so it is cut to the
            // new range and added up again from what is left — the range is what it is drawn over,
            // not something asked of the instance.
            _accessHistory.RemoveAll(entry => entry.At < cutoff);
            _accessMetrics = AccessTotals.Sum(_accessHistory, _clock, _rangeSeconds);
            _latest = _accessMetrics;
            // The range is a property of the drawing, so what it redraws is the readings already in
            // hand: asking for a fresh sample to redraw with restarts the trend schedule and leaves
            // the once-a-second one where it was, and from then on every moment the two are each due
            // becomes two requests — a page whose reader changed the window and got twice the
            // sampling they had chosen. See VerifyRangeKeepsCadence.
            Render();
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
        _heldMemory = new PerformanceChart("stats.layerHeld.memory", "#7c3aed", "B", ChartSummary.Gauge);
        _heldDisk = new PerformanceChart("stats.layerHeld.disk", "#c026d3", "B", ChartSummary.Gauge);
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
        _accessPie.SetAligned(_accessAligned);
        // Switching the drawing is a choice like the refresh rate: written down the moment it is
        // made, so the page opens the way the reader left it.
        _accessPie.ModeChanged += () => { _accessAligned = _accessPie.Aligned; SaveStats(); };
        access.AddWidget(_accessPie, 1);
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

        // The machine's own account of the instance, next to VFS's account of it. The unit of these
        // is the process: one instance is one process, so nothing has to be attributed to a mount
        // for them to be per instance.
        _processBody = new QWidget();
        var processBody = new QVBoxLayout(_processBody);
        processBody.SetContentsMargins(0, 0, 0, 0);
        processBody.SetSpacing(12);
        // Levels, so their cards report what the window averaged and peaked at and not what its
        // samples add up to: a level summed over a window is a number that grows with how often the
        // page looked, which is the refresh rate.
        _processCharts = new[]
        {
            new PerformanceChart("stats.process.cpu", "#0f766e", "stats.unit.cores", ChartSummary.Gauge),
            new PerformanceChart("stats.process.memory", "#b45309", "B", ChartSummary.Gauge),
            new PerformanceChart("stats.process.threads", "#475569", "", ChartSummary.Gauge),
            new PerformanceChart("stats.process.handles", "#64748b", "", ChartSummary.Gauge)
        };
        _processIo = new PerformanceChart("stats.process.moved", "#475569", "B", ChartSummary.Volume);
        // Every card the page draws, in one list: what the movement between two samples has to hand
        // a later moment to. The page does not draw them one at a time — it draws them at one
        // moment, which is what stops the same instant landing a second apart on two of them.
        _cards = _charts.Concat(new[] { _heldMemory, _heldDisk }).Concat(_processCharts).Append(_processIo).ToArray();
        var processGrid = new QGridLayout();
        processGrid.SetSpacing(16);
        for (int i = 0; i < _processCharts.Length; i++) processGrid.AddWidget(_processCharts[i], i / 2, i % 2);
        // Its own row, across both columns: it is the one card here that is a rate rather than a
        // level, and two overlapping areas need the width to be told apart.
        processGrid.AddWidget(_processIo, _processCharts.Length / 2, 0, 1, 2);
        processBody.AddLayout(processGrid);
        _processFigures = new QLabel();
        _processFigures.SetObjectName("muted");
        _processFigures.SetWordWrap(true);
        _processFigures.SetTextFormat(Qt.TextFormat.PlainText);
        processBody.AddWidget(_processFigures);

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
        // A page that was already open has been watching throughout — what is on screen is the last
        // minute of it — so there is no gap to make up for and this only asks for what is happening
        // now rather than waiting out the interval that is already running.
        _requested = true;
    }

    /// <summary>Whether this page is watching its instance: not paused, and its timer running. For
    /// the smoke run, which closes and opens a tab inside one turn of the event loop and has no
    /// second to wait for a sample it could count instead.</summary>
    internal bool Watching => !_paused && _timer.IsActive();

    /// <summary>Self-check: a page that is not on screen keeps watching. The smoke run steps to
    /// another tab, waits a couple of seconds and steps back; a page that pauses when it is not on
    /// show comes back to a hole where the time it was away should be, which is what its reader sees
    /// as a line that stops and starts again.</summary>
    internal void VerifyWatchedWhileHidden(DateTime from, DateTime until)
    {
        var samples = _history.GetValueOrDefault(_instanceName ?? "") ?? new();
        int readings = samples.Count(sample => sample.Time > from && sample.Time < until);
        if (readings == 0)
            throw new InvalidOperationException(
                $"Nothing was taken down in the {(until - from).TotalSeconds:0.#} seconds this page's tab was not on show");
    }

    /// <summary>How many process readings the page has taken. One is taken per request, so this is
    /// the page's own count of how often it looked — which is what the smoke run counts.</summary>
    internal int ProcessReadings => _processHistory.Count;

    /// <summary>The rate the page is sampling at, for the smoke run to put back afterwards.</summary>
    internal double SamplingRate => _refreshHz;

    /// <summary>Sets the sampling rate the way the reader's picker does, for the smoke run.</summary>
    internal void SmokeSamplingRate(double rate)
    {
        int index = Array.IndexOf(RefreshRates, rate);
        if (index >= 0) _refresh.SetCurrentIndex(index);
    }

    /// <summary>Picks a different range the way the reader's picker does, for the smoke run, and
    /// answers the index it had so the run can put the reader's choice back.</summary>
    internal int SmokeChangeRange()
    {
        int before = _range.CurrentIndex;
        _range.SetCurrentIndex((Math.Max(before, 0) + 1) % RangeLadder.Length);
        return before;
    }

    internal void SmokeRestoreRange(int index)
    {
        if (index >= 0 && index < RangeLadder.Length) _range.SetCurrentIndex(index);
    }

    /// <summary>Self-check: changing the range changes how much of the line is drawn, not how often
    /// the page takes a reading. The process cards take a reading per request, so the count here is
    /// the page's own request cadence — and one range change that also asked for a sample was enough
    /// to leave the trend schedule and the once-a-second one anchored a fraction of a second apart,
    /// after which every moment they were both due became two requests. A reader saw that as the IO
    /// card sampling faster than the rate they had chosen.</summary>
    internal void VerifyRangeKeepsCadence(int readingsWhenChanged, DateTime changedAt)
    {
        double seconds = (DateTime.UtcNow - changedAt).TotalSeconds;
        // The once-a-second readings take a process reading with them even under a slower chart
        // rate, so that is the floor the page is expected to hold — not the rate alone.
        double asked = Math.Max(1, _refreshHz) * seconds;
        int readings = _processHistory.Count - readingsWhenChanged;
        if (readings > asked + 1.5)
            throw new InvalidOperationException(
                $"Changing the range made the page take {readings} readings in {seconds:0.#} s, "
                + $"where the rate it is set to asks for {asked:0.#}");
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
        // Only the cards being drawn: one outside the viewport or inside a folded section is not
        // rebuilt at all, on purpose, and "drawing nothing" is what that looks like from here.
        foreach (var chart in _processCharts.Append(_processIo).Where(chart => chart.InSight))
        {
            if (chart.Figures.Count == 0) throw new InvalidOperationException("A process card carries no figures");
            if (chart.Series.Count == 0) throw new InvalidOperationException($"{chart.TitleKey} is drawing nothing");
        }
        if (_processHistory.Count == 0) throw new InvalidOperationException("No process reading was taken");
        // The history is bounded by time, so how many rows that is depends on the refresh rate:
        // what must hold is that it reaches back over the window its charts are drawn against, and
        // that the only row outside it is the one the window's left edge falls inside.
        VerifyHistory("The process history", _processHistory, second => second.Time);
        if (_process.Processes == 0) throw new InvalidOperationException("The instance reads as having no process behind it");
        var last = _processHistory[^1];
        if (last.WorkingSet <= 0 || last.Threads <= 0 || last.Handles <= 0)
            throw new InvalidOperationException($"A process reading came back empty: {last}");
        // The line under the cards is the section's own summary — how many processes, what they
        // cost and what they hold — so it is the working set it has to be reporting.
        if (!_processFigures.Text.Contains(ByteUnits.Format(last.WorkingSet), StringComparison.Ordinal))
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
        foreach (var chart in Cards)
            if (chart.Height < chart.MinimumHeight || chart.Width < chart.MinimumWidth)
                throw new InvalidOperationException(
                    $"{chart.TitleKey} is {chart.Width}x{chart.Height}, under its own " +
                    $"{chart.MinimumWidth}x{chart.MinimumHeight} minimum");
        // And inside each card, what the chart did with the rectangle it was given: the plot is what
        // is left of it after the two axes and the legend, and what is left can be nothing. Measured
        // while the card drew its legend row by hand, one card's plot came out 286 by 30 pixels
        // inside a card of 330 by 260 with every other check on this page passing. So both halves of
        // that are checked: the share of the plot the data got, and whether the chart had to elide
        // an axis label to fit — which turns a column of numbers into a column of dots, and is the
        // one way a chart reports a plot too short for what it was asked to draw.
        foreach (var chart in Cards)
        {
            var areas = chart.Areas;
            if (areas.WidthShare < 0.7 || areas.HeightShare < 0.45)
                throw new InvalidOperationException($"{chart.TitleKey} keeps only {areas} for its data");
            if (chart.LabelsTruncated)
                throw new InvalidOperationException(
                    $"{chart.TitleKey} had to elide its value labels to fit its plot: {areas}");
        }
        // The access section's cards are of the same kind and squeeze the same way. What a squeeze
        // does to them is worse than to a chart: a circle below its own width is one whose legend has
        // been cut, and a row of bars squeezed under its names is one whose numbers have been — so
        // they are two to a row rather than four.
        foreach (var (title, width, height, minWidth, minHeight) in _accessPie.Cards)
            if (width < minWidth || height < minHeight)
                throw new InvalidOperationException(
                    $"The '{title}' access card is {width}x{height}, under its own {minWidth}x{minHeight} minimum");
    }

    /// <summary>Every chart card on this page, in the order the page lays them out.</summary>
    private IEnumerable<PerformanceChart> Cards =>
        _charts.Concat(_processCharts).Append(_processIo).Append(_heldMemory).Append(_heldDisk);
    /// <summary>The layers' account and the operating system's are two measurements of different
    /// things — the sizes of the files the layers resolved, against every byte the process moved,
    /// whichever part of it asked — so they are drawn on two cards rather than stacked into one.
    /// Stacked, the card said one total in its header (the layers') and another in its picture (the
    /// operating system's), and the difference was drawn as if it were a layer.
    ///
    /// What is checked here is that each card is one account: no byte card carries the other one,
    /// and the card that carries the process's own numbers draws both directions of them, at their
    /// own value rather than summed.
    /// </summary>
    internal void VerifyByteAccount()
    {
        foreach (var chart in new[] { _charts[1], _charts[3] })
            if (chart.Series.Any(item => item.Name == T("stats.layerUnknown")))
                throw new InvalidOperationException(
                    $"The byte card under {chart.TitleKey} is mixing the process's own account into the layers' stack");
        var io = _processIo.Series.ToArray();
        if (io.Length != 2 || io[0].Name != T("stats.process.moved.read") || io[1].Name != T("stats.process.moved.write"))
            throw new InvalidOperationException(
                "The process's own bytes are not drawn as two directions: " + string.Join(", ", io.Select(item => item.Name)));
        if (io.Any(item => item.Stacked))
            throw new InvalidOperationException("The process's reads and writes are stacked, which adds them into a total neither of them is");
        foreach (var (series, rates) in new[] { (io[0], TransferRates(reads: true)), (io[1], TransferRates(reads: false)) })
        {
            if (rates.Count == 0 || series.Samples.Count == 0)
                throw new InvalidOperationException($"The card of the process's own bytes is not drawing {series.Name}");
            double drawn = series.Samples[^1].Value, moved = rates[^1].Rate;
            if (Math.Abs(drawn - moved) > Math.Max(1, moved * 0.001))
                throw new InvalidOperationException(
                    $"{series.Name} reads {drawn:0.#} where the process's counters last said {moved:0.#}");
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
    /// <summary>How many callers the access cards name before the rest are folded into one row. A
    /// card is 250 px tall and a row wants up to 26 of them, so seven rows is what fits — the named
    /// callers, the fold's own row, and the service's unattributed one.</summary>
    private const int AccessNamedCallers = 5;

    /// <summary>The access section's two readings of one window: the layers' own counts, which the
    /// circle is drawn from, and the same counts split by the caller that asked for them, which the
    /// rows beside it are drawn from. Built here rather than inside the render loop so that the
    /// smoke run can put a fixture through it and add the rows up against the circle — agreeing is
    /// the one property this section exists for.
    /// </summary>
    internal static (List<AccessSlice> Layers, List<AccessRow> Callers) AccessSlices(
        IReadOnlyList<MountMetrics> metrics, string selected)
    {
        var chosen = metrics.Where(m => selected.Length == 0 || m.Mount == selected).ToList();
        // Which colour a layer is drawn in, so that a bar's pieces are the circle's slices by more
        // than their names: one layer keeps one colour on both sides of the card.
        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in chosen.SelectMany(m => m.Layers))
            if (!colors.ContainsKey(layer.Name))
                colors[layer.Name] = string.IsNullOrEmpty(layer.Color) ? "#64748B" : layer.Color;

        var layers = chosen.SelectMany(m => m.Layers.Select(layer => (Mount: m.Mount, Layer: layer)))
            .Select(row => new AccessSlice(
                selected.Length == 0 ? row.Mount + "/" + row.Layer.Name
                    : string.IsNullOrEmpty(row.Layer.DisplayName) ? row.Layer.Name : row.Layer.DisplayName,
                string.IsNullOrEmpty(row.Layer.Color) ? "#64748B" : row.Layer.Color,
                row.Layer.Hits, row.Layer.Writes))
            .ToList();

        // Who asked, over the same window, ranked and folded together: one application may be in two
        // mounts' answers, so the fold is over the merged list rather than per instance — which is
        // also why Core sends counts rather than shares for these. Ranked by what the layers did on
        // a caller's behalf rather than by the requests it made, because that is what the bars are
        // drawn from: a fold keeping the busiest callers by requests could drop the busiest by layer
        // traffic, which is the row the card is here to show.
        var callers = ProcessFold.Top(
            chosen.SelectMany(m => m.Processes).ToList(),
            AccessNamedCallers,
            row => row.ByLayer.Sum(part => part.Reads + part.Writes));
        var rows = callers.Select(row => new AccessRow(
            row.Other ? I18n.T("stats.access.other")
                : row.Name.Length > 0 ? row.Name : I18n.F("stats.process.pid", row.Pid),
            Parts(row.ByLayer, colors))).ToList();
        // What nobody asked for: the read-ahead and the warm-up the service did for itself. It is a
        // row like any other here, because the circle beside these rows counts that work — it was
        // in the layers' account and in no process's, which is the whole of why the two circles this
        // section used to draw could not be added up against each other.
        var unattributed = Parts(chosen.SelectMany(m => m.Unattributed), colors);
        if (unattributed.Count > 0)
            rows.Add(new AccessRow(I18n.T("stats.process.unattributed"), unattributed));
        return (layers, rows.OrderByDescending(row => row.Parts.Sum(part => part.Reads + part.Writes)).ToList());
    }

    /// <summary>The pieces of one bar: what each layer did for one caller, merged across the mounts
    /// that answered for it, in the colours the circle beside it uses.</summary>
    private static List<AccessPart> Parts(IEnumerable<ProcessLayerCount> counts, Dictionary<string, string> colors)
    {
        var merged = new Dictionary<string, (long Reads, long Writes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var count in counts)
        {
            merged.TryGetValue(count.Name, out var held);
            merged[count.Name] = (held.Reads + count.Reads, held.Writes + count.Writes);
        }
        return merged
            .OrderByDescending(entry => entry.Value.Reads + entry.Value.Writes)
            .Select(entry => new AccessPart(entry.Key,
                colors.TryGetValue(entry.Key, out var color) ? color : "#64748B",
                entry.Value.Reads, entry.Value.Writes))
            .ToList();
    }

    /// <summary>What the access section is for, pinned: the rows beside a circle are the same
    /// traffic as the circle, split by who asked, so every layer's row counts have to add up to that
    /// layer's slice — the service's own work included. That last row is the one that used to be
    /// missing, and without it the two ways of reading the window could not be checked against each
    /// other at all.</summary>
    internal void VerifyAccessSplit()
    {
        var fixture = new MountMetrics
        {
            Mount = "m",
            Layers = new()
            {
                new() { Name = "cache", Color = "#0891b2", Hits = 300, Writes = 20 },
                new() { Name = "svn", Color = "#f59e0b", Hits = 40 }
            },
            Processes = new()
            {
                new()
                {
                    Pid = 11, Name = "Code.exe", Reads = 200, Writes = 20,
                    ByLayer = new() { new() { Name = "cache", Reads = 160, Writes = 20 }, new() { Name = "svn", Reads = 40 } }
                },
                new()
                {
                    Pid = 22, Name = "explorer.exe", Reads = 100,
                    ByLayer = new() { new() { Name = "cache", Reads = 100 } }
                }
            },
            Unattributed = new() { new() { Name = "cache", Reads = 40 } }
        };
        var (layers, callers) = AccessSlices(new[] { fixture }, "");
        if (callers.Count != 3) throw new InvalidOperationException($"The access card drew {callers.Count} rows for two callers and the service's own work");
        foreach (var layer in fixture.Layers)
        {
            long reads = callers.Sum(row => row.Parts.Where(part => part.Name == layer.Name).Sum(part => part.Reads));
            long writes = callers.Sum(row => row.Parts.Where(part => part.Name == layer.Name).Sum(part => part.Writes));
            if (reads != layer.Hits || writes != layer.Writes)
                throw new InvalidOperationException(
                    $"The rows account for {reads} reads and {writes} writes on {layer.Name} where the circle says "
                    + $"{layer.Hits} and {layer.Writes}");
        }
        long drawn = callers.Sum(row => row.Parts.Sum(part => part.Reads + part.Writes));
        long circled = layers.Sum(slice => slice.Reads + slice.Writes);
        if (drawn != circled)
            throw new InvalidOperationException($"The rows add up to {drawn} against a circle of {circled}");
        // The colour a piece is drawn in is the colour its slice is drawn in, or the bar says one
        // thing and the legend beside it another.
        foreach (var part in callers.SelectMany(row => row.Parts))
        {
            string expected = fixture.Layers.Single(layer => layer.Name == part.Name).Color;
            if (part.Color != expected)
                throw new InvalidOperationException($"{part.Name} is drawn {part.Color} in the rows and {expected} in the circle");
        }
    }

    /// <summary>Self-check: the table and the two drawings cover the range the reader chose, not a
    /// fixed minute the instance keeps. Two answers ten seconds apart: a window that holds only the
    /// newer one counts only its traffic, and one that holds both counts their sum — and the
    /// section's own invariant survives that, the caller rows still adding up to the circle.</summary>
    internal void VerifyAccessWindow()
    {
        var at = new DateTime(2026, 1, 1, 0, 0, 30, DateTimeKind.Utc);
        static MountMetrics Answer(long reads, long writes) => new()
        {
            Mount = "m",
            WindowSeconds = 1,
            TotalReads = reads,
            TotalWrites = writes,
            Layers = new() { new LayerMetric { Name = "cache", Color = "#0891b2", Hits = reads, Writes = writes } },
            Processes = new()
            {
                new ProcessMetric
                {
                    Pid = 7,
                    Name = "tool.exe",
                    Reads = reads,
                    Writes = writes,
                    ByLayer = new() { new ProcessLayerCount { Name = "cache", Reads = reads, Writes = writes } }
                }
            }
        };
        var history = new List<(DateTime At, List<MountMetrics> Entries)>
        {
            (at.AddSeconds(-10), new() { Answer(10, 2) }),
            (at, new() { Answer(4, 0) })
        };

        var recent = AccessTotals.Sum(history, at, 5);
        var whole = AccessTotals.Sum(history, at, 30);
        if (recent.Count != 1 || recent[0].TotalReads != 4 || recent[0].Layers[0].Hits != 4 ||
            recent[0].Layers[0].Writes != 0)
            throw new InvalidOperationException("A window shorter than the oldest answer counted more than the answer it holds");
        if (whole.Count != 1 || whole[0].TotalReads != 14 || whole[0].Layers[0].Hits != 14 ||
            whole[0].Layers[0].Writes != 2)
            throw new InvalidOperationException("A window holding both answers did not count their sum");
        if (whole[0].MeasuredSeconds > 30 || whole[0].MeasuredSeconds < 10)
            throw new InvalidOperationException(
                $"A window of two answers ten seconds apart reports {whole[0].MeasuredSeconds:0.#} seconds watched");
        var (layers, callers) = AccessSlices(whole, "");
        long drawn = callers.Sum(row => row.Parts.Sum(part => part.Reads + part.Writes));
        long circled = layers.Sum(slice => slice.Reads + slice.Writes);
        if (drawn != circled)
            throw new InvalidOperationException($"A summed window's rows add up to {drawn} against a circle of {circled}");

        // And the page's own account, which is what the section is drawn from: a page that has been
        // watching for seconds must have kept every answer it was given.
        if (_accessHistory.Count == 0)
            throw new InvalidOperationException("The access section kept none of the answers it was given");
    }

    /// <summary>The access cards are read two ways and the heading over them is the switch. What is
    /// checked is the whole of that path: the click raises the switch, every card follows, the page
    /// takes the drawing the cards are actually using (so it can write it down), and the way back is
    /// the way it came.</summary>
    internal void VerifyAccessModes()
    {
        bool before = _accessPie.Aligned;
        _accessPie.SmokeToggle();
        if (_accessPie.Aligned == before)
            throw new InvalidOperationException("The access cards' switch did not change the drawing");
        if (_accessAligned != _accessPie.Aligned)
            throw new InvalidOperationException(
                $"The page kept {_accessAligned} while the cards are drawing {_accessPie.Aligned}");
        _accessPie.SmokeToggle();
        if (_accessPie.Aligned != before || _accessAligned != before)
            throw new InvalidOperationException("Switching the access drawing twice did not come back to where it started");
    }

    /// <summary>How the access card lays its columns out, held to by arithmetic rather than by
    /// looking at the card. Each of these was a rule the picture broke only once someone looked at
    /// it: a layer that served four requests in twenty thousand was given a column wide enough to
    /// read as a real one, because the widths had a minimum; and a column's cells, a column's
    /// heading and the layers' order all hang off numbers nothing was checking.</summary>
    internal void VerifyAccessGrid()
    {
        static AccessRow Row(string name, params (string Layer, long Reads, long Writes)[] parts) =>
            new(name, parts.Select(part => new AccessPart(part.Layer, "#123456", part.Reads, part.Writes)).ToList());
        var rows = new List<AccessRow>
        {
            // The shape from a real window: two layers that carry the traffic and a third that
            // answered four requests out of twenty thousand.
            Row("XEditorProxy_x64h", ("svn", 10155, 0), ("memory-cache", 9722, 0), ("cache", 4, 0)),
            Row("second", ("svn", 60, 0), ("memory-cache", 40, 0))
        };
        const int left = 100, span = 400;
        var columns = AccessGrid.Columns(rows, reads: true, left, span);
        var svn = columns.Single(column => column.Name == "svn");
        var memory = columns.Single(column => column.Name == "memory-cache");
        var cache = columns.Single(column => column.Name == "cache");

        // 0.0% takes no room: a column is a share of the traffic, not a slot.
        if (cache.Width != 0 || cache.Drawn)
            throw new InvalidOperationException($"A layer with 4 of 19,981 was given {cache.Width} px of the card");
        if (cache.Length(4) != 0)
            throw new InvalidOperationException("A cell was drawn in a column that has no room");

        // The columns that are drawn are their shares of the room the gaps leave, within a pixel.
        int room = span - AccessGrid.Gap * (columns.Count - 1);
        foreach (var (column, sum) in new[] { (svn, 10215L), (memory, 9762L) })
        {
            int expected = (int)Math.Round(room * (double)sum / 19981);
            if (Math.Abs(column.Width - expected) > 1)
                throw new InvalidOperationException(
                    $"{column.Name} is {column.Width} px wide where its share of the card is {expected}");
        }

        // Every row's cell for a layer starts at that layer's column, which is what aligned means.
        if (memory.Left != svn.Left + svn.Width + AccessGrid.Gap)
            throw new InvalidOperationException(
                $"memory-cache starts at {memory.Left} after svn ends at {svn.Left + svn.Width}");

        // A column is read against its own busiest row: that row fills it, half of it is half, and
        // a row that used none of it is not drawn.
        if (svn.Length(10155) != svn.Width)
            throw new InvalidOperationException($"The busiest row fills {svn.Length(10155)} of svn's {svn.Width} px");
        if (Math.Abs(svn.Length(60) - svn.Width * 60.0 / 10155) > 1)
            throw new InvalidOperationException($"60 of 10,155 is drawn {svn.Length(60)} px wide of {svn.Width}");
        if (svn.Length(0) != 0)
            throw new InvalidOperationException("A row that used none of a layer drew a cell");

        // Two layers that carried the same amount keep one order, so a sample that swaps them does
        // not also swap their columns.
        var tied = AccessGrid.Columns(new[] { Row("r", ("zeta", 5, 0), ("alpha", 5, 0)) }, true, left, span);
        if (tied[0].Name != "alpha" || tied[1].Name != "zeta")
            throw new InvalidOperationException("Layers that carried the same amount changed places: " + string.Join(", ", tied.Select(c => c.Name)));

        // The stacked drawing's pieces add up to its own bar, in the columns' order.
        var pieces = AccessGrid.Stacked(columns, rows[0], reads: true, barWidth: 400);
        if (pieces.Sum() != 400)
            throw new InvalidOperationException($"The stacked pieces add up to {pieces.Sum()} for a bar of 400");
        int expectedFirst = (int)Math.Round(400.0 * 10155 / 19881);
        if (Math.Abs(pieces[0] - expectedFirst) > 1)
            throw new InvalidOperationException($"The first piece is {pieces[0]} px of a 400 px bar, not {expectedFirst}");

        // The direction the card reads decides the numbers, so a writes card is not a reads card.
        if (AccessGrid.Total(rows[0], reads: false) != 0 || AccessGrid.Total(rows[0], reads: true) != 19881)
            throw new InvalidOperationException("The two directions read the same numbers");
        var writes = AccessGrid.Columns(new[] { Row("r", ("svn", 0, 7), ("cache", 0, 3)) }, reads: false, left, span);
        if (writes.Count != 2 || writes[0].Name != "svn")
            throw new InvalidOperationException("A writes card is not reading the writes");
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
        // A series is cut to the window at its left edge, whichever cadence fed it, and at the right
        // it is drawn no further than it was read. At the left the value on the edge is the one the
        // line has there, interpolated between the two rows that straddle it — moving the older row
        // to the edge and keeping its value stretched that segment and made its end jump once per
        // sample, which is what a reader saw as the left of the line moving. At the right a series
        // that stops short of the moment the page is drawn up to stops there: the part of the plot
        // after its last reading is a part nothing was read for.
        var cut = PerformanceChart.Windowed(new List<(DateTime Time, double Value)>
        {
            (start.AddSeconds(-4), 0.0), (start.AddSeconds(16), 10.0), (start.AddSeconds(26), 20.0)
        }, start.AddSeconds(30), 30);
        if (cut.Count != 3 || cut[0].Time != start || Math.Abs(cut[0].Value - 2.0) > 1e-9 ||
            cut[^1].Time != start.AddSeconds(26) || cut[^1].Value != 20.0)
            throw new InvalidOperationException(
                "A series is not cut to its window: " + string.Join(", ", cut.Select(sample => $"{sample.Value:0.###} at {(sample.Time - start).TotalSeconds:0.###}s")));
        // The time axis has to name its span and its ticks in the unit each makes readable: a
        // fifteen-minute range divided into hundred-second steps would be unreadable, and a step of
        // a minute or more is a duration nobody counts in seconds. The step ladder and the unit are
        // what the card decides; where a tick lands and where its label sits is the chart's own
        // layout, from the range, the interval and the format it was handed.
        if (PerformanceChart.TimeStep(900 / 8.0) != 120 || PerformanceChart.TimeStep(12) != 15 ||
            PerformanceChart.TimeUnit(120) != (60, "%.0f m") || PerformanceChart.TimeUnit(15) != (1, "%.0f s"))
            throw new InvalidOperationException("The time axis does not name its ticks in the unit that fits them");
        if (!PerformanceChart.SpanText(900).Contains("15", StringComparison.Ordinal) ||
            !PerformanceChart.SpanText(30).Contains("30", StringComparison.Ordinal))
            throw new InvalidOperationException("The axis caption does not name the span it is drawn over");
        // A schedule is a lattice: the sample after a late tick is due when it always was, not one
        // interval after the tick that happened to carry its predecessor. What that fixes is the
        // cadence a reader sees — a hundred-millisecond poll quantized to 109 makes a page asked for
        // one hertz sample at 0.92, and every vertex on a chart drifts left of the second it belongs
        // to by a tenth of a second per second of age. A slot the page was not there for is skipped
        // rather than caught up: a burst of samples covering tenths of a second would bill traffic
        // to windows that never saw it.
        DateTime slot = start.AddSeconds(7.5);
        if (NextDue(DateTime.MinValue, start, 1) != start.AddSeconds(1) ||
            NextDue(slot, start.AddSeconds(8.1), 1) != start.AddSeconds(8.5) ||
            NextDue(slot, start.AddSeconds(8.6), 1) != start.AddSeconds(9.5) ||
            NextDue(slot, start.AddSeconds(11.6), 4) != start.AddSeconds(15.5))
            throw new InvalidOperationException(
                "A schedule is counted from the tick that carried the last sample rather than from its own lattice");
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
        // Two series drawn beside each other reach the taller of them, not their sum: scaled to
        // the sum, the taller one would stop short of the top of the plot. So a shorter series
        // drawn beside a taller one must leave the axis exactly as it was.
        var taller = ChartDomain.Of(new List<ChartSeries>
        {
            new("read", "#059669", new() { (time, 40960.0) }) { Stacked = false }
        }, "B", 2.1);
        var overlaid = ChartDomain.Of(new List<ChartSeries>
        {
            new("read", "#059669", new() { (time, 40960.0) }) { Stacked = false },
            new("write", "#0891b2", new() { (time, 20480.0) }) { Stacked = false }
        }, "B", 2.1);
        if (overlaid.Max != taller.Max || taller.Max <= 0)
            throw new InvalidOperationException(
                $"Two overlaid series scale the axis to {overlaid.Max} where the taller of them alone scales it to {taller.Max}");
    }

    /// <summary>Smoke check: the chart summaries describe their samples, the charts carry the
    /// figures their metric is described by, and the layer table fits the rows it holds. None of
    /// it needs a running service.</summary>
    /// <summary>Self-check for the charts module the cards are drawn with. It is a package of its
    /// own — the binding, its native glue and Qt6Charts.dll beside the ones the core binding ships —
    /// and a module that failed to load draws nothing rather than throwing, so what it drew is read
    /// back here: a chart built off-screen, and the ink that came out of it.</summary>
    internal void VerifyChartsModule(string? capture = null)
    {
        using var series = new QtSharp.Charts.QLineSeries();
        series.SetName("module");
        series.Append(0.0, 1.0);
        series.Append(1.0, 4.0);
        series.Append(2.0, 2.0);
        if (series.Count() != 3) throw new InvalidOperationException("The charts module kept none of the points it was given");
        using var chart = new QtSharp.Charts.QChart();
        chart.AddSeries(series);
        chart.CreateDefaultAxes();
        if (chart.Axes().Length != 2)
            throw new InvalidOperationException($"A chart built its own axes as {chart.Axes().Length} axes rather than two");
        using var view = new QtSharp.Charts.QChartView(chart);
        view.Resize(240, 160);
        using var painted = view.Grab();
        if (painted.Width() != 240 || painted.Height() != 160)
            throw new InvalidOperationException($"A chart rendered {painted.Width()}x{painted.Height()} in a 240x160 view");
        // A blank image is what a module that did not really load produces: the widget is there, the
        // pen never reaches the canvas. The stroke is the series' own colour, so the count of
        // pixels that are not the background is what is asked for.
        using var rendered = painted.ToImage().ConvertToFormat(QImage.Format.Format_ARGB32);
        int ink = 0;
        for (int y = 0; y < rendered.Height(); y++)
            for (int x = 0; x < rendered.Width(); x++)
            {
                using var pixel = rendered.PixelColor(x, y);
                if (pixel.Alpha() > 0 && pixel.Red() + pixel.Green() + pixel.Blue() < 720) ink++;
            }
        if (capture != null) painted.Save(capture);
        if (ink < 100)
            throw new InvalidOperationException($"A chart drew {ink} pixels of ink, which is nothing a line through three points would leave");
    }

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
        // A level is not an amount either: cores in use, bytes held, threads and handles open are
        // read once and read again, and added up they are a number that grows with the refresh rate.
        foreach (var gauge in _processCharts.Append(_heldMemory).Append(_heldDisk))
            if (gauge.Figures.Contains(ChartFigure.Total))
                throw new InvalidOperationException($"{gauge.TitleKey} totals a level: a level summed over a window is not an amount of anything");
        // A card too narrow for every figure keeps the first ones instead of clipping one.
        if (PerformanceChart.Fitted(new[] { 0, 60, 120, 180 }, 150) != 2 ||
            PerformanceChart.Fitted(new[] { 0, 60, 120, 180 }, 180) != 3 ||
            PerformanceChart.Fitted(new[] { 0, 60, 120, 180 }, 59) != 0)
            throw new InvalidOperationException("Chart figures do not degrade by importance when the card is narrow");
        // A pause in the readings splits a series into separate items so that no line is drawn
        // across it — and the legend names every item, so a hole the width of a backend restart
        // used to put a second copy of every layer in the legend. What the legend names is the
        // series, not its runs.
        using (var gapped = new PerformanceChart("stats.reads", "#2563eb", "/s", ChartSummary.Volume))
        {
            var end = DateTime.UtcNow;
            var samples = new List<(DateTime Time, double Value)>();
            for (int second = 40; second >= 20; second--) samples.Add((end.AddSeconds(-second), 5));
            for (int second = 10; second >= 0; second--) samples.Add((end.AddSeconds(-second), 5));
            gapped.SpanSeconds = 60;
            gapped.SetSeries(new List<ChartSeries>
            {
                new("cache", "#059669", samples),
                new("svn", "#2563eb", samples)
            }, end);
            // The chart lays its legend out when it paints, and a card that has never painted has
            // no markers to ask about.
            gapped.Resize(418, 280);
            using var painted = gapped.Grab();
            var named = gapped.LegendNames().OrderBy(name => name).ToArray();
            if (named.Length != 2 || named[0] != "cache" || named[1] != "svn")
                throw new InvalidOperationException(
                    $"Two series drawn in two runs each are named {named.Length} time(s) in the legend: {string.Join(", ", named)}");
        }
        // Every card is drawn up to the same moment, whatever cadence feeds it. A card drawn
        // against its own newest row put the same moment at a different place on each of them, and
        // the two fed once a second (the layers' holdings, the process's cost) drifted against the
        // four fed at the reader's rate as their cadences drifted.
        // The cards that have to agree about now are the ones being drawn: a card out of sight or in
        // a folded section is not rebuilt at all, so it holds the moment it was last built at.
        var ends = _charts.Concat(_processCharts).Append(_heldMemory).Append(_heldDisk)
            .Where(chart => chart.InSight)
            .Select(chart => chart.End).Distinct().ToArray();
        if (ends.Length > 1)
            throw new InvalidOperationException(
                $"The cards are drawn up to {ends.Length} different moments: {string.Join(", ", ends.Select(at => at.ToString("HH:mm:ss.fff")))}");
        // And the page's own cards: what their legends say must be one entry per layer, none
        // missing. A rebuild that leaves new markers unlaid-out, or a gap that is allowed to name
        // every run, fails here rather than in a picture somebody has to look at.
        foreach (var card in _charts.Where(card => card.InSight && card.Series.Count > 1))
        {
            var expected = card.Series.Select(item => item.Name).Distinct(StringComparer.Ordinal).ToArray();
            var names = card.LegendNames();
            if (names.Count != expected.Length || expected.Any(name => !names.Contains(name, StringComparer.Ordinal)))
                throw new InvalidOperationException(
                    $"{card.TitleKey} names {string.Join(", ", names)} in its legend where its series are {string.Join(", ", expected)}");
        }
        // What each card's chart was handed fits the axis it was handed with it: a fill is drawn
        // between its points and the floor, against that axis, so a point above it is an area over
        // the top of the plot and a point far below it is an area nobody can see. The axis is scaled
        // from the same readings, so the tallest of them belongs between a half and all of it.
        foreach (var card in Cards)
        {
            var (peak, axis) = card.Drawn;
            if (peak <= 0 || axis <= 0) continue;
            if (peak > axis * 1.001)
                throw new InvalidOperationException(
                    $"{card.TitleKey} draws up to {peak:0.###} against an axis that ends at {axis:0.###}");
            if (peak < axis * 0.5)
                throw new InvalidOperationException(
                    $"{card.TitleKey} draws up to {peak:0.###} against an axis of {axis:0.###}, which is nowhere near its own scale");
        }
        // The layer table carries every layer: no second scrollbar and no row outside the widget.
        if (_table.RowCount > 0 && (_table.Height < _table.HorizontalHeader().Height + _table.RowCount * TableRowHeight ||
            _table.VerticalScrollBarPolicy != Qt.ScrollBarPolicy.ScrollBarAlwaysOff))
            throw new InvalidOperationException($"Layer table shows {_table.RowCount} rows in {_table.Height} px");

    }

    /// <summary>Self-check for the line drawn between two observations: it passes through each of
    /// them at the value that was measured, between two of them it never leaves the stretch they
    /// span, readings that only rise are drawn only rising, and the grid the readings are drawn on
    /// is the readings themselves wherever they are already close enough together to be a curve.
    /// None of it needs a chart or a service.</summary>
    internal void VerifySmoothing()
    {
        // A peak between two quiet stretches: the shape a curve that overshoots would round up into
        // something taller than the reading it was given.
        double[] nodes = { 0, 1, 2, 3, 4 };
        double[] peak = { 0, 0, 10, 0, 0 };
        var grid = new List<double>();
        for (int i = 0; i < nodes.Length - 1; i++)
            for (int part = 0; part < 8; part++)
                grid.Add(nodes[i] + (nodes[i + 1] - nodes[i]) * part / 8.0);
        grid.Add(nodes[^1]);
        var drawn = SmoothCurve.Through(nodes, peak, grid.ToArray());
        if (drawn.Length != grid.Count)
            throw new InvalidOperationException($"The curve came back as {drawn.Length} points for the {grid.Count} it was asked about");
        for (int i = 0; i < nodes.Length; i++)
        {
            int at = grid.IndexOf(nodes[i]);
            if (at < 0) throw new InvalidOperationException($"The curve was not asked about the reading at {nodes[i]}");
            if (drawn[at] != peak[i])
                throw new InvalidOperationException($"The curve draws {drawn[at]} at a reading of {peak[i]}, which is not the reading");
        }
        for (int i = 0; i < grid.Count; i++)
        {
            int segment = Math.Min(nodes.Length - 2, (int)Math.Floor(grid[i]));
            double low = Math.Min(peak[segment], peak[segment + 1]);
            double high = Math.Max(peak[segment], peak[segment + 1]);
            if (drawn[i] < low - 1e-9 || drawn[i] > high + 1e-9)
                throw new InvalidOperationException(
                    $"The curve reaches {drawn[i]:0.###} between the readings of {low} and {high}");
        }
        var rising = SmoothCurve.Through(nodes, new double[] { 0, 1, 2, 3, 4 }, grid.ToArray());
        for (int i = 1; i < rising.Length; i++)
            if (rising[i] < rising[i - 1] - 1e-9)
                throw new InvalidOperationException(
                    $"Readings that only rise were drawn falling at {grid[i]}: {rising[i - 1]:0.###} then {rising[i]:0.###}");
        // The grid itself: a second of readings across fifteen minutes is a tenth of a pixel and is
        // left alone; across a minute it is six pixels and is drawn in two; and a pause is not drawn
        // across, so nothing is inserted into it.
        if (PerformanceChart.Subdivisions(1, 900, 2.5, 360) != 1 ||
            PerformanceChart.Subdivisions(1, 60, 2.5, 360) != 2 ||
            PerformanceChart.Subdivisions(2, 60, 2.5, 360) != 3 ||
            PerformanceChart.Subdivisions(30, 60, 2.5, 360) != 1)
            throw new InvalidOperationException(
                "The grid between readings is not the one the plot has room for: " +
                $"{PerformanceChart.Subdivisions(1, 900, 2.5, 360)}, {PerformanceChart.Subdivisions(1, 60, 2.5, 360)}, " +
                $"{PerformanceChart.Subdivisions(2, 60, 2.5, 360)}, {PerformanceChart.Subdivisions(30, 60, 2.5, 360)}");
    }

    /// <summary>Self-check for the movement between two samples: the same readings handed back a
    /// moment later move the picture on the time axis and change nothing else — not the value axis,
    /// which is scaled from readings that have not changed, and not the height the picture reaches —
    /// and a window of different readings is scaled again rather than drawn against the axis of the
    /// one before it. None of it needs a service.</summary>
    internal void VerifyFrames()
    {
        DateTime at = DateTime.UtcNow;
        using var card = new PerformanceChart("stats.reads", "#2563eb", "/s", ChartSummary.Volume);
        card.SpanSeconds = 30;
        card.GapSeconds = 2.5;
        card.SetSeries(Peaked(at, 4), at);
        var scaled = card.Domain;
        if (card.End != at)
            throw new InvalidOperationException($"A card handed a reading at {at:HH:mm:ss.fff} drew up to {card.End:HH:mm:ss.fff}");
        var (before, _) = card.Drawn;
        card.Advance(at.AddSeconds(1));
        if (card.End != at.AddSeconds(1))
            throw new InvalidOperationException($"A card moved to {card.End:HH:mm:ss.fff} rather than to the moment it was given");
        if (card.Domain != scaled)
            throw new InvalidOperationException($"A frame of the same readings rescaled the axis from {scaled} to {card.Domain}");
        // Nothing is drawn where nothing was read: the card moved a second on, and the newest
        // reading is still where it was read rather than stretched out to the edge it moved to.
        // A rate held over an interval nobody measured is a picture of something the page was
        // never told, and it is the end of the line changing shape at every sample.
        foreach (var series in card.Series)
            if (series.Samples.Count == 0 || series.Samples[^1].Time != at)
                throw new InvalidOperationException(
                    $"A card moved to {at.AddSeconds(1):HH:mm:ss.fff} draws {series.Name} out to " +
                    $"{series.Samples.LastOrDefault().Time:HH:mm:ss.fff}, which is not its last reading");
        var (after, _) = card.Drawn;
        // The peak the window reaches is where the reading put it: a curve drawn between readings
        // that ran past one of them would show up here as a window reaching higher than its own
        // readings do.
        if (Math.Abs(before - 4) > 1e-9 || Math.Abs(after - 4) > 1e-9)
            throw new InvalidOperationException($"A window of readings reaching 4 was drawn reaching {before} and then {after}");
        card.SetSeries(Peaked(at.AddSeconds(1), 40), at.AddSeconds(1));
        if (card.Domain == scaled)
            throw new InvalidOperationException("Ten times the readings were scaled to the axis of the window before them");
    }

    /// <summary>One window of readings a second apart, with a peak in it that recurs, so that a
    /// card's own checks have something to scale an axis to and something to keep reaching for.</summary>
    private static List<ChartSeries> Peaked(DateTime end, double height)
    {
        var samples = new List<(DateTime Time, double Value)>();
        for (int back = 30; back >= 0; back--)
            samples.Add((end.AddSeconds(-back), back % 7 < 3 ? height : height * 0.4));
        return new List<ChartSeries> { new("svn", "#f59e0b", samples) };
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
        if (!_paused) { _requested = true; RestartSchedules(); _lastSample = DateTime.MinValue; _lastAccessAt = DateTime.MinValue; }
    }

    private void Tick()
    {
        // A page watches for as long as it exists: what it samples for is the tab it is on, not the
        // picture it is showing, so stepping to another tab for a moment and coming back leaves no
        // hole in the line — and closing that tab takes the page, and with it this timer, away.
        bool watching = !_paused;
        if (_pending is { IsCompleted: true })
        {
            var task = _pending;
            _pending = null;
            try
            {
                var sample = task.GetAwaiter().GetResult();
                // The process reading is one of the charts' readings, so it is recorded with the
                // chart sample and at the reader's rate. It is *taken* with every request because
                // taking it costs nothing — it is the operating system being asked about a local
                // process — but a request that went out for the once-a-second access window alone
                // is a reading of the table, not another point on the IO card.
                if (sample.Trends is not null) AcceptProcess(sample.Process, sample.At);
                if (_requestEpoch == _epoch && watching)
                {
                    double dispatch = Stopwatch.GetElapsedTime(sample.Completed).TotalMilliseconds;
                    var ui = Stopwatch.StartNew();
                    if (sample.Access is { } access) AcceptAccess(access, sample.At);
                    if (sample.Held is { } held)
                    {
                        // A reading that names no layer is not a reading of zero held. It is what
                        // the first status call after a mount comes back with, before the layers
                        // have opened their stores and have a footprint to report — and drawn, it
                        // put a point at nothing at the left end of both held cards and a vertical
                        // jump from there to the first real level. Nothing is recorded for one; the
                        // schedule it rides is a lattice, so it is asked again at its own second
                        // rather than on every tick either way.
                        if (held.Count > 0)
                        {
                            _heldHistory.Add(new HeldSecond(sample.At, held));
                            LayerSampler.Trim(_heldHistory, sample.At.AddSeconds(-_rangeSeconds),
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
                        Accept(trends, measured, sample.At);
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
                if (watching) _status.SetText(T("stats.gap") + " · " + ex.Message);
            }
        }
        if (watching && _pending == null)
        {
            DateTime now = DateTime.UtcNow;
            // Watching starts here, resumes here, or follows a rate the reader just changed: the
            // next sample then measures the nominal interval rather than however long the page spent
            // paused or on the old rate. A page that has been watching all along is not fresh, and
            // its next sample covers the time since the last one it took.
            bool fresh = _requested || _lastSample == DateTime.MinValue;
            bool trendsDue = fresh || now >= _next;
            // The access reading is the expensive half of a request and its readers are the table
            // and the two drawings, so it keeps a once-a-second schedule of its own instead of
            // riding the trend rate - which is also what keeps it at a second when the charts run
            // slower. What it covers is the interval since the previous reading, so the answers
            // tile the timeline and the section can add them up over the range the reader chose.
            bool secondDue = _nextSecond == DateTime.MinValue || now >= _nextSecond;
            bool accessDue = _accessMetrics.Count == 0 || secondDue;
            // What the layers hold is a status question, and status is the expensive one: it walks
            // every layer's storage. It rides the access schedule for the same reason.
            bool heldDue = _heldHistory.Count == 0 || secondDue;
            if (trendsDue || accessDue || heldDue)
            {
                // The window is the time since the previous sample, so consecutive windows tile
                // the timeline: a tick that ran late - only one request may be in flight, so they
                // do run late - covers what it missed instead of leaving a hole or counting it
                // twice. The clamp keeps a long gap from billing a minute of traffic to one point.
                double seconds = fresh ? RefreshSeconds : Math.Clamp((now - _lastSample).TotalSeconds, 0.1, 5);
                // The access window tiles the same way, but it is measured from its own previous
                // reading rather than from the charts': the two schedules fire on different ticks,
                // and an answer that covered the wrong interval would be traffic counted twice or
                // not at all once the page adds the answers up over its range.
                double accessSeconds = 0;
                if (accessDue)
                {
                    accessSeconds = _lastAccessAt == DateTime.MinValue
                        ? Math.Min(1, Math.Max(0.1, RefreshSeconds))
                        : Math.Clamp((now - _lastAccessAt).TotalSeconds, 0.1, 5);
                    _lastAccessAt = now;
                }
                _requested = false;
                _requestEpoch = _epoch;
                if (trendsDue) { _next = NextDue(_next, now, RefreshSeconds); _lastSample = now; }
                // The once-a-second readings keep their own lattice, so refreshing the charts
                // faster does not multiply them and a late tick does not move them.
                if (secondDue) _nextSecond = NextDue(_nextSecond, now, 1);
                _pending = Task.Run(() =>
                {
                    var watch = Stopwatch.StartNew();
                    // The process is read here rather than on the thread that draws: it is the
                    // operating system being asked about a process, and it can be gone by the time the
                    // question is asked.
                    var process = _process.Read();
                    var trends = trendsDue ? Window(seconds) : null;
                    var access = accessDue ? Window(accessSeconds) : null;
                    var held = heldDue ? Held() : null;
                    watch.Stop();
                    // The moment the request went out travels with the answer: it is the moment the
                    // windows it covers end, and the moment the sample is stamped with, so a round
                    // trip that varied from sample to sample never reaches the chart.
                    return (trends, access, held, process, watch.Elapsed.TotalMilliseconds, Stopwatch.GetTimestamp(), seconds, now);
                });
            }
        }
        // Between two samples the picture moves rather than waiting: the readings are the same
        // ones, a moment of the clock later, which is a frame of the movement the reader sees.
        Animate();
    }

    /// <summary>The movement between two samples. A reader on a slow rate sees the whole picture
    /// step sideways once a second, a whole sampling interval at a time; what this draws is the
    /// steps in between, so that what the page shows is the window sliding rather than the window
    /// jumping.
    ///
    /// It is one moment for every card, taken here, because a card that moved to its own idea of
    /// now would put the same instant in a different place on each of them — which is the drift the
    /// cards were made to share a moment to avoid in the first place.
    ///
    /// Two things decide when a frame is drawn. The moment it draws to is now, so that the right
    /// edge of every card is the moment the reader is reading it at; the readings behind it are
    /// where they were read, and what has not been read since is the empty stretch between the last
    /// reading and that edge — see Windowed. A frame is drawn once the moment has moved the curve
    /// two pixels, which is where a step sideways starts to be visible as one: a span or a rate
    /// whose samples already move it less than that is not drawn between them at all, and a slow
    /// rate is drawn in steps of just those two pixels. And frames only happen while somebody is
    /// looking at the page — the reading goes on either way, and a page nobody is looking at is
    /// redrawn whole at its next sample's moment regardless.
    ///
    /// The frame rate is what the tick the page already runs on allows, ten a second, and what it
    /// costs is one draw of the cards and nothing else: the tables, the pie and the reconciliation
    /// are answers about the readings, and the readings have not changed.</summary>
    private void Animate()
    {
        if (_paused || !IsVisible()) return;
        double room = _charts[0].Areas.PlotWidth;
        if (room <= 0) return;
        double step = AnimationPixels * _rangeSeconds / room;
        DateTime end = DateTime.UtcNow;
        if (end < _clock.AddSeconds(step)) return;
        foreach (var card in _cards) card.Advance(end);
        _clock = end;
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
        DateTime end = _clock;
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
            if (cards[card].InSight) cards[card].SetSeries(drawn[card], end, ChartStatistics.Of(totals));
            if (cards[card].InSight) cards[card].SetMarkers(_recording?.Marks ?? Array.Empty<PerformanceChart.ChartMarker>());
        }
    }

    /// <summary>What the process moved per second, one rate per interval between two readings of
    /// its counters. Each carries the seconds it was measured over, because the window's total is
    /// an amount rather than the sum of a column of rates. A counter that went backwards is a
    /// worker that was replaced, and that interval has no rate to report.</summary>
    private List<(DateTime Time, double Rate, double Seconds)> TransferRates(bool reads)
    {
        var rates = new List<(DateTime, double, double)>();
        for (int i = 1; i < _processHistory.Count; i++)
        {
            var (previous, current) = (_processHistory[i - 1], _processHistory[i]);
            double elapsed = (current.Time - previous.Time).TotalSeconds;
            long now = reads ? current.ReadBytes : current.WriteBytes;
            long before = reads ? previous.ReadBytes : previous.WriteBytes;
            if (elapsed <= 0 || now < before) continue;
            rates.Add((current.Time, (now - before) / elapsed, elapsed));
        }
        return rates;
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

    /// <summary>Adds a reading to the window. A reading taken while nothing is running the instance
    /// is still a reading — zeroes say the process is not there, which is worth seeing rather than a
    /// gap the chart would draw as an absence of observation. The history is bounded by time rather
    /// than by count, because the refresh rate decides how many readings fall inside the window.
    ///
    /// The cards are not repainted from here: a reading arrives with the chart sample, and a card
    /// drawn as its rows arrive is a card with a cadence of its own. The page draws every card at
    /// the moment it takes a sample, against the one clock they share; this reading is drawn with
    /// the rest of them then.</summary>
    private void AcceptProcess(ProcessReading reading, DateTime now)
    {
        _processHistory.Add(new ProcessSecond(now, reading.Cores, reading.WorkingSetBytes, reading.PrivateBytes,
            reading.Threads, reading.Handles, reading.ReadBytes, reading.WriteBytes));
        LayerSampler.Trim(_processHistory, now.AddSeconds(-_rangeSeconds), second => second.Time, BreakSeconds);
    }

    /// <summary>The four process cards: the CPU the instance's process used, the memory it holds,
    /// and the threads and handles it has open. Their figures are the window's readings — the one
    /// the window's left edge falls inside is drawn, so that the line starts on the edge, but it is
    /// not counted, which is what keeps the header describing the window the line is drawn over.</summary>
    private void RenderProcess()
    {
        DateTime end = _clock;
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
            if (_processCharts[i].InSight) _processCharts[i].SetSeries(series[i], end, ChartStatistics.Of(figures[i]));
            if (_processCharts[i].InSight) _processCharts[i].SetMarkers(_recording?.Marks ?? Array.Empty<PerformanceChart.ChartMarker>());
        }
        // What the instance's own process moved, from the counters the operating system keeps for
        // it — the account the layer cards above are not: every byte that process moved, whichever
        // part asked (its logs, the cache database, the pipe this window reads), and blind to the
        // source's own fetching, which happens in a process of its own. One rate per interval
        // between two readings, the two directions drawn from the zero line so that they overlap
        // and the overlap reads as the two colours mixed.
        var reads = TransferRates(reads: true);
        var writes = TransferRates(reads: false);
        var totals = reads.Select(rate => (rate.Time, Read: rate.Rate, Write: 0.0, rate.Seconds))
            .Concat(writes.Select(rate => (rate.Time, Read: 0.0, Write: rate.Rate, rate.Seconds)))
            .GroupBy(point => point.Time)
            .OrderBy(group => group.Key)
            .Select(group => (Time: group.Key, Value: group.Sum(point => point.Read + point.Write),
                Seconds: group.Max(point => point.Seconds)))
            .ToList();
        var coveredIo = Covered(totals, end, _rangeSeconds, point => point.Time);
        _processIo.GapSeconds = BreakSeconds;
        _processIo.SpanSeconds = _rangeSeconds;
        if (_processIo.InSight) _processIo.SetSeries(new List<ChartSeries>
        {
            new(T("stats.process.moved.read"), "#059669", reads.Select(rate => (rate.Time, rate.Rate)).ToList()) { Stacked = false },
            new(T("stats.process.moved.write"), "#0891b2", writes.Select(rate => (rate.Time, rate.Rate)).ToList()) { Stacked = false }
        }, end, ChartStatistics.Of(coveredIo.Select(point => point.Value).ToArray(),
            coveredIo.Select(point => point.Seconds).ToArray()));
        if (_processIo.InSight) _processIo.SetMarkers(_recording?.Marks ?? Array.Empty<PerformanceChart.ChartMarker>());
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

    /// <summary>Takes one once-a-second reading: the table, the drawings, the legend, and the layer
    /// list the trend series are named and coloured from all read the sum of these over the range
    /// the reader chose rather than this answer alone.</summary>
    private void AcceptAccess(List<MountMetrics> access, DateTime at)
    {
        _accessHistory.Add((at, access));
        DateTime cutoff = at.AddSeconds(-_rangeSeconds);
        _accessHistory.RemoveAll(entry => entry.At < cutoff);
        _accessMetrics = AccessTotals.Sum(_accessHistory, at, _rangeSeconds);
        _latest = _accessMetrics;
        var alive = access.Select(m => m.Mount).Append("").ToHashSet();
        foreach (var stale in _history.Keys.Where(k => !alive.Contains(k)).ToArray()) _history.Remove(stale);
    }

    /// <summary>Records one observation of the trends. <paramref name="at"/> is the moment its
    /// request went out rather than the moment the answer came back: the round trip is tens to
    /// hundreds of milliseconds and it varies, so a stamp carrying it moves every vertex on the
    /// chart by however long that answer happened to take. It is also the moment the window the
    /// service answered for ends, and the same clock <paramref name="seconds"/> — the interval the
    /// amounts are divided by — is measured from, so the picture and the rates agree.</summary>
    private void Accept(List<MountMetrics> trends, double seconds, DateTime at)
    {
        var names = _latest.Select(m => m.Mount).ToArray();
        string selected = _mount.CurrentIndex <= 0 ? "" : _mount.CurrentText;
        // One step per layer for the whole tick, shared with the combined sample: taking it
        // again for the combined view would find the counters already advanced and report zero.
        var wasteSteps = WasteSteps(at, trends);
        foreach (var mount in trends) Add(mount.Mount, CaptureLayers(at, new[] { mount }, wasteSteps, seconds));
        Add("", CaptureLayers(at, trends, wasteSteps, seconds));
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

    /// <summary>Restarts both schedules on the same moment: the moment the next request goes out.
    ///
    /// They must share an anchor. The once-a-second readings — the access window and what the
    /// layers hold — normally fall due on the same tick as a trend sample and ride it, which is how
    /// one request covers both. Restart one schedule and not the other and they end up anchored a
    /// fraction of a second apart; from then on each moment either is due is a request of its own,
    /// so a page a reader set to one hertz asks twice a second for as long as it lives. Only the two
    /// places that force a sample have any reason to restart anything, and both restart everything.
    /// </summary>
    private void RestartSchedules() => _next = _nextSecond = DateTime.MinValue;

    /// <summary>When the sample after the one being taken now is due: the moment after
    /// <paramref name="now"/> on the lattice that <paramref name="due"/> stands on, or one nominal
    /// interval past <paramref name="now"/> when there is no lattice yet (<c>MinValue</c>).
    ///
    /// The page's poll is a coarse timer, and its ticks are quantized twice over: Qt lets a coarse
    /// timer sit anywhere within five per cent of its interval, and a Win32 timer's messages arrive
    /// on the system's own 15.6 ms tick, so a hundred-millisecond poll really fires every 109 ms.
    /// A schedule of "one interval after the tick that happened to carry the last sample" inherits
    /// both, and a page asked for one hertz then samples at 0.92 — which a reader sees as every
    /// second on the time axis sitting a tenth of a second to the left of the sample that belongs
    /// to it, the error growing across the window until the vertex nearest each gridline changes
    /// and it closes up again (measured on the live page: the tray's once-a-second requests came
    /// back every 1.06 to 1.15 s, mean 1.09).
    ///
    /// Counted from the lattice instead, a tick that arrives late is late by at most one tick, and
    /// the sample after it is still due when it always was. Slots the page was not there for are
    /// skipped rather than caught up: nobody watched through them, and a burst of samples each
    /// covering a tenth of a second would bill traffic to windows that never saw it.</summary>
    private static DateTime NextDue(DateTime due, DateTime now, double seconds)
    {
        DateTime next = due == DateTime.MinValue ? now : due;
        do next = next.AddSeconds(seconds); while (next <= now);
        return next;
    }

    private void Render()
    {
        string selected = _instanceName ?? (_mount.CurrentIndex <= 0 ? "" : _mount.CurrentText);
        var samples = _history.GetValueOrDefault(selected) ?? new();
        DateTime end = samples.LastOrDefault()?.Time ?? DateTime.UtcNow;
        _clock = end;
        RenderProcess();
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
            // The counters the operating system keeps for the process are a second account of the
            // same window, measuring something else: every byte the process moved, whichever part
            // of it asked — and blind to the source's own fetching, which happens in a process of
            // its own. That is not a layer, and stacking it here made the card state one total in
            // its header and another in its picture. It is drawn on its own card, under the
            // process's own numbers; what is short between the two accounts is a number, and the
            // byte account below the charts prints it.
            _charts[chart].SpanSeconds = _rangeSeconds;
            // A gap is what the drawing cannot join: bucketed points are further apart than the
            // observations they were folded from, so the threshold follows the point spacing too.
            _charts[chart].GapSeconds = Math.Max(BreakSeconds, spacing * 1.5);
            if (_charts[chart].InSight) _charts[chart].SetSeries(drawn, end, Figures(covered, rows, metric, predictiveKey));
            if (_charts[chart].InSight) _charts[chart].SetMarkers(_recording?.Marks ?? Array.Empty<PerformanceChart.ChartMarker>());
        }
        var accessRows = _accessMetrics.Where(m => selected == "" || m.Mount == selected)
            .SelectMany(m => m.Layers.Select(l => (Mount: m.Mount, Layer: l))).ToList();
        long total = accessRows.Sum(r => r.Layer.Hits + r.Layer.Writes);
        var (slices, callers) = AccessSlices(_accessMetrics, selected);
        _accessPie.SetSlices(slices, callers);
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
    private static (double RefreshHz, double RangeSeconds, Dictionary<string, bool> Sections, bool AccessAligned) LoadStats()
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
            // Missing from a file written before the access cards had two drawings, and aligned is
            // the right answer then too: it is the one the page was showing.
            bool aligned = !json.RootElement.TryGetProperty("AccessAligned", out var drawing) ||
                drawing.ValueKind != JsonValueKind.False;
            return (Array.Exists(RefreshRates, known => known == rate) ? rate : 1,
                Array.Exists(RangeLadder, known => known == span) ? span : 60, sections, aligned);
        }
        catch { return (1, 60, new Dictionary<string, bool>(), true); }
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
                Sections = _sections,
                AccessAligned = _accessAligned
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

    /// <summary>Whether this series sits on what is under it. True by default, which is what a
    /// layer's contribution is: the layers of one account tile the total and their bands add up.
    /// False for series that are not parts of anything — the process's own reads against its own
    /// writes — which are each drawn from the zero line instead, so the two areas overlap and the
    /// overlap is the two colours mixed. Stacked, two directions of traffic would be summed into a
    /// third number that is neither of them.</summary>
    public bool Stacked { get; init; } = true;
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
    /// <summary>A rate or an amount over time: the window's sum is an amount of the same kind as
    /// the samples, so the card reports it.</summary>
    Volume,

    /// <summary>A duration: milliseconds add up to nothing a reader asked for.</summary>
    Latency,

    /// <summary>A level rather than an amount — cores in use, bytes held, threads and handles open.
    /// What the window is worth is the average it sat at and the peak it reached; the samples added
    /// up are a number that grows with how often the page sampled, which is the refresh rate.</summary>
    Gauge
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
    /// as anything actually reached. <paramref name="targetIntervals"/> is what the plot has room
    /// to letter, which is a ceiling rather than a wish — see below.</summary>
    public static ChartDomain Of(IEnumerable<ChartSeries> series, string unit, double targetIntervals)
    {
        var samples = series.ToList();
        var times = samples.SelectMany(s => s.Samples.Select(sample => sample.Time)).Distinct().Order().ToArray();
        // As in Runs: the series on a card share one timeline, so when their times line up index for
        // index there is nothing to look up and no dictionary to build - and this runs on every card
        // on every sample, which is where a reader's stutter comes from.
        bool aligned = samples.All(s => s.Samples.Count == times.Length);
        for (int i = 0; aligned && i < samples.Count; i++)
            aligned = samples[i].Samples.Select(sample => sample.Time).SequenceEqual(times);
        var values = aligned
            ? null
            : samples.Select(s => s.Samples.ToDictionary(sample => sample.Time, sample => sample.Value)).ToArray();
        // What is stacked adds up, and what is drawn at its own value is only a candidate: two
        // overlaid series reach the taller of them, not their sum, and scaling to the sum would
        // leave the taller one short of the top of the plot.
        double At(int index, int step) => values == null
            ? samples[index].Samples[step].Value
            : values[index].GetValueOrDefault(times[step]);
        double peak = times.Select((time, step) =>
        {
            double stacked = 0, alone = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                double value = At(i, step);
                if (samples[i].Stacked) stacked += Math.Max(0, value);
                else alone = Math.Max(alone, Math.Max(0, value));
            }
            return Math.Max(stacked, alone);
        }).DefaultIfEmpty(0).Max();
        double trough = times.Select((time, step) => samples.Select((s, i) => Math.Min(0, At(i, step))).Sum())
            .DefaultIfEmpty(0).Min();
        var axis = unit == "B" ? ByteUnits.Select(peak) : (Scale: 1.0, Unit: unit);
        // The axis is scaled to what the window reached, and to nothing else. The floor this used
        // to keep under that — five, for a rate — left the busiest second of a quiet instance drawn
        // flat along the bottom of an axis nothing had filled, which is a picture of an idle card
        // rather than of a quiet one. A window that reached nothing at all has no magnitude of its
        // own to take a scale from, so it is given one: there is nothing to draw against it either
        // way, and an axis of no height has no grid for the next reading to land on.
        double max = Math.Max(peak / axis.Scale, 0) * 1.15;
        if (max <= 0) max = 1;
        double step = 0;
        int intervals = 0;
        // D3 nice: round the domain outward, then recompute until the step stabilizes.
        for (int pass = 0; pass < 10; pass++)
        {
            double next = NiceStep(max / targetIntervals);
            // Counts and cores are read in whole numbers — a gridline every half a request, or
            // every third of a core, is one nobody counts by. Durations and bytes have no grain.
            if (unit is not ("ms" or "B" or "stats.unit.cores")) next = Math.Max(1, next);
            if (next == step) break;
            step = next;
            intervals = Intervals(max, step);
            max = TickValue(intervals, step);
        }
        // Rounding a step down is what leaves more intervals than were asked for, and a chart whose
        // labels do not fit elides every one of them: measured, six labels in ninety-two pixels of
        // plot came out as six rows of dots. So the step grows until what will be drawn is what the
        // plot has room for — the ceiling is the caller's, and the caller measures it from the
        // height the last draw left for the data.
        int below = Below(trough, axis.Scale, step, intervals);
        while (step > 0 && intervals + below > targetIntervals)
        {
            step = NextNiceStep(step);
            intervals = Intervals(max, step);
            max = TickValue(intervals, step);
            below = Below(trough, axis.Scale, step, intervals);
        }
        return new(axis.Scale, axis.Unit, -below * step, max, step, intervals + below);
    }

    /// <summary>How many intervals of that width the domain is divided into.</summary>
    private static int Intervals(double max, double step) =>
        step <= 0 ? 0 : (int)Math.Ceiling(step < 1 ? max * Math.Round(1 / step) : max / step);

    /// <summary>The gridlines below the line: as far as what the window reached went below it, and
    /// no further than the positive side is tall. Every interval costs a line and a label, so an
    /// uncapped floor lets one sample decide how long a frame takes — waste is the sample that can
    /// do it, a layer evicting its whole budget in a second against a chart scaled to a few
    /// kilobytes, which once asked for six hundred million grid lines. What overflows the floor is
    /// drawn clipped to it.</summary>
    private static int Below(double trough, double scale, double step, int intervals) =>
        step <= 0 ? 0 : (int)Math.Clamp(Math.Ceiling(-trough / scale / step), 0, Math.Max(1, intervals));

    /// <summary>The next step of the 1/2/5 ladder above this one, for a domain that has to give up
    /// intervals to fit.</summary>
    private static double NextNiceStep(double step)
    {
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(step) + 1e-9));
        double fraction = step / magnitude;
        return magnitude * (fraction < 2 ? 2 : fraction < 5 ? 5 : 10);
    }

    /// <summary>D3 tickStep: nearest 1/2/5 decade, using geometric-mean thresholds.
    /// https://d3js.org/d3-array/ticks</summary>
    internal static double NiceStep(double target)
    {
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(target)));
        double fraction = target / magnitude;
        return magnitude * (fraction < Math.Sqrt(2) ? 1 : fraction < Math.Sqrt(10) ? 2 : fraction < Math.Sqrt(50) ? 5 : 10);
    }

    /// <summary>The number format for a value axis' labels: as many decimals as the step it chose
    /// has, so a step of 0.5 does not letter itself "0" over and over.</summary>
    internal static string TickFormat(double step) =>
        "%." + Math.Clamp(-(int)Math.Floor(Math.Log10(step)), 0, 15).ToString(I18n.Culture) + "f";

    internal static double TickValue(int index, double step) => step < 1 ? index / Math.Round(1 / step) : index * step;
}

/// <summary>What a card's layout came out as: the card, the rectangle the plot widget was given
/// inside it, the area the chart kept of that for the data itself, and the height its legend
/// claimed. The differences are what the axes and the legend take, which is the one thing about a
/// chart a reader cannot see in the picture — and the only way to tell a plot that is too small
/// from a card that is. The shares are against the plot widget, which is what the chart was given
/// to work with.</summary>
internal readonly record struct CardAreas(
    double Width, double Height, double WidgetWidth, double WidgetHeight,
    double PlotWidth, double PlotHeight, double LegendHeight)
{
    public double WidthShare => WidgetWidth <= 0 ? 0 : PlotWidth / WidgetWidth;
    public double HeightShare => WidgetHeight <= 0 ? 0 : PlotHeight / WidgetHeight;
    public override string ToString() =>
        $"{Width,4:0}x{Height,-4:0} card, plot {PlotWidth,4:0}x{PlotHeight,-4:0} of the " +
        $"{WidgetWidth,4:0}x{WidgetHeight,-4:0} given to the chart " +
        $"({WidthShare * 100,3:0}% wide, {HeightShare * 100,3:0}% tall), legend {LegendHeight,3:0}";
}

/// <summary>One chart card: what an instance did over the window its reader chose.
///
/// The card owns what no chart can draw — its frame, its title, the headline and the window's
/// figures — and hands everything inside the plot to Qt's chart: the grid, both axes with their
/// labels and their units, the legend and the areas. So a tick and the gridline it belongs to, or
/// a value and the line under it, cannot disagree: one transform decides all of them.
///
/// The chart is a child widget placed by this card's own layout, which is what makes the plot
/// whatever is left after the header. What the chart spends inside that rectangle on its axes and
/// its legend is the chart's own measurement, not a strip of pixels guessed at here: a chart uses
/// the margins it is given as a floor and grows them for its labels, so a card that added its own
/// room for them paid for the same labels twice.
/// </summary>
internal sealed class PerformanceChart : QWidget
{
    /// <summary>What the card keeps for its own furniture: the header above the plot, which holds
    /// the title and the headline with its figures, and the frame's inset at the sides and under
    /// the plot. Everything else is the chart's rectangle.
    ///
    /// The inset is as small as the frame allows: the chart is a square rectangle of its own white
    /// and the card's border is drawn round it, so the chart has to stop before the border's corners
    /// curve, which is a few pixels in. What the chart then keeps inside that for its axes and its
    /// legend is its own, and it keeps twenty pixels a side whatever it is asked for — that padding
    /// is most of what a reader sees between the plot and the card, and it is not the card's to take
    /// away. The text keeps an inset of its own, which is what the value labels beside it line up
    /// with.</summary>
    private const int PlotInset = 4, HeaderHeight = 72, FooterInset = 4;
    /// <summary>The baseline the headline and the window's figures share, so the header's row reads
    /// as one line. The title's baseline belongs to the card face, which every card shares.</summary>
    private const int HeadlineBaseline = 58;
    /// <summary>Gap between two figures in the headline's row.</summary>
    private const int FigureGap = 20;
    /// <summary>What the chart keeps inside the plot widget before the data gets any of it: its own
    /// corner padding and both axes' labels. Measured, a chart in a widget of this shape keeps
    /// forty-eight pixels of it, and a label that needs more than this rule leaves it asks for one
    /// interval fewer the next time the chart reports having elided one.</summary>
    private const int AxisFurniture = 48;
    /// <summary>How much of the plot one value label is read in. A chart elides every label rather
    /// than crowding them, and it does it on its own idea of how tall a label is: measured, six
    /// labels in ninety-two pixels of plot came out as six rows of dots where five were drawn whole,
    /// and eight in a hundred and forty-five came out cut where seven were not. Twenty-eight pixels
    /// is a label with the air it is read with at every height a card of these sizes reaches.</summary>
    private const int LabelSpacing = 28;
    private readonly string _titleKey, _color, _unit;
    private readonly ChartFigure[] _figures;
    private DateTime _end;
    private List<ChartSeries> _series = new();
    /// <summary>The readings the last draw was handed, before they were cut to the window. A frame
    /// between two samples is those same readings at a later moment, and it cuts them again from
    /// here rather than from the window the last frame drew — a window cut twice is a window whose
    /// own left edge has already been moved once.</summary>
    private List<ChartSeries>? _given;
    /// <summary>The plot itself: a Qt chart the card hosts as a child widget. It owns the grid, the
    /// axes, the areas and the lines — and its own layout, which is the rectangle the axes and the
    /// legend leave it.</summary>
    private readonly QChart _chart = new();
    private readonly QChartView _plot;
    private readonly QValueAxis _valueAxis = new();
    private readonly QValueAxis _timeAxis = new();
    /// <summary>What the chart is holding, so the next draw can move it rather than remake it: one
    /// band per run of every series, one hairline per recording edge, and the words beside those.
    /// A band is a slot a draw may find a different series in; an edge and its words belong to one
    /// marker each.</summary>
    private readonly List<Band> _bands = new();
    private readonly List<QLineSeries> _edges = new();
    private readonly List<QGraphicsSimpleTextItem> _notes = new();
    /// <summary>What the last draw was scaled to: the value domain both axes were given, and the
    /// unit the time axis is read in (one second, or a minute once the span is long enough that a
    /// tick a minute apart is what fits).</summary>
    private ChartDomain _domain;
    private bool _forced;
    private double _timeUnit = 1;
    private ChartStatistics _stats;

    /// <summary>A moment on the axis rather than a measurement of one — the edge of a recording.
    /// It travels with the data and leaves the window the way a sample does.</summary>
    internal readonly record struct ChartMarker(DateTime Time, string LabelKey, string Color);

    private IReadOnlyList<ChartMarker> _markers = Array.Empty<ChartMarker>();

    /// <summary>What this card is drawing, for the smoke run's checks.</summary>
    internal IReadOnlyList<ChartSeries> Series => _series;

    /// <summary>The moment this card was drawn up to, for the smoke run's check that every card is
    /// drawn up to the same one whatever cadence feeds it.</summary>
    internal DateTime End => _end;

    /// <summary>What the chart's legend names, for the smoke run. A series split around a pause is
    /// several items drawn and one name in the legend, so this is what a reader sees beside the
    /// plot rather than what the chart is holding.</summary>
    internal List<string> LegendNames()
    {
        var names = new List<string>();
        foreach (var band in _bands)
            foreach (var marker in _chart.Legend().Markers(band.Area))
                if (marker.IsVisible()) names.Add(marker.label());
        return names;
    }

    /// <summary>Whether any part of this card is inside the window the reader is looking at. The page
    /// is a scroll area some two thousand pixels tall seen through a viewport of eight hundred, and a
    /// card inside a folded section is not on screen either — in both cases Qt's visible region is
    /// empty, which is the one question worth asking before rebuilding a chart nobody can see.
    ///
    /// Skipping a draw is not a card left behind: the card is drawn again on the very next sample,
    /// because that is when it is asked for, and a card that has just come back into sight is asked
    /// for exactly like the others.</summary>
    internal bool InSight => _forced || !VisibleRegion().IsEmpty();

    /// <summary>Draws this card even though nobody can see it. The checks look at the cards
    /// themselves, and a page that is deliberately not drawing the ones off screen would answer them
    /// about half a page — so a check, or a photograph, asks for the whole of it.</summary>
    internal void DrawAnyway() => _forced = true;

    /// <summary>What the card's own layout came out as, for the render command and the smoke run:
    /// the card, the plot widget's rectangle inside it, the area the chart kept of that for the
    /// data, and what its legend took.</summary>
    internal CardAreas Areas => new(Width, Height, _plot.Width, _plot.Height,
        _chart.PlotArea.Width(), _chart.PlotArea.Height(),
        _chart.Legend().IsVisible() ? _chart.Legend().Geometry.Height() : 0);

    /// <summary>What the chart was handed against what its axis was scaled to: the tallest point
    /// among the areas it is drawing, and the top of the value axis it is drawing them against. A
    /// point above that axis is an area painted over the top of the plot whatever the card's own
    /// numbers say — which is what a series handed in bytes beside an axis in megabytes looks like:
    /// measured on a card holding 15.94 MiB against an axis of 20 MiB, the fill reached the top.
    /// </summary>
    internal (double Peak, double AxisMax) Drawn =>
        (_bands.Count == 0 ? 0 : _bands.Max(band => Enumerable.Range(0, band.Top.Count()).Max(i => band.Top.At(i).Y())),
         _domain.Max);

    /// <summary>Whether the chart had to elide its value labels to fit the plot it was given. A
    /// column of dots is a card nobody can read, and it is what a plot too short for the ticks it
    /// was asked for looks like — silently, because a chart answers by cutting the words.</summary>
    internal bool LabelsTruncated => _valueAxis.LabelsTruncated;

    /// <summary>What the card is showing, for the export and its own smoke checks.</summary>
    internal string TitleKey => _titleKey;
    internal string Unit => _unit;
    /// <summary>The unit as it is written out. A unit can be a catalog key — cores are counted in a
    /// word, where a rate, a byte and a millisecond are counted in a symbol the whole world spells
    /// the same — so it is lettered here rather than where the card is built, which is once. A
    /// string with no translation of its own comes back as itself, which is what the symbols do.
    /// </summary>
    private string UnitText => I18n.T(_unit);
    internal IReadOnlyList<ChartFigure> Figures => _figures;
    internal ChartStatistics Statistics => _stats;
    internal ChartDomain Domain => _domain;

    public PerformanceChart(string titleKey, string color, string unit, ChartSummary summary)
    {
        _titleKey = titleKey;
        _color = color;
        _unit = unit;
        _figures = summary switch
        {
            ChartSummary.Latency => new[] { ChartFigure.Average, ChartFigure.P95, ChartFigure.Peak },
            ChartSummary.Gauge => new[] { ChartFigure.Average, ChartFigure.Peak },
            _ => new[] { ChartFigure.Average, ChartFigure.Peak, ChartFigure.Total }
        };
        // The minimum is what the chart needs inside plus what the card keeps: the header, the
        // legend's own row, and both axes' labels — measured at this width, a chart spends 89 of its
        // pixels on the legend, the value labels and Qt's own corner padding, and what is left has
        // to be a plot a reader can read a curve off. A window too short for the page scrolls it,
        // which is what the scroll area is for.
        SetMinimumSize(330, 280);
        _chart.SetAnimationOptions(QChart.AnimationOptions.NoAnimation);
        _chart.SetBackgroundVisible(false);
        _chart.SetPlotAreaBackgroundVisible(false);
        _chart.SetPlotAreaBackgroundBrush(new QBrush(new QColor(TrayTheme.Tokens.Surface)));
        _chart.SetLocalizeNumbers(false);
        // What the chart keeps around its plot is its own business: it makes room for its axis
        // labels and grows the margins it is given where they are not enough. Its default is twenty
        // pixels on every side, and measured, that is exactly what a card was paying for nothing —
        // the same chart in the same rectangle drew a plot 40 pixels wider and 40 taller with this
        // set to nothing, which is a fifth of the card's height handed to no one.
        // What the chart keeps around its plot is its own business: it makes room for its axis
        // labels and holds the margins it is given to as a floor, so asking for none still leaves
        // about twenty pixels a side of its own padding — measured, that padding is most of what a
        // reader sees between the plot and the card. It is a floor rather than a cap, and a negative
        // margin is how a card takes half of it back: measured on a card of 418x280, the plot goes
        // from 338x115 to 358x139, the ink beside it from 19 and 18 pixels off the edges to 9, and
        // the white under the legend from 27 pixels to 15. What is left is still Qt's: the labels
        // sit in it, and the label area under the plot needs the eighteen pixels that remain.
        _chart.SetMargins(new QMargins(-10, -12, -10, -12));
        // The legend is the chart's own, under the plot where Qt puts it: one row of the series'
        // names, wrapped and cut off by the chart. It costs the plot the height of that row, and
        // nothing else does — drawing that row here as well meant three things had to agree about
        // one strip of the card, and the row's own contents changed width as the values in it did.
        _chart.Legend().SetAlignment(Qt.Alignment.AlignBottom);
        // The series are added bottom of the stack first, so the chart's own legend would list them
        // bottom up while the eyes read the stack top down. Reversed, the row reads the way the
        // picture does, and in the order the page's layer table lists the same layers.
        _chart.Legend().SetReverseMarkers(true);
        _chart.Legend().SetBackgroundVisible(false);
        _chart.Legend().SetLabelColor(new QColor(TrayTheme.Tokens.Body));
        _chart.Legend().SetFont(new QFont("Segoe UI", 8));
        // Both axes carry their own labels: dark enough to read, over a faint grid, with no axis
        // line of their own — the plot's white background is what the areas are read against.
        _valueAxis.SetLabelsFont(new QFont("Segoe UI", 8));
        _valueAxis.SetLabelsColor(new QColor(TrayTheme.Tokens.Muted));
        _valueAxis.SetGridLineColor(new QColor(TrayTheme.Tokens.Grid));
        _valueAxis.SetLineVisible(false);
        _valueAxis.SetMinorTickCount(0);
        _timeAxis.SetLabelsFont(new QFont("Segoe UI", 8));
        _timeAxis.SetLabelsColor(new QColor(TrayTheme.Tokens.Muted));
        _timeAxis.SetGridLineColor(new QColor(TrayTheme.Tokens.Grid));
        _timeAxis.SetLineVisible(false);
        _timeAxis.SetMinorTickCount(0);
        _chart.AddAxis(_timeAxis, Qt.Alignment.AlignBottom);
        _chart.AddAxis(_valueAxis, Qt.Alignment.AlignLeft);
        _plot = new QChartView(_chart, this);
        _plot.SetRenderHint(QPainter.RenderHint.Antialiasing);
        // The card is white behind the plot: a view's own viewport is otherwise the platform's
        // window colour, which shows through wherever the chart draws nothing.
        _plot.SetBackgroundBrush(new QBrush(new QColor(TrayTheme.Tokens.Surface)));
        // A card's plot is repainted whole rather than in the pieces Qt works out have changed: the
        // window moves under the chart at every sample, every series is re-mapped with it, and what
        // a partial update leaves showing is the frame before — which is the plot blinking.
        _plot.SetViewportUpdateMode(QGraphicsView.ViewportUpdateMode.FullViewportUpdate);
        // The plot is placed by this layout rather than by hand: a graphics view that is never told
        // what size it is lays itself out against a rectangle of its own making, which is how a
        // chart's legend ends up below the bottom edge of the widget the reader is looking at.
        var layout = new QVBoxLayout(this);
        layout.SetContentsMargins(PlotInset, HeaderHeight, PlotInset, FooterInset);
        layout.AddWidget(_plot);
    }

    public override void ChangeEvent(QEvent e)
    { base.ChangeEvent(e); if (e.type() == QEvent.Type.LanguageChange) Update(); }

    /// <summary>Marks the axis at those moments. They are annotation rather than data: drawn over
    /// the fills, in the recording's own colour, and never part of the stack or its statistics.</summary>
    public void SetMarkers(IReadOnlyList<ChartMarker> markers)
    {
        _markers = markers;
        // A recording's edges arrive between samples — the moment one is opened or closed — so they
        // are drawn into the chart as they come rather than waiting for the window to move.
        PlotNotes();
        RefreshToolTip();
        Update();
    }

    /// <summary>What the card says on hover: the layers, the window's figures, and when a recording
    /// this window covers began or ended. The plot says which layer is which, and what each one is
    /// at now is the one figure a picture of a stack cannot carry.</summary>
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

    /// <summary>The unit a time axis is read in, and the format its labels are lettered with, from
    /// the step its ticks stand at: a step of a minute or more is a duration nobody counts out in
    /// seconds. The unit is a symbol like the value axis' own, and the format is one string the
    /// chart letters every tick from.</summary>
    internal static (double Unit, string Format) TimeUnit(double step) =>
        step >= 60 ? (60, "%.0f m") : (1, "%.0f s");

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

    /// <summary><paramref name="figures"/> are the chart's window figures, computed by the page
    /// over every observation it holds. Drawing folds observations into fewer points when a range
    /// is long, and the numbers beside the line must not follow the drawing; without them, the
    /// series are all there is and they are described as they are.</summary>
    public void SetSeries(List<ChartSeries> series, DateTime end, ChartStatistics? figures = null)
    {
        _end = end;
        // The figures describe the observations, so they are taken from the series as they were
        // handed over, before the drawing cuts them to the window.
        if (figures is { } given) _stats = given;
        else
        {
            // Every series is built from the same sample list, so the window's totals are the sum
            // of the series at each observation.
            var times = series.SelectMany(s => s.Samples.Select(sample => sample.Time)).Distinct().Order().ToArray();
            var values = series.Select(s => s.Samples.ToDictionary(sample => sample.Time, sample => sample.Value)).ToArray();
            _stats = ChartStatistics.Of(times.Select(time => values.Sum(s => s.GetValueOrDefault(time))));
        }
        _series = series.Select(item => item with { Samples = Windowed(item.Samples, end, SpanSeconds) }).ToList();
        _given = series;
        Plot();
        RefreshToolTip();
        Update();
    }

    /// <summary>Draws the card at a moment later than the readings it was handed, with those same
    /// readings: what a reader watching a slow rate sees is the whole picture stepping sideways once
    /// a second, and this is the page drawing the steps in between.
    ///
    /// The moment is the page's and the readings are not: they are the ones it was given, cut to the
    /// window this moment makes, so nothing is invented between them — the newest reading stays
    /// where it was read, and the stretch between it and the right-hand edge is empty until the next
    /// reading extends the line into it. The value axis is left as the last draw scaled it: it is
    /// scaled from the readings, the readings are the same ones, and a frame that is a moment of the
    /// same window is not a reason to move every gridline under it. What sliding the window does
    /// change is which readings it still covers — a moment of them leaving on the left — and where
    /// that has taken the tall ones with it, the axis is scaled again rather than left describing a
    /// window that is gone.</summary>
    public void Advance(DateTime end)
    {
        if (_given is not { } readings || end <= _end) return;
        _end = end;
        _series = readings.Select(item => item with { Samples = Windowed(item.Samples, end, SpanSeconds) }).ToList();
        Plot(keepAxis: true);
        var (peak, axis) = Drawn;
        if (axis > 0 && (peak > axis * 1.001 || peak < axis * 0.5))
        {
            ConfigureAxes();
            Plot(keepAxis: true);
        }
        RefreshToolTip();
        Update();
    }

    /// <summary>Where a moment sits on the time axis: how long ago it was, in the unit that axis is
    /// reading in. Nought is the moment the card is drawn up to, which every card shares, and the
    /// window's other end is its own span before that.</summary>
    private double Along(DateTime time) => -(_end - time).TotalSeconds / _timeUnit;

    /// <summary>How much of the plot widget the data gets: what is left of it after the chart's own
    /// corner padding, the two axes' labels and the legend. The widget's own height is what this is
    /// counted from rather than the plot area the chart reports, because a chart reports where its
    /// plot ended up only after it has laid one out — a card's first picture would be scaled to a
    /// plot that does not exist yet — while the height it has been given is known immediately.</summary>
    private double PlotRoom() =>
        Math.Max(40, _plot.Height - AxisFurniture - (_chart.Legend().IsVisible() ? _chart.Legend().Geometry.Height() : 0));

    /// <summary>How much of the plot widget the data gets across, counted the same way the height
    /// is: what the chart's own corner padding and the value axis' labels leave of it. It is what
    /// decides how finely the readings are drawn between each other — see DrawGrid.</summary>
    private double PlotWidth => Math.Max(40, _plot.Width - AxisFurniture);

    /// <summary>One stretch of one series as the chart is holding it: the area, and the two lines it
    /// is built from. These are kept between draws and given new points, because a chart that is
    /// handed a fresh set of series every second rebuilds its item list and its legend every second:
    /// measured, tearing the seven cards down and building them again cost thirteen milliseconds a
    /// redraw, which at ten hertz is the interface thread blocked for an eighth of every second —
    /// and it is what a reader sees as the plot blinking.</summary>
    private sealed class Band
    {
        public Band(QAreaSeries area, QLineSeries top, QLineSeries bottom, string name, string color, bool hatched)
        {
            Area = area;
            Top = top;
            Bottom = bottom;
            Name = name;
            Color = color;
            Hatched = hatched;
        }
        public QAreaSeries Area { get; }
        public QLineSeries Top { get; }
        public QLineSeries Bottom { get; }
        /// <summary>What this band was last drawn as, so a draw that changes nothing about it does
        /// not touch the chart at all.</summary>
        public string Name { get; set; }
        public string Color { get; set; }
        public bool Hatched { get; set; }
    }

    /// <summary>Hands the window to the chart: both axes, an area per run of every series, and a
    /// hairline per recording edge.
    ///
    /// The runs are walked back to front, so the first one in the list is drawn last and reads as
    /// the top of the stack; what is not stacked is drawn over what came before it instead.
    ///
    /// A draw whose runs are the same runs as the last one moves their points rather than replacing
    /// them, which is the steady state of a page that is being watched: the same layers, drawn
    /// again, a moment later. Anything else — a layer appearing or going, a gap opening in the
    /// readings — is a new set of items, and the chart is rebuilt for it.
    ///
    /// <paramref name="keepAxis"/> draws the window against the value axis the last draw scaled,
    /// which is what a frame of the movement between two samples does: it is a moment of the same
    /// readings, and rescaling the axis for it would step the gridlines back and forth ten times a
    /// second. See <see cref="Advance"/>, which is the only caller that asks for it.</summary>
    private void Plot(bool keepAxis = false)
    {
        if (!keepAxis) ConfigureAxes();
        var runs = Runs();
        bool same = runs.Count == _bands.Count;
        for (int index = 0; same && index < runs.Count; index++) same = _bands[index].Name == runs[index].Name;
        if (same)
        {
            for (int index = 0; index < runs.Count; index++)
            {
                var (name, color, hatched, top, bottom) = runs[index];
                var band = _bands[index];
                band.Top.Replace(top);
                band.Bottom.Replace(bottom);
                if (band.Color != color || band.Hatched != hatched)
                {
                    band.Color = color;
                    band.Hatched = hatched;
                    Style(band.Area, color, hatched);
                }
            }
        }
        else
        {
            foreach (var band in _bands) { _chart.RemoveSeries(band.Area); band.Area.Dispose(); }
            _bands.Clear();
            foreach (var (name, color, hatched, top, bottom) in runs)
            {
                var topLine = new QLineSeries();
                var bottomLine = new QLineSeries();
                topLine.Replace(top);
                bottomLine.Replace(bottom);
                var area = new QAreaSeries(topLine, bottomLine);
                // The legend names the series, which is all a legend carries: what every series is
                // at is in the headline above, in the tooltip, and in each area's own edge.
                area.SetName(name);
                Style(area, color, hatched);
                _chart.AddSeries(area);
                _chart.SetAxisX(_timeAxis, area);
                _chart.SetAxisY(_valueAxis, area);
                _bands.Add(new Band(area, topLine, bottomLine, name, color, hatched));
            }
            // A series split around a pause is several items on the chart and one thing to the
            // reader, so only the first run of a name is named in the legend: the rest are that same
            // layer continuing. The legend names every item, which is why a backend restart — a hole
            // the width of the restart in the readings — used to put a second "cache" beside the
            // first and read as a second layer.
            var named = new HashSet<string>(StringComparer.Ordinal);
            foreach (var band in _bands)
            {
                if (named.Add(band.Name)) continue;
                foreach (var marker in _chart.Legend().Markers(band.Area)) marker.SetVisible(false);
            }
            // A series added to a chart that has already drawn comes with its legend marker hidden,
            // and nothing an ordinary sample does lays the legend out again — the chart is only
            // resized when its card is. Ask for that layout here and put the chart back the size the
            // view gave it, so a rebuild — a gap opening, a layer appearing — does not cost its
            // readers the legend. Measured: without this, every marker was invisible after the
            // second rebuild and stayed so; with it the first draw and every rebuild read alike.
            var geometry = _chart.Geometry;
            if (geometry.Width() > 0 && geometry.Height() > 0)
            {
                _chart.AdjustSize();
                _chart.SetGeometry(geometry);
            }
        }
        PlotNotes();
    }

    /// <summary>Colours one area the way its series is drawn: its own colour at the alpha a fill
    /// under a line is read at, hatched and dashed when it is a second reading of a layer that is
    /// already on the card rather than a layer of its own.</summary>
    private static void Style(QAreaSeries area, string color, bool hatched)
    {
        using var fill = TrayTheme.Series(color);
        fill.SetAlpha(TrayTheme.Tokens.SeriesFillAlpha);
        if (hatched)
        {
            using var hatch = new QBrush(Qt.BrushStyle.BDiagPattern);
            hatch.SetColor(fill);
            area.SetBrush(hatch);
        }
        else area.SetBrush(new QBrush(fill));
        using var stroke = TrayTheme.Series(color);
        using var pen = new QPen(stroke);
        if (hatched) pen.SetStyle(Qt.PenStyle.DashLine);
        pen.SetWidthF(1.8);
        area.SetPen(pen);
    }

    /// <summary>The window as the runs the chart draws: one per series, split wherever the readings
    /// pause for longer than a gap, back to front — the last one is the bottom of the stack.
    ///
    /// What is drawn is in the unit the axis is scaled to rather than in the unit the readings were
    /// taken in: a byte card's values are bytes and its axis is in whatever unit its tallest reading
    /// is read in (kilobytes, megabytes), which is what makes the axis readable and what makes a
    /// series drawn at its raw byte count an area that fills the plot whatever it holds — measured
    /// on a card holding 15.94 MiB against an axis of 20 MiB, the fill reached the top of it.
    ///
    /// The readings are drawn on a grid rather than joined up: every stretch between two of them
    /// that is wider than a few pixels gets points between them, and the values at those points come
    /// off the monotone cubic through the window's own readings (see <see cref="SmoothCurve"/>).
    /// Every layer is read on the same grid, which is what keeps the bands adjacent — the top of one
    /// is the bottom of the next, point for point — and every reading is a node of it, so the drawn
    /// curve still passes through each measurement at the value that was measured. Where the
    /// readings are already closer together than a few pixels the grid is the readings themselves
    /// and nothing at all is inserted, which is every long range and every fast rate.</summary>
    private List<(string Name, string Color, bool Hatched, QPointF[] Top, QPointF[] Bottom)> Runs()
    {
        var times = _series.SelectMany(item => item.Samples.Select(sample => sample.Time)).Distinct().Order().ToArray();
        // Every series on a card is built from the same observations — the page hands it one
        // timeline and a value per layer — so their times line up index for index, and a dictionary
        // built for the occasion is a lookup nobody needs: at ten hertz across a dozen cards that
        // was a dictionary per layer per card per sample, and it is the allocation, not the lookup,
        // that a reader feels. When the times do line up the values are read by index; a series with
        // a timeline of its own (a gap the others do not have) takes the dictionaries as before.
        bool aligned = _series.All(item => item.Samples.Count == times.Length);
        for (int layer = 0; aligned && layer < _series.Count; layer++)
            aligned = _series[layer].Samples.Select(sample => sample.Time).SequenceEqual(times);
        var values = aligned
            ? null
            : _series.Select(item => item.Samples.ToDictionary(sample => sample.Time, sample => sample.Value)).ToArray();
        var nodes = times.Select(Along).ToArray();
        var (grid, at) = DrawGrid(times, nodes);

        bool drawn = grid.Length > nodes.Length;
        // Each layer's own reading at each point of the grid: its measurements where nothing was
        // inserted between them, its curve where something was.
        var readings = new double[_series.Count][];
        for (int layer = 0; layer < _series.Count; layer++)
        {
            var measured = values == null
                ? _series[layer].Samples.Select(sample => sample.Value).ToArray()
                : times.Select(time => values[layer].GetValueOrDefault(time)).ToArray();
            readings[layer] = drawn ? SmoothCurve.Through(nodes, measured, grid) : measured;
        }
        var baseline = new double[grid.Length];
        double scale = _domain.Scale;
        var runs = new List<(string, string, bool, QPointF[], QPointF[])>();
        for (int layer = _series.Count - 1; layer >= 0; layer--)
        {
            var item = _series[layer];
            var reading = readings[layer];
            var lower = new double[grid.Length];
            var upper = new double[grid.Length];
            for (int index = 0; index < grid.Length; index++)
            {
                lower[index] = !item.Stacked || reading[index] < 0 ? 0 : baseline[index];
                upper[index] = lower[index] + reading[index];
            }
            int start = 0;
            while (start < times.Length)
            {
                // A gap wider than the page's own sampling is a pause rather than a stretch of
                // curve: the run ends there and the next one begins where the readings resume.
                int finish = start + 1;
                while (finish < times.Length && (times[finish] - times[finish - 1]).TotalSeconds <= GapSeconds) finish++;
                int from = at[start], to = at[finish - 1];
                var top = new QPointF[to - from + 1];
                var bottom = new QPointF[to - from + 1];
                for (int index = from; index <= to; index++)
                {
                    top[index - from] = new QPointF(grid[index], upper[index] / scale);
                    bottom[index - from] = new QPointF(grid[index], lower[index] / scale);
                }
                runs.Add((item.Name, item.Color, item.Hatched, top, bottom));
                start = finish;
            }
            if (item.Stacked)
                for (int index = 0; index < grid.Length; index++)
                    if (upper[index] > baseline[index]) baseline[index] = upper[index];
        }
        return runs;
    }

    /// <summary>The grid a window is drawn on: every reading's own place on the time axis, with the
    /// stretches between two of them given the points that turn a corner into a curve.
    /// <paramref name="at"/> says where each reading landed on it, which is how a run — a stretch of
    /// readings with no pause in it — is found on the grid rather than on the readings.
    ///
    /// What has room for a point between two readings is a question about pixels: a reading a second
    /// apart across a minute of a three-hundred-pixel plot is six pixels from the next one, and six
    /// pixels is a corner; the same second across fifteen minutes is a third of a pixel, which is
    /// already a curve. So the grid is bounded by the plot rather than by the window: it is at most
    /// one point every few pixels plus the readings themselves, however many readings there are.</summary>
    private (double[] X, int[] At) DrawGrid(DateTime[] times, double[] nodes)
    {
        var grid = new List<double>(times.Length);
        var at = new int[times.Length];
        for (int index = 0; index < times.Length; index++)
        {
            at[index] = grid.Count;
            grid.Add(nodes[index]);
            if (index + 1 == times.Length) continue;
            int parts = Subdivisions((times[index + 1] - times[index]).TotalSeconds, SpanSeconds, GapSeconds, PlotWidth);
            double width = nodes[index + 1] - nodes[index];
            for (int part = 1; part < parts; part++) grid.Add(nodes[index] + width * part / parts);
        }
        return (grid.ToArray(), at);
    }

    /// <summary>How many points the stretch between two readings a second apart is drawn in.
    /// <paramref name="room"/> is how many pixels the plot has for the whole window, so the width of
    /// the stretch is its share of that.</summary>
    internal static int Subdivisions(double seconds, double span, double gapSeconds, double room) =>
        seconds > gapSeconds || span <= 0 ? 1 : SmoothCurve.Subdivisions(room * seconds / span);


    /// <summary>Hands both axes what they draw with: the value domain, and the window as time
    /// before now. Both are set as a range, a step and a unit, and every position and every word
    /// after that is the chart's own — which is what keeps a label under the gridline it belongs
    /// to and a line inside the box the reader sees.
    ///
    /// How many intervals the value axis can carry is a question about the plot's height, and the
    /// plot's height is a question about how wide the labels are — so the domain is settled in two
    /// passes rather than guessed at in one: the second asks the chart again, having given it the
    /// first domain's labels to make room for.</summary>
    private void ConfigureAxes()
    {
        // A legend names the series so a reader can tell which band is which; a card that draws one
        // series has nothing to tell apart, and its row would only repeat the title above it. It is
        // decided before the axes are scaled because the room it takes is part of that answer.
        _chart.Legend().SetVisible(_series.Count > 1);
        // How many intervals the value axis can carry is a question about how much of the plot the
        // data actually gets, and a chart elides a label that does not fit rather than crowding it:
        // asked for too many, it answers with a column of dots. So the domain is given a ceiling
        // taken from the plot, and a card whose last draw came out cut gives up one interval for
        // this one — which covers the first draw, where the chart has not reported its legend yet,
        // and any label that turns out wider than this card assumed.
        double room = PlotRoom() / (double)LabelSpacing;
        if (_valueAxis.LabelsTruncated) room -= 1;
        _domain = ChartDomain.Of(_series, _unit, Math.Max(2, room));
        // The unit the numbers are in is written after each of them rather than beside them: an
        // axis title is drawn rotated, which reads as a word standing on its end next to numbers
        // that are upright, and every label that carries its own unit needs nothing explained. A
        // byte card is scaled into kilobytes or megabytes by what it holds, so the label says which
        // one it is; a card with no unit of its own — threads, handles — says nothing extra.
        // A unit that is a word cannot ride this format string yet: the call marshals it as an ANSI
        // format, so 核 arrives as "?" on every gridline label — measured, and it is this call and
        // not the catalog, because the card's headline, its figures and its tooltip draw the same
        // word correctly. Until the format string can carry one, the axis is left to the plain
        // number and the card's title and headline are what name the unit, which is how the sibling
        // cards — threads, handles — have always read.
        string spelled = _domain.Unit.Length > 0 ? I18n.T(_domain.Unit) : "";
        string unit = spelled.Length > 0 && spelled.All(character => character < 128) ? " " + spelled : "";
        string value = ChartDomain.TickFormat(_domain.Step) + unit;
        _valueAxis.SetRange(_domain.Floor, _domain.Max);
        _valueAxis.SetTickType(QValueAxis.TickType.TicksFixed);
        _valueAxis.SetTickCount(_domain.GridLines + 1);
        _valueAxis.SetLabelFormat(value);
        // The time axis reads as time before now: the moment the card is drawn up to is nought at
        // the right-hand end, and the span the reader chose runs back from it to the left. A long
        // span is read in minutes — the step decides, the way a clock does — and the unit goes into
        // the label format, which is one string rather than a tick table kept in step by hand.
        double step = TimeStep(SpanSeconds / Math.Max(2, Math.Max(240, _plot.Width) / 80));
        (_timeUnit, string format) = TimeUnit(step);
        _timeAxis.SetRange(-SpanSeconds / _timeUnit, 0);
        _timeAxis.SetTickType(QValueAxis.TickType.TicksDynamic);
        _timeAxis.SetTickAnchor(0);
        _timeAxis.SetTickInterval(step / _timeUnit);
        _timeAxis.SetLabelFormat(format);
    }

    /// <summary>A recording's edges, drawn into the chart as annotation rather than as data: a
    /// hairline dashed in the recording's own colour, dashed so it never reads as the boundary of a
    /// fill, with its words beside it. Both fade over the last stretch of the window, so a mark
    /// leaves the way it arrived instead of being cut off at the edge. An edge carries no reading,
    /// so it is left out of the legend: a marker with nothing behind it is a row spent on nothing.
    ///
    /// A recording's two edges can be seconds apart and their words wider than that: a note that
    /// would land on one already placed drops to a second row, and one with no room in either is
    /// left out rather than overprinted — its hairline is still on the axis, which is where the
    /// moment is read from anyway.
    ///
    /// The hairlines and their words are kept and moved rather than made again, the way a series is:
    /// an edge that is being watched is redrawn ten times a second, and a scene that gains and loses
    /// items ten times a second is a scene that blinks.</summary>
    private void PlotNotes()
    {
        if (_plot.Scene() is not { } scene) return;
        using var labelFont = new QFont("Segoe UI", 8);
        double left = -SpanSeconds / _timeUnit, fade = left * 0.12;
        var placed = new List<(double Left, double Right, int Row)>();
        int slot = 0;
        foreach (var marker in _markers.OrderBy(mark => mark.Time))
        {
            if (marker.Time < _end.AddSeconds(-SpanSeconds) || marker.Time > _end) continue;
            double x = Along(marker.Time);
            double alpha = x >= fade ? 1 : Math.Clamp((x - left) / (fade - left), 0, 1);
            var line = slot < _edges.Count ? _edges[slot] : FreshEdge();
            line.Replace(new[] { new QPointF(x, _domain.Floor), new QPointF(x, _domain.Max) });
            using var hairline = new QColor(marker.Color);
            hairline.SetAlpha((int)(Math.Min(alpha, 0.45) * 255));
            using var pen = new QPen(hairline);
            pen.SetStyle(Qt.PenStyle.DashLine);
            pen.SetWidthF(1.2);
            line.SetPen(pen);

            if (slot >= _notes.Count)
            {
                var fresh = new QGraphicsSimpleTextItem();
                fresh.SetFont(labelFont);
                _notes.Add(fresh);
                scene.AddItem(fresh);
            }
            var item = _notes[slot];
            item.SetText(I18n.T(marker.LabelKey));
            using var ink = new QColor(marker.Color);
            ink.SetAlpha((int)(alpha * 255));
            item.SetBrush(new QBrush(ink));
            // Beside its hairline, on whichever side has room for it, and never outside the plot.
            var at = _chart.MapToPosition(new QPointF(x, _domain.Max));
            double width = item.BoundingRect().Width();
            double beside = at.X() + 5 + width <= _plot.Width ? at.X() + 5 : at.X() - 5 - width;
            double noteLeft = Math.Clamp(beside, 0, Math.Max(0, _plot.Width - width));
            bool Clashes(int row) => placed.Any(other => other.Row == row &&
                noteLeft < other.Right + 6 && noteLeft + width > other.Left - 6);
            int stack = Clashes(0) ? 1 : 0;
            item.SetVisible(!(stack == 1 && Clashes(1)));
            if (item.IsVisible()) placed.Add((noteLeft, noteLeft + width, stack));
            item.SetPos(noteLeft, at.Y() + 3 + stack * 16);
            slot++;
        }
        // What is left over belonged to edges that have left the window: their hairlines come off
        // the chart and their words go, which is the one thing here that is taken away rather than
        // moved — an edge that has left has nothing left to move.
        while (_edges.Count > slot)
        {
            var extra = _edges[^1];
            _chart.RemoveSeries(extra);
            extra.Dispose();
            _edges.RemoveAt(_edges.Count - 1);
        }
        foreach (var note in _notes.Skip(slot)) note.SetVisible(false);
    }

    /// <summary>A hairline for a recording edge that has not had one before. It carries no reading,
    /// so its legend marker goes: the chart lists every series it holds, and this is not one.</summary>
    private QLineSeries FreshEdge()
    {
        var line = new QLineSeries();
        _chart.AddSeries(line);
        _chart.SetAxisX(_timeAxis, line);
        _chart.SetAxisY(_valueAxis, line);
        foreach (var legend in _chart.Legend().Markers(line)) legend.SetVisible(false);
        _edges.Add(line);
        return line;
    }

    public override void ResizeEvent(QResizeEvent e)
    {
        base.ResizeEvent(e);
        // Both axes are scaled from the rectangle they turned out to have: how many intervals a
        // value axis carries is a question about the plot's height, and which clock step the time
        // axis can afford is one about its width. A resize is rare; a redraw is not.
        Plot();
    }

    /// <summary>A series as the window shows it. A card is drawn against one moment — the moment the
    /// page is drawn up to — and the rows behind a series are read at whatever cadence that series
    /// has: the four trend cards at the reader's rate, what the layers hold once a second, what the
    /// process costs with every request. So a run of rows is cut to the window rather than drawn
    /// where it happens to stop.
    ///
    /// At the left, the segment crossing the edge is cut at the edge and the value there is
    /// interpolated between the two rows that straddle it. The alternative — moving the older row
    /// to the edge and keeping its value — stretches that first segment and makes it jump every
    /// time the row ages out, one sampling interval wide; the line is the same line either way,
    /// only cut at the right place.
    ///
    /// At the right the series simply ends where its readings end. Nothing is drawn past the last
    /// one however far the window reaches: a rate held over an interval that has not been measured,
    /// and a level carried forward until the next reading contradicts it, are both a picture of
    /// something the page was never told. It used to carry the last row's value out to the window's
    /// edge, which is what made the right-hand end of a curve something of its own — it changed
    /// shape at every sample, in the one place a reader watching a slow rate is looking.
    ///
    /// Nothing is added before the first row either: a page younger than its window has no earlier
    /// row to draw from, and its line still begins where its own rows do.</summary>
    internal static List<(DateTime Time, double Value)> Windowed(List<(DateTime Time, double Value)> samples,
        DateTime end, double span)
    {
        DateTime start = end.AddSeconds(-span);
        int first = samples.FindIndex(sample => sample.Time >= start);
        // Every row is older than the window: the run has left it, and there is nothing to cut.
        if (first < 0) return samples;
        var drawn = samples.Skip(first).ToList();
        // A row landing exactly on the edge is the crossing segment's own end: there is nothing to
        // cut, and a point inserted beside it would be the same moment twice.
        if (first > 0 && drawn[0].Time > start)
        {
            var (before, after) = (samples[first - 1], samples[first]);
            double gap = (after.Time - before.Time).TotalSeconds;
            double weight = gap > 0 ? (start - before.Time).TotalSeconds / gap : 0;
            drawn.Insert(0, (start, before.Value + (after.Value - before.Value) * weight));
        }
        return drawn;
    }

    private string FormatValue(double value) => _unit switch
    {
        "B" => ByteUnits.Format(value),
        "" => value.ToString("0.##", I18n.Culture),
        _ => value.ToString("0.##", I18n.Culture) + " " + UnitText
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
        return _unit == "B" ? ByteUnits.Format(value) : value.ToString("0.##", I18n.Culture) + " " + UnitText;
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
        var (number, unit) = _unit == "B" ? ByteUnits.Split(_stats.Current) : (_stats.Current.ToString("0.##", I18n.Culture), UnitText);
        string text = hasData ? number : "—";
        p.SetFont(numberFont);
        using var ink = TrayTheme.Series(_color);
        p.SetPen(new QPen(ink));
        p.DrawText(18, HeadlineBaseline, text);
        int headRight = 18 + numberMetrics.HorizontalAdvance(text);
        if (hasData && unit.Length > 0)
        {
            p.SetFont(unitFont);
            p.SetPen(new QPen(new QColor(TrayTheme.Tokens.Muted)));
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
            p.SetPen(new QPen(new QColor(TrayTheme.Tokens.Muted)));
            p.DrawText(x, HeadlineBaseline, labels[i]);
            x += labelMetrics.HorizontalAdvance(labels[i]) + 6;
            p.SetFont(figureFont);
            p.SetPen(new QPen(new QColor(TrayTheme.Tokens.Body)));
            p.DrawText(x, HeadlineBaseline, values[i]);
            x += figureMetrics.HorizontalAdvance(values[i]) + FigureGap;
        }
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

    public override void PaintEvent(QPaintEvent e)
    {
        using var p = new QPainter(AsQPaintDevice());
        p.SetRenderHint(QPainter.RenderHint.Antialiasing);
        try
        {
            int width = Width, height = Height;
            CardFace.Paint(p, width, height, I18n.T(_titleKey));
            bool hasData = _series.Any(item => item.Samples.Count > 0);
            DrawHeadline(p, width, hasData);
            // The plot itself is the chart's: it drew its grid, its axes, its labels and its
            // legend as this event was reached, and what is drawn here is only what a chart has no
            // way to draw.
            if (!hasData)
            {
                var view = _plot.Geometry;
                using var noteFont = new QFont("Segoe UI", 8);
                p.SetFont(noteFont);
                p.SetPen(new QPen(new QColor(TrayTheme.Tokens.Faint)));
                p.DrawText(view.X() + 12, view.Y() + 30, I18n.T("stats.noData"));
            }
        }
        finally { p.End(); }
    }
}
