using PolyMount.Core;
using PolyMount.Core.Ipc;
using QtSharp.Api;
using System.Diagnostics;

namespace PolyMount.Tray;

/// <summary>Task-manager style performance charts, sampled asynchronously once per second.</summary>
internal sealed class StatsWindow : QWidget
{
    private readonly QComboBox _mount;
    private readonly string? _instanceName;
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
    private readonly PerformanceChart _outcome;
    private readonly Dictionary<string, List<Sample>> _history = new();
    private List<MountMetrics> _latest = new();
    private Task<(ControlResponse Trends, ControlResponse Access, double RequestMs, long Completed)>? _pending;
    private bool _paused, _requested, _wasVisible;
    private int _epoch, _requestEpoch;
    private DateTime _next;
    private sealed record LayerSample(double Reads, double Cost, double Bytes, double WriteBytes,
        double Used = 0, double Wasted = 0, double Held = 0);
    private sealed record Sample(DateTime Time, Dictionary<string, LayerSample> Layers);
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

        _charts = new[]
        {
            new PerformanceChart("stats.reads", "#2563eb", "/s"),
            new PerformanceChart("stats.layerBytes", "#059669", "B"),
            new PerformanceChart("stats.latency", "#8b5cf6", "ms"),
            new PerformanceChart("stats.bytes", "#0891b2", "B")
        };
        _outcome = new PerformanceChart("stats.bytes", "#059669", "");
        var grid = new QGridLayout();
        grid.SetSpacing(16);
        for (int i = 0; i < _charts.Length; i++) grid.AddWidget(_charts[i], i / 2, i % 2);
        grid.AddWidget(_outcome, _charts.Length / 2, _charts.Length % 2);
        _outcome.SetVisible(false);
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
        _table.SetColumnCount(7);
        _table.SetEditTriggers(QAbstractItemView.EditTriggers.NoEditTriggers);
        _table.SetShowGrid(false);
        _table.SetSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows);
        _table.VerticalHeader().Hide();
        _table.HorizontalHeader().SetSectionResizeMode(QHeaderView.ResizeMode.Stretch);
        _table.HorizontalHeader().SetDefaultAlignment(Qt.Alignment.AlignLeft | Qt.Alignment.AlignVCenter);
        _table.SetMaximumHeight(205);
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
    internal void VerifyResponseSampling()
    {
        if (_responseSamples.Count == 0 || _responseCost.Text == T("stats.response.waiting"))
            throw new InvalidOperationException("Performance window did not collect response overhead samples");
        var sample = CaptureLayers(DateTime.UtcNow, new[] { new MountMetrics { Mount = "test", Layers = new()
        {
            new() { Name = "a", Hits = 2, AvgCostMs = 10, Bytes = 1024 },
            new() { Name = "b", Hits = 3, AvgCostMs = 20, Bytes = 2048, WriteBytes = 4096 },
            new() { Name = "idle" }
        } } });
        if (sample.Layers.Count != 3 || sample.Layers.Values.Sum(l => l.Reads) != 5 ||
            sample.Layers.Values.Sum(l => l.Cost) != 16 || sample.Layers.Values.Sum(l => l.Bytes) != 3072 ||
            sample.Layers.Values.Sum(l => l.WriteBytes) != 4096)
            throw new InvalidOperationException("Layer chart contributions do not sum to the overall metrics");
        if (ByteUnits.Select(1023).Unit != "B" || ByteUnits.Select(1024).Unit != "KiB" ||
            ByteUnits.Select(1048576).Unit != "MiB" || ByteUnits.Select(1073741824).Unit != "GiB")
            throw new InvalidOperationException("Byte unit thresholds are incorrect");
    }

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
                    while (_responseSamples.Count > 60) _responseSamples.Dequeue();
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
                var access = ControlClient.Metrics(60, _instanceName);
                watch.Stop();
                return (trends, access, watch.Elapsed.TotalMilliseconds, Stopwatch.GetTimestamp());
            });
        }
        _wasVisible = visible;
    }

    private void Accept(List<MountMetrics> trends, List<MountMetrics> metrics)
    {
        _latest = metrics;
        DateTime now = DateTime.UtcNow;
        var alive = metrics.Select(m => m.Mount).Append("").ToHashSet();
        foreach (var stale in _history.Keys.Where(k => !alive.Contains(k)).ToArray()) _history.Remove(stale);
        foreach (var mount in trends) Add(mount.Mount, CaptureLayers(now, new[] { mount }));
        Add("", CaptureLayers(now, trends));
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

    private static Sample CaptureLayers(DateTime time, IEnumerable<MountMetrics> mounts)
    {
        var rows = mounts.SelectMany(m => m.Layers.Select(l => (Key: m.Mount + "/" + l.Name, Layer: l))).ToArray();
        double reads = rows.Sum(r => r.Layer.Hits);
        return new(time, rows.ToDictionary(r => r.Key, r => new LayerSample(r.Layer.Hits,
            reads == 0 ? 0 : r.Layer.AvgCostMs * r.Layer.Hits / reads, r.Layer.Bytes, r.Layer.WriteBytes,
            r.Layer.HeldUsed, r.Layer.HeldWasted, r.Layer.HeldCount)));
    }

    private void Add(string name, Sample sample)
    {
        if (!_history.TryGetValue(name, out var samples)) _history[name] = samples = new();
        samples.Add(sample);
        samples.RemoveAll(s => s.Time < sample.Time.AddSeconds(-60));
    }

    private void Render()
    {
        string selected = _instanceName ?? (_mount.CurrentIndex <= 0 ? "" : _mount.CurrentText);
        var samples = _history.GetValueOrDefault(selected) ?? new();
        DateTime end = samples.LastOrDefault()?.Time ?? DateTime.UtcNow;
        var mounts = _latest.Where(m => selected == "" || m.Mount == selected).ToList();
        var rows = mounts.SelectMany(m => m.Layers.Select(l => (m, l))).ToList();
        for (int chart = 0; chart < _charts.Length; chart++)
        {
            int metric = chart;
            _charts[chart].SetSeries(rows.Select(row => new ChartSeries(
                selected == "" ? row.m.Mount + "/" + row.l.Name : string.IsNullOrEmpty(row.l.DisplayName) ? row.l.Name : row.l.DisplayName,
                string.IsNullOrEmpty(row.l.Color) ? "#64748B" : row.l.Color,
                samples.Select(s =>
                {
                    var layer = s.Layers.GetValueOrDefault(row.m.Mount + "/" + row.l.Name);
                    return (s.Time, layer == null ? 0 : metric switch { 0 => layer.Reads, 1 => layer.Bytes, 2 => layer.Cost, _ => layer.WriteBytes });
                }).ToList())).ToList(), end);
        }
        var outcomeMount = mounts.FirstOrDefault(m => m.PredictiveTitle != null);
        _outcome.SetVisible(outcomeMount != null);
        if (outcomeMount != null)
        {
            string key = outcomeMount.Mount + "/prediction-cache";
            _outcome.SetTitle(() => outcomeMount.PredictiveTitle?.Resolve(I18n.Language));
            var outcomes = new (LocalizedText? Text, string Color)[]
            {
                (outcomeMount.PredictiveUsed, "#059669"), (outcomeMount.PredictiveWasted, "#dc2626"), (outcomeMount.PredictiveHeld, "#94a3b8")
            };
            _outcome.SetSeries(outcomes.Select((slice, index) => new ChartSeries(
                slice.Text?.Resolve(I18n.Language) ?? "", slice.Color,
                samples.Select(s =>
                {
                    var layer = s.Layers.GetValueOrDefault(key);
                    return (s.Time, layer == null ? 0 : index switch { 0 => layer.Used, 1 => layer.Wasted, _ => layer.Held });
                }).ToList())).ToList(), end);
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
            string[] values = { mount.Mount, string.IsNullOrEmpty(layer.DisplayName) ? layer.Name : layer.DisplayName, layer.Hits.ToString("N0", I18n.Culture), layer.SharePercent.ToString("0.#", I18n.Culture),
                layer.AvgCostMs.ToString("0.##", I18n.Culture), layer.MaxCostMs.ToString("0.##", I18n.Culture), ByteUnits.Format(layer.Bytes) };
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
            _table.SetRowHeight(row, 36);
        }
        if (structureChanged) _table.SetUpdatesEnabled(true);
    }

    private void Translate()
    {
        _pause.SetText(T(_paused ? "stats.resume" : "stats.pause"));
        _pause.SetIcon(TrayIcons.Get(_paused ? "play" : "stop"));
        _mount.SetItemText(0, T("stats.all"));
        _table.SetHorizontalHeaderLabels(new[] { T("mount.instances"), T("layer"), T("hits"), T("share"), T("avg"), T("max"), T("bytes") });
        _status.SetText(T(_paused ? "stats.paused" : "stats.live"));
        Render();
        RenderResponseCost();
    }
    private void RenderResponseCost()
    {
        if (_responseSamples.Count == 0) { _responseCost.SetText(T("stats.response.waiting")); return; }
        var requests = _responseSamples.Select(s => s.Request).Order().ToArray();
        double p95 = requests[Math.Clamp((int)Math.Ceiling(requests.Length * .95) - 1, 0, requests.Length - 1)];
        _responseCost.SetText(I18n.F("stats.response.cost", requests.Average(), p95,
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
internal sealed record ChartSeries(string Name, string Color, List<(DateTime Time, double Value)> Samples);

internal sealed class PerformanceChart : QWidget
{
    private readonly string _titleKey, _color, _unit;
    private Func<string>? _title;
    private DateTime _end;
    private List<ChartSeries> _series = new();
    public PerformanceChart(string titleKey, string color, string unit)
    {
        _titleKey = titleKey;
        _color = color;
        _unit = unit;
        SetMinimumSize(330, 210);
    }
    public override void ChangeEvent(QEvent e)
    { base.ChangeEvent(e); if (e.type() == QEvent.Type.LanguageChange) Update(); }
    /// <summary>Overrides the tray-catalog title for a chart worded by the core.</summary>
    public void SetTitle(Func<string>? title) { _title = title; Update(); }
    public void SetSeries(List<ChartSeries> series, DateTime end)
    {
        _series = series;
        _end = end;
        SetToolTip(string.Join("\n", series.Select(s => s.Name + ": " + FormatValue(s.Samples.LastOrDefault().Value))));
        Update();
    }
    private string FormatValue(double value) => _unit == "B" ? ByteUnits.Format(value) : value.ToString("0.##", I18n.Culture) + " " + _unit;
    private static double NiceStep(double target)
    {
        // D3 tickStep: nearest 1/2/5 decade, using geometric-mean thresholds.
        // https://d3js.org/d3-array/ticks
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(target)));
        double fraction = target / magnitude;
        return magnitude * (fraction < Math.Sqrt(2) ? 1 : fraction < Math.Sqrt(10) ? 2 : fraction < Math.Sqrt(50) ? 5 : 10);
    }

    private static double TickValue(int index, double step) => step < 1 ? index / Math.Round(1 / step) : index * step;

    private static string TickText(double value, double step)
    {
        int decimals = Math.Clamp(-(int)Math.Floor(Math.Log10(step)), 0, 15);
        return value.ToString("F" + decimals, I18n.Culture);
    }

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
            p.DrawText(18, 29, _title?.Invoke() ?? I18n.T(_titleKey));
            var times = _series.SelectMany(s => s.Samples.Select(sample => sample.Time)).Distinct().Order().ToArray();
            var valuesBySeries = _series.Select(s => s.Samples.ToDictionary(sample => sample.Time, sample => Math.Max(0, sample.Value))).ToArray();
            var totals = times.Select(time => valuesBySeries.Sum(s => s.GetValueOrDefault(time))).ToArray();
            double value = totals.LastOrDefault();
            using var numberFont = new QFont("Segoe UI", 20);
            numberFont.SetBold(true);
            p.SetFont(numberFont);
            p.SetPen(new QPen(new QColor(_color)));
            p.DrawText(18, 63, times.Length == 0 ? "—" : FormatValue(value));

            using var detailFont = new QFont("Segoe UI", 8);
            using var detailMetrics = new QFontMetrics(detailFont);
            var summaries = _series.Select(s => (s.Color, Text: s.Name + " " + FormatValue(s.Samples.LastOrDefault().Value))).ToArray();
            int summariesWidth = summaries.Sum(s => detailMetrics.HorizontalAdvance(s.Text) + 18);
            if (summariesWidth <= width - 36)
            {
                p.SetFont(detailFont);
                int x = 18;
                foreach (var summary in summaries)
                {
                    p.SetPen(new QPen(new QColor(summary.Color)));
                    p.DrawText(x, 80, summary.Text);
                    x += detailMetrics.HorizontalAdvance(summary.Text) + 18;
                }
            }

            int top = summariesWidth <= width - 36 && summaries.Length > 0 ? 108 : 88, right = width - 18, bottom = height - 46;
            double peak = totals.DefaultIfEmpty(0).Max();
            var axis = _unit == "B" ? ByteUnits.Select(peak) : (Scale: 1.0, Unit: _unit);
            double max = Math.Max(_unit == "ms" || _unit == "B" ? 1 : 5, peak / axis.Scale * 1.15);
            double targetIntervals = Math.Max(2, (bottom - top) / 36.0);
            double valueStep = 0;
            int valueIntervals = 0;
            // D3 nice: round the domain outward, then recompute until the step stabilizes.
            for (int pass = 0; pass < 10; pass++)
            {
                double step = NiceStep(max / targetIntervals);
                if (_unit != "ms" && _unit != "B") step = Math.Max(1, step);
                if (step == valueStep) break;
                valueStep = step;
                valueIntervals = (int)Math.Ceiling(step < 1 ? max * Math.Round(1 / step) : max / step);
                max = TickValue(valueIntervals, step);
            }
            using var smallFont = new QFont("Segoe UI", 8);
            p.SetFont(smallFont);
            using var fontMetrics = new QFontMetrics(smallFont);
            int labelWidth = Enumerable.Range(0, valueIntervals + 1)
                .Max(i => fontMetrics.HorizontalAdvance(TickText(TickValue(i, valueStep), valueStep)));
            int left = 18 + labelWidth + 10;
            int timeLabelWidth = fontMetrics.HorizontalAdvance("−60s") + 24;
            double timeStep = Math.Max(1, NiceStep(60 / Math.Max(2, (right - left) / (double)Math.Max(70, timeLabelWidth))));
            p.SetPen(new QPen(new QColor("#718096")));
            string maxLabel = TickText(max, valueStep) + " " + axis.Unit;
            p.DrawText(right - fontMetrics.HorizontalAdvance(maxLabel), top - 8, maxLabel);
            p.DrawText(left, height - 10, I18n.T("stats.history"));
            p.DrawText(right - fontMetrics.HorizontalAdvance(I18n.T("stats.now")), height - 10, I18n.T("stats.now"));
            using var gridPen = new QPen(new QColor("#e7edf5"));
            using var labelPen = new QPen(new QColor("#718096"));
            for (int i = 0; i <= valueIntervals; i++)
            {
                int y = bottom - (int)Math.Round((bottom - top) * i / (double)valueIntervals);
                p.SetPen(gridPen);
                p.DrawLine(left, y, right, y);
                string label = TickText(TickValue(i, valueStep), valueStep);
                p.SetPen(labelPen);
                p.DrawText(left - 10 - fontMetrics.HorizontalAdvance(label), y + 4, label);
            }
            for (int i = 0; i <= (int)Math.Floor(60 / timeStep); i++)
            {
                double seconds = TickValue(i, timeStep);
                int x = right - (int)Math.Round((right - left) * seconds / 60);
                p.SetPen(gridPen);
                p.DrawLine(x, top, x, bottom);
                string label = (seconds == 0 ? "0" : "−" + TickText(seconds, timeStep)) + "s";
                int textWidth = fontMetrics.HorizontalAdvance(label);
                p.SetPen(labelPen);
                p.DrawText(Math.Clamp(x - textWidth / 2, left, right - textWidth), bottom + 17, label);
            }

            double X(DateTime time) => left + Math.Clamp(1 - (_end - time).TotalSeconds / 60, 0, 1) * (right - left);
            double Y(double number) => bottom - Math.Clamp(number / axis.Scale / max, 0, 1) * (bottom - top);
            var baseline = new double[times.Length];
            // The last provider is the bottom of the chain and of the area stack.
            for (int layer = _series.Count - 1; layer >= 0; layer--)
            {
                var series = _series[layer];
                var upper = times.Select((time, index) => baseline[index] + valuesBySeries[layer].GetValueOrDefault(time)).ToArray();
                int start = 0;
                while (start < times.Length)
                {
                int finish = start + 1;
                while (finish < times.Length && (times[finish] - times[finish - 1]).TotalSeconds <= 2.5) finish++;
                using var fill = new QPainterPath();
                fill.MoveTo(X(times[start]), Y(baseline[start]));
                for (int index = start; index < finish; index++) fill.LineTo(X(times[index]), Y(upper[index]));
                for (int index = finish - 1; index >= start; index--) fill.LineTo(X(times[index]), Y(baseline[index]));
                fill.CloseSubpath();
                using var fillColor = new QColor(series.Color);
                fillColor.SetAlpha(155);
                p.SetPen(new QPen(Qt.PenStyle.NoPen));
                p.SetBrush(new QBrush(fillColor));
                p.DrawPath(fill);
                using var line = new QPainterPath();
                line.MoveTo(X(times[start]), Y(upper[start]));
                for (int index = start + 1; index < finish; index++) line.LineTo(X(times[index]), Y(upper[index]));
                using var pen = new QPen(new QColor(series.Color));
                pen.SetWidthF(1.8);
                p.SetPen(pen);
                p.SetBrush(new QBrush(Qt.BrushStyle.NoBrush));
                p.DrawPath(line);
                start = finish;
                }
                baseline = upper;
            }
            if (times.Length == 0)
            {
                p.SetPen(new QPen(new QColor("#94a3b8")));
                p.DrawText(left + 12, top + 30, I18n.T("stats.noData"));
            }
        }
        finally { p.End(); }
    }
}
