using PolyMount.Core;
using PolyMount.Core.Ipc;
using QtSharp.Api;
using System.Diagnostics;
using System.Text.Json;

namespace PolyMount.Tray;

/// <summary>Task-manager style performance charts, sampled asynchronously once per second.</summary>
internal sealed class StatsWindow : QWidget, ICloseGuard
{
    private readonly QComboBox _mount;
    private readonly string? _instanceName;
    private readonly RecordingPanel? _recording;
    private readonly QPushButton _pause;
    private readonly QTableWidget _table;
    private readonly QLabel _status;
    private readonly QLabel _layerLegend;
    private readonly LayerAccessPie _accessPie;
    private readonly QLabel _accessLegend;
    private readonly QLabel _responseCost;
    private readonly Queue<(double Request, double Dispatch, double Ui)> _responseSamples = new();
    private List<MountMetrics> _accessMetrics = new();
    private readonly QTimer _timer;
    private readonly PerformanceChart[] _charts;
    private readonly Dictionary<string, List<LayerSampler.Sample>> _history = new();
    /// <summary>Last-seen waste counter per layer, with the time it was read: the charts plot
    /// a rate, so a step only means something next to the interval it was measured over.</summary>
    private readonly Dictionary<string, LayerSampler.Counters> _wasteSteps = new();
    /// <summary>Sampling runs once a second; a step spanning more than this is a pause in
    /// observation rather than a rate.</summary>
    private const double SamplingGapSeconds = 3;
    /// <summary>The window every chart, the sample history and the response overhead share.</summary>
    private const int HistorySeconds = 60;
    /// <summary>The height of one row of the layer table, which the table is sized from.</summary>
    private const int TableRowHeight = 36;
    private List<MountMetrics> _latest = new();
    private Task<(ControlResponse Trends, ControlResponse Access, double RequestMs, long Completed)>? _pending;
    private bool _paused, _requested, _wasVisible;
    private int _epoch, _requestEpoch;
    private DateTime _next;
    private static string T(string key) => I18n.T(key);

    public StatsWindow(string? instanceName = null)
    {
        _instanceName = instanceName;
        SetObjectName("dashboard");
        SetStyleSheet(TrayTheme.StyleSheet);
        SetWindowIcon(TrayIcons.App);
        I18n.Bind(SetWindowTitle, "stats.title");
        Resize(1180, 960);
        var root = new QVBoxLayout(WindowScrollArea.Create(this, 900, 900));
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
        // A recording's edges are worth seeing the moment they happen, not at the next sample.
        if (_recording != null) _recording.MarksChanged += Render;
        root.AddLayout(grid, 1);
        _layerLegend = new QLabel();
        _layerLegend.SetWordWrap(true);
        _layerLegend.SetTextFormat(Qt.TextFormat.RichText);
        _layerLegend.SetObjectName("muted");
        root.AddWidget(_layerLegend);
        root.AddWidget(Label("stats.layerBytes.definition", "muted"));
        var access = new QHBoxLayout();
        _accessPie = new LayerAccessPie();
        access.AddWidget(_accessPie);
        _accessLegend = new QLabel();
        _accessLegend.SetTextFormat(Qt.TextFormat.RichText);
        _accessLegend.SetWordWrap(true);
        access.AddWidget(_accessLegend, 1);
        root.AddLayout(access);
        root.AddWidget(Label("stats.layer", "sectionTitle"));
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
        root.AddWidget(_table);
        _status = new QLabel();
        _status.SetObjectName("muted");
        _status.SetTextFormat(Qt.TextFormat.PlainText);
        root.AddWidget(_status);
        _responseCost = new QLabel();
        _responseCost.SetObjectName("muted");
        _responseCost.SetWordWrap(true);
        root.AddWidget(_responseCost);
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
        _requested = true;
    }

    internal void Capture(string path) { using var image = Grab(); image.Save(path); }

    /// <summary>The recording's own checks, driven from here because the panel belongs to this
    /// window and a smoke run has no other handle on it.</summary>
    internal void VerifyRecording(string? capture = null)
    {
        _recording?.Verify();
        // The marks are drawn by hand, in a paint path nothing else exercises while no recording is
        // running: this forces one with both edges on the axis.
        if (_recording == null) return;
        var end = DateTime.UtcNow;
        foreach (var chart in _charts)
        {
            chart.SetMarkers(new[]
            {
                new PerformanceChart.ChartMarker(end.AddSeconds(-20), "record.marker.start", "#dc2626"),
                new PerformanceChart.ChartMarker(end.AddSeconds(-3), "record.marker.stop", "#64748b"),
                // One on its way out, which must fade rather than be cut off, and one that has
                // already left the window, which must simply not be drawn.
                new PerformanceChart.ChartMarker(end.AddSeconds(-57), "record.marker.start", "#dc2626"),
                new PerformanceChart.ChartMarker(end.AddSeconds(-90), "record.marker.start", "#dc2626")
            });
            // A second apart, the way real samples arrive: a chart draws breaks for gaps, so
            // sparse data would leave the marks over an empty plot and prove nothing.
            var cache = new List<(DateTime Time, double Value)>();
            var svn = new List<(DateTime Time, double Value)>();
            for (int second = 59; second >= 0; second--)
            {
                double wave = Math.Sin(second / 7.0);
                cache.Add((end.AddSeconds(-second), 6.0 + wave * 3.0));
                svn.Add((end.AddSeconds(-second), 2.0 + Math.Cos(second / 5.0) * 1.5));
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
    internal void CaptureTable(string path) { using var image = _table.Grab(); image.Save(path); }
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
        var sample = CaptureLayers(now, mounts, WasteSteps(now, mounts));
        if (sample.Layers.Count != 3 || sample.Layers.Values.Sum(l => l.Reads) != 5 ||
            sample.Layers.Values.Sum(l => l.Cost) != 16 || sample.Layers.Values.Sum(l => l.Bytes) != 3072 ||
            sample.Layers.Values.Sum(l => l.WriteBytes) != 4096)
            throw new InvalidOperationException("Layer chart contributions do not sum to the overall metrics");
        // A block cache reports lifetime byte totals, so its window figure is the step between
        // observations rather than the counters themselves.
        var ram = new MountMetrics { Mount = "m", Layers = new() { new() { Name = "ram", Accelerating = true } } };
        var history = new List<LayerSampler.Sample>();
        foreach (var (served, fetched) in new[] { (0L, 0L), (3000L, 1000L), (6000L, 2000L) })
        {
            ram.Layers[0].AcceleratedBytes = served;
            ram.Layers[0].FetchedBytes = fetched;
            history.Add(CaptureLayers(now, new[] { ram }, WasteSteps(now, new[] { ram })));
        }
        if (Accelerated(history, "m/ram") != (75.0, 6000.0))
            throw new InvalidOperationException("Accelerator bytes are not measured over the window");
        if (ByteUnits.Select(1023).Unit != "B" || ByteUnits.Select(1024).Unit != "KiB" ||
            ByteUnits.Select(1048576).Unit != "MiB" || ByteUnits.Select(1073741824).Unit != "GiB")
            throw new InvalidOperationException("Byte unit thresholds are incorrect");
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
            Seconds = new List<SecondRecord> { new(ended, 2, 4096, 1.5, 0, 4, 0, 0, 1, 2, 3, 4, 5) },
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
        if (!_paused) { _requested = true; _next = DateTime.MinValue; }
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
                var response = sample.Trends;
                if (!response.Ok) throw new InvalidOperationException(response.Error);
                if (!sample.Access.Ok) throw new InvalidOperationException(sample.Access.Error);
                if (_requestEpoch == _epoch && !_paused && visible)
                {
                    double dispatch = Stopwatch.GetElapsedTime(sample.Completed).TotalMilliseconds;
                    var ui = Stopwatch.StartNew();
                    _accessMetrics = sample.Access.Metrics;
                    // The table and legend show the recent average; the one-second sample
                    // only feeds the per-second chart history, where a gap would otherwise
                    // read as "every layer served nothing".
                    Accept(response.Metrics, sample.Access.Metrics);
                    ui.Stop();
                    _responseSamples.Enqueue((sample.RequestMs, dispatch, ui.Elapsed.TotalMilliseconds));
                    while (_responseSamples.Count > HistorySeconds) _responseSamples.Dequeue();
                    RenderResponseCost();
                }
            }
            catch (Exception ex)
            {
                if (!_paused && visible) _status.SetText(T("stats.gap") + " · " + ex.Message);
            }
        }
        if (visible && !_paused && _pending == null && (_requested || !_wasVisible || DateTime.UtcNow >= _next))
        {
            _requested = false;
            _requestEpoch = _epoch;
            _next = DateTime.UtcNow.AddSeconds(1);
            _pending = Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                var trends = ControlClient.Metrics(1, _instanceName);
                var access = ControlClient.Metrics(HistorySeconds, _instanceName);
                watch.Stop();
                return (trends, access, watch.Elapsed.TotalMilliseconds, Stopwatch.GetTimestamp());
            });
        }
        _wasVisible = visible;
    }


    /// <summary>A layer key is "mount/layer"; the records carry the two apart.</summary>
    private static string Relative(string key, string mount) =>
        key.StartsWith(mount + "/", StringComparison.Ordinal) ? key[(mount.Length + 1)..] : key;

    private void Accept(List<MountMetrics> trends, List<MountMetrics> metrics)
    {
        _latest = metrics;
        DateTime now = DateTime.UtcNow;
        var alive = metrics.Select(m => m.Mount).Append("").ToHashSet();
        foreach (var stale in _history.Keys.Where(k => !alive.Contains(k)).ToArray()) _history.Remove(stale);
        // One step per layer for the whole tick, shared with the combined sample: taking it
        // again for the combined view would find the counters already advanced and report zero.
        var wasteSteps = WasteSteps(now, trends);
        foreach (var mount in trends) Add(mount.Mount, CaptureLayers(now, new[] { mount }, wasteSteps));
        Add("", CaptureLayers(now, trends, wasteSteps));
        string selected = _mount.CurrentIndex <= 0 ? "" : _mount.CurrentText;
        var names = metrics.Select(m => m.Mount).ToArray();
        if (_mount.Count != names.Length + 1 || names.Where((name, i) => _mount.ItemText(i + 1) != name).Any())
        {
            _mount.BlockSignals(true);
            _mount.Clear();
            _mount.AddItem(T("stats.all"));
            foreach (var name in names) _mount.AddItem(name);
            int index = metrics.FindIndex(m => m.Mount == selected);
            _mount.SetCurrentIndex(index < 0 ? 0 : index + 1);
            _mount.BlockSignals(false);
        }
        Render();
        _status.SetText(T("stats.live") + " · " + DateTime.Now.ToString("HH:mm:ss"));
    }

    /// <summary>Takes the step for this tick of every counter a layer reports as a lifetime
    /// total, one entry per layer, through the rule the recording page shares.</summary>
    private Dictionary<string, LayerSampler.WasteStep> WasteSteps(DateTime time, IEnumerable<MountMetrics> mounts) =>
        LayerSampler.Steps(time, mounts, _wasteSteps, SamplingGapSeconds);

    private LayerSampler.Sample CaptureLayers(DateTime time, IEnumerable<MountMetrics> mounts,
        Dictionary<string, LayerSampler.WasteStep> wasteSteps) => LayerSampler.Capture(time, mounts, wasteSteps);

    private void Add(string name, LayerSampler.Sample sample) =>
        LayerSampler.Add(_history, name, sample, HistorySeconds);

    /// <summary>What an accelerating layer kept out of the streams underneath over the window.</summary>
    private static (double Percent, double Bytes) Accelerated(IEnumerable<LayerSampler.Sample> samples, string key) =>
        LayerSampler.Accelerated(samples, key);

    private void Render()
    {
        string selected = _instanceName ?? (_mount.CurrentIndex <= 0 ? "" : _mount.CurrentText);
        var samples = _history.GetValueOrDefault(selected) ?? new();
        DateTime end = samples.LastOrDefault()?.Time ?? DateTime.UtcNow;
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
        for (int chart = 0; chart < _charts.Length; chart++)
        {
            int metric = chart;
            var drawn = rows.Select(row => new ChartSeries(
                selected == "" ? row.m.Mount + "/" + row.l.Name : string.IsNullOrEmpty(row.l.DisplayName) ? row.l.Name : row.l.DisplayName,
                string.IsNullOrEmpty(row.l.Color) ? "#64748B" : row.l.Color,
                samples.Select(s =>
                {
                    var layer = s.Layers.GetValueOrDefault(row.m.Mount + "/" + row.l.Name);
                    return (s.Time, layer == null ? 0 : metric switch { 0 => layer.Reads, 1 => layer.Bytes, 2 => layer.Cost, _ => layer.WriteBytes });
                }).ToList())).ToList();
            // A guess that had to be evicted unread is drawn below the zero line, so a
            // read-ahead layer that is guessing badly pulls its own chart downward.
            if (metric <= 1 && outcomes != null)
            {
                var (name, color) = outcomes.Value;
                drawn.Add(new ChartSeries(name, color, samples.Select(s =>
                {
                    var layer = s.Layers.GetValueOrDefault(predictiveKey!);
                    double step = layer == null ? 0 : metric == 0 ? layer.Wasted : layer.WastedBytes;
                    return (s.Time, -step);
                }).ToList()) { Hatched = true });
            }
            _charts[chart].SetSeries(drawn, end);
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
        string legend = string.Join(" &nbsp; · &nbsp; ", rows.Select(row =>
            $"<span style='color:{(string.IsNullOrEmpty(row.l.Color) ? "#64748B" : row.l.Color)}'>●</span> " +
            System.Net.WebUtility.HtmlEncode(selected == "" ? row.m.Mount + "/" + row.l.Name : string.IsNullOrEmpty(row.l.DisplayName) ? row.l.Name : row.l.DisplayName) +
            " " + ByteUnits.Format(row.l.Bytes)));
        if (_layerLegend.Text != legend) _layerLegend.SetText(legend);
        bool structureChanged = _table.RowCount != rows.Count;
        if (structureChanged) { _table.SetUpdatesEnabled(false); _table.SetRowCount(rows.Count); }
        for (int row = 0; row < rows.Count; row++)
        {
            var (mount, layer) = rows[row];
            string accelerated = "";
            if (layer.Accelerating)
            {
                var (percent, bytes) = Accelerated(samples, mount.Mount + "/" + layer.Name);
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
        _mount.SetItemText(0, T("stats.all"));
        _table.SetHorizontalHeaderLabels(new[] { T("mount.instances"), T("layer"), T("hits"), T("share"), T("avg"), T("max"), T("bytes"), T("stats.accelerated") });
        _status.SetText(T(_paused ? "stats.paused" : "stats.live"));
        Render();
        RenderResponseCost();
    }
    private void RenderResponseCost()
    {
        if (_responseSamples.Count == 0) { _responseCost.SetText(T("stats.response.waiting")); return; }
        var requests = _responseSamples.Select(s => s.Request).Order().ToArray();
        _responseCost.SetText(I18n.F("stats.response.cost", requests.Average(), ChartStatistics.Percentile(requests, 0.95),
            _responseSamples.Average(s => s.Dispatch), _responseSamples.Average(s => s.Ui), _responseSamples.Count));
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
    public static ChartStatistics Of(IEnumerable<double> values)
    {
        var samples = values.ToArray();
        if (samples.Length == 0) return default;
        var sorted = samples.Order().ToArray();
        return new(samples[^1], samples.Average(), sorted[^1], Percentile(sorted, 0.95), samples.Sum(), samples.Length);
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
        double max = Math.Max(unit == "ms" || unit == "B" ? 1 : 5, peak / axis.Scale * 1.15);
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

    public void SetSeries(List<ChartSeries> series, DateTime end)
    {
        _series = series;
        _end = end;
        // Every series is built from the same sample list, so the window's totals are the sum
        // of the series at each observation.
        var times = series.SelectMany(s => s.Samples.Select(sample => sample.Time)).Distinct().Order().ToArray();
        var values = series.Select(s => s.Samples.ToDictionary(sample => sample.Time, sample => sample.Value)).ToArray();
        _stats = ChartStatistics.Of(times.Select(time => values.Sum(s => s.GetValueOrDefault(time))));
        RefreshToolTip();
        Update();
    }
    private string FormatValue(double value) => _unit == "B" ? ByteUnits.Format(value) : value.ToString("0.##", I18n.Culture) + " " + _unit;

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
            int summariesWidth = summaries.Sum(s => detailMetrics.HorizontalAdvance(s.Text) + 28);
            if (summariesWidth <= width - 36 && summaries.Length > 0)
            {
                p.SetFont(detailFont);
                int x = 18;
                foreach (var summary in summaries)
                {
                    // The dot carries the layer's colour and the value stays in ink, so a light
                    // layer hue cannot leave its own number unreadable.
                    p.SetPen(new QPen(Qt.PenStyle.NoPen));
                    p.SetBrush(new QBrush(new QColor(summary.Color)));
                    p.DrawEllipse(x + 1, SeriesBaseline - 6, 6, 6);
                    p.SetPen(new QPen(new QColor("#334155")));
                    p.DrawText(x + 14, SeriesBaseline, summary.Text);
                    x += detailMetrics.HorizontalAdvance(summary.Text) + 28;
                }
            }

            int top = summariesWidth <= width - 36 && summaries.Length > 0 ? 108 : 88, right = width - 18, bottom = height - 46;
            var domain = ChartDomain.Of(_series, _unit, Math.Max(2, (bottom - top) / 36.0));
            double max = domain.Max, valueStep = domain.Step, floor = domain.Floor;
            int gridLines = domain.GridLines;
            using var smallFont = new QFont("Segoe UI", 8);
            p.SetFont(smallFont);
            using var fontMetrics = new QFontMetrics(smallFont);
            int labelWidth = Enumerable.Range(0, gridLines + 1)
                .Max(i => fontMetrics.HorizontalAdvance(TickText(floor + ChartDomain.TickValue(i, valueStep), valueStep)));
            int left = 18 + labelWidth + 10;
            int timeLabelWidth = fontMetrics.HorizontalAdvance("−60s") + 24;
            double timeStep = Math.Max(1, ChartDomain.NiceStep(60 / Math.Max(2, (right - left) / (double)Math.Max(70, timeLabelWidth))));
            p.SetPen(new QPen(new QColor("#718096")));
            string maxLabel = TickText(max, valueStep) + " " + domain.Unit;
            p.DrawText(right - fontMetrics.HorizontalAdvance(maxLabel), top - 8, maxLabel);
            p.DrawText(left, height - 10, I18n.T("stats.history"));
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
            for (int i = 0; i <= (int)Math.Floor(60 / timeStep); i++)
            {
                double seconds = ChartDomain.TickValue(i, timeStep);
                int x = right - (int)Math.Round((right - left) * seconds / 60);
                p.SetPen(gridPen);
                p.DrawLine(x, top, x, bottom);
                string label = (seconds == 0 ? "0" : "−" + TickText(seconds, timeStep)) + "s";
                int textWidth = fontMetrics.HorizontalAdvance(label);
                p.SetPen(labelPen);
                p.DrawText(Math.Clamp(x - textWidth / 2, left, right - textWidth), bottom + 17, label);
            }

            double X(DateTime time) => left + Math.Clamp(1 - (_end - time).TotalSeconds / 60, 0, 1) * (right - left);
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
                while (finish < times.Length && (times[finish] - times[finish - 1]).TotalSeconds <= 2.5) finish++;
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
            if (marker.Time < _end.AddSeconds(-60) || marker.Time > _end) continue;
            int x = (int)Math.Round(left + Math.Clamp(1 - (_end - marker.Time).TotalSeconds / 60, 0, 1) * (right - left));
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
