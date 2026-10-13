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
    private readonly Dictionary<string, List<Sample>> _history = new();
    private List<MountMetrics> _latest = new();
    private Task<(ControlResponse Trends, ControlResponse Access, double RequestMs, long Completed)>? _pending;
    private bool _paused, _requested, _wasVisible;
    private int _epoch, _requestEpoch;
    private DateTime _next;
    private sealed record Sample(DateTime Time, double Reads, double Cost, double Bytes, Dictionary<string, double> Shares);
    private static string T(string key) => I18n.T(key);

    public StatsWindow(string? instanceName = null)
    {
        _instanceName = instanceName;
        SetObjectName("dashboard");
        SetStyleSheet(TrayTheme.StyleSheet);
        SetWindowIcon(TrayIcons.App);
        I18n.Bind(SetWindowTitle, "stats.title");
        Resize(1180, 960);
        SetMinimumSize(900, 700);
        var root = new QVBoxLayout(this);
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
        _mount = new QComboBox();
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
            new PerformanceChart("stats.layerShare", "#059669", "%", 100),
            new PerformanceChart("stats.latency", "#8b5cf6", "ms"),
            new PerformanceChart("stats.bytes", "#0891b2", "KB/s")
        };
        var grid = new QGridLayout();
        grid.SetSpacing(16);
        for (int i = 0; i < _charts.Length; i++) grid.AddWidget(_charts[i], i / 2, i % 2);
        root.AddLayout(grid, 1);
        _layerLegend = new QLabel();
        _layerLegend.SetWordWrap(true);
        _layerLegend.SetTextFormat(Qt.TextFormat.RichText);
        _layerLegend.SetObjectName("muted");
        root.AddWidget(_layerLegend);
        root.AddWidget(Label("stats.share.definition", "muted"));
        var access = new QHBoxLayout();
        _accessPie = new LayerAccessPie();
        access.AddWidget(_accessPie);
        _accessLegend = new QLabel();
        _accessLegend.SetTextFormat(Qt.TextFormat.RichText);
        _accessLegend.SetWordWrap(true);
        access.AddWidget(_accessLegend, 1);
        root.AddLayout(access);
        root.AddWidget(Label("stats.layer", "sectionTitle"));
        _table = new QTableWidget();
        _table.SetColumnCount(7);
        _table.SetEditTriggers(QAbstractItemView.EditTriggers.NoEditTriggers);
        _table.SetShowGrid(false);
        _table.SetSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows);
        _table.VerticalHeader().Hide();
        _table.HorizontalHeader().SetSectionResizeMode(QHeaderView.ResizeMode.Stretch);
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
                    _accessMetrics = sample.Access.Metrics; Accept(response.Metrics);
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

    private void Accept(List<MountMetrics> metrics)
    {
        _latest = metrics;
        DateTime now = DateTime.UtcNow;
        var alive = metrics.Select(m => m.Mount).Append("").ToHashSet();
        foreach (var stale in _history.Keys.Where(k => !alive.Contains(k)).ToArray()) _history.Remove(stale);
        foreach (var mount in metrics)
            Add(mount.Mount, new Sample(now, mount.TotalReads, mount.AvgCostMs, mount.Layers.Sum(l => l.Bytes) / 1024.0,
                mount.Layers.ToDictionary(l => mount.Mount + "/" + l.Name, l => l.SharePercent)));
        double reads = metrics.Sum(m => m.TotalReads);
        Add("", new Sample(now, reads,
            reads == 0 ? 0 : metrics.Sum(m => m.AvgCostMs * m.TotalReads) / reads,
            metrics.Sum(m => m.Layers.Sum(l => l.Bytes)) / 1024.0,
            metrics.SelectMany(m => m.Layers.Select(l => (Key: m.Mount + "/" + l.Name, Share: reads == 0 ? 0 : l.Hits * 100.0 / reads)))
                .ToDictionary(l => l.Key, l => l.Share)));
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
        for (int i = 0; i < _charts.Length; i++)
        {
            if (i == 1) continue;
            int chart = i;
            _charts[i].SetSamples(samples.Select(s => (s.Time, chart switch { 0 => s.Reads, 2 => s.Cost, _ => s.Bytes })).ToList(), end);
        }
        var mounts = _latest.Where(m => selected == "" || m.Mount == selected).ToList();
        var rows = mounts.SelectMany(m => m.Layers.Select(l => (m, l))).ToList();
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
        _charts[1].SetSeries(rows.Select(row => new ChartSeries(
            selected == "" ? row.m.Mount + "/" + row.l.Name : string.IsNullOrEmpty(row.l.DisplayName) ? row.l.Name : row.l.DisplayName,
            string.IsNullOrEmpty(row.l.Color) ? "#64748B" : row.l.Color,
            samples.Where(s => s.Shares.ContainsKey(row.m.Mount + "/" + row.l.Name))
                .Select(s => (s.Time, s.Shares[row.m.Mount + "/" + row.l.Name])).ToList())).ToList(), end);
        string legend = string.Join(" &nbsp; · &nbsp; ", rows.Select(row =>
            $"<span style='color:{(string.IsNullOrEmpty(row.l.Color) ? "#64748B" : row.l.Color)}'>●</span> " +
            System.Net.WebUtility.HtmlEncode(selected == "" ? row.m.Mount + "/" + row.l.Name : string.IsNullOrEmpty(row.l.DisplayName) ? row.l.Name : row.l.DisplayName) +
            " " + row.l.SharePercent.ToString("0.#", I18n.Culture) + "%"));
        if (_layerLegend.Text != legend) _layerLegend.SetText(legend);
        bool structureChanged = _table.RowCount != rows.Count;
        if (structureChanged) { _table.SetUpdatesEnabled(false); _table.SetRowCount(rows.Count); }
        for (int row = 0; row < rows.Count; row++)
        {
            var (mount, layer) = rows[row];
            string[] values = { mount.Mount, string.IsNullOrEmpty(layer.DisplayName) ? layer.Name : layer.DisplayName, layer.Hits.ToString("N0", I18n.Culture), layer.SharePercent.ToString("0.#", I18n.Culture),
                layer.AvgCostMs.ToString("0.##", I18n.Culture), layer.MaxCostMs.ToString("0.##", I18n.Culture), layer.Bytes.ToString("N0", I18n.Culture) };
            for (int col = 0; col < values.Length; col++)
            {
                var item = _table.Item(row, col);
                if (item == null) _table.SetItem(row, col, new QTableWidgetItem(values[col]));
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

/// <summary>Time-scaled, filled line graph. Missing observations are gaps, not fabricated zeroes.</summary>
internal sealed record ChartSeries(string Name, string Color, List<(DateTime Time, double Value)> Samples);

internal sealed class PerformanceChart : QWidget
{
    private readonly string _titleKey, _color, _unit;
    private readonly double _fixedMax;
    private List<(DateTime Time, double Value)> _samples = new();
    private DateTime _end;
    private List<ChartSeries>? _series;
    public PerformanceChart(string titleKey, string color, string unit, double fixedMax = 0)
    {
        _titleKey = titleKey;
        _color = color;
        _unit = unit;
        _fixedMax = fixedMax;
        SetMinimumSize(330, 210);
        I18n.Changed += Update;
    }
    public void SetSamples(List<(DateTime Time, double Value)> samples, DateTime end)
    {
        _samples = samples;
        _end = end;
        Update();
    }
    public void SetSeries(List<ChartSeries> series, DateTime end)
    {
        _series = series;
        _end = end;
        Update();
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
            p.DrawText(18, 29, I18n.T(_titleKey));
            double value = _samples.LastOrDefault().Value;
            using var numberFont = new QFont("Segoe UI", 20);
            numberFont.SetBold(true);
            p.SetFont(numberFont);
            p.SetPen(new QPen(new QColor(_color)));
            p.DrawText(18, 63, _series != null ? I18n.F("stats.series.count", _series.Count)
                : _samples.Count == 0 ? "—" : value.ToString("0.##", I18n.Culture) + " " + _unit);

            int left = 18, top = 88, right = width - 18, bottom = height - 30;
            double max = _fixedMax > 0 ? _fixedMax : Math.Max(1, Math.Ceiling(_samples.Select(s => s.Value).DefaultIfEmpty(0).Max() * 1.15));
            using var smallFont = new QFont("Segoe UI", 8);
            p.SetFont(smallFont);
            p.SetPen(new QPen(new QColor("#718096")));
            p.DrawText(right - 95, top - 8, max.ToString("0.#", I18n.Culture) + " " + _unit);
            p.DrawText(left, height - 10, I18n.T("stats.history"));
            p.DrawText(right - 35, height - 10, I18n.T("stats.now"));
            p.SetPen(new QPen(new QColor("#e7edf5")));
            for (int i = 0; i <= 4; i++) p.DrawLine(left, top + (bottom - top) * i / 4, right, top + (bottom - top) * i / 4);
            for (int i = 0; i <= 6; i++) p.DrawLine(left + (right - left) * i / 6, top, left + (right - left) * i / 6, bottom);

            foreach (var series in _series ?? new List<ChartSeries> { new("", _color, _samples) })
            {
            var segments = new List<List<(DateTime Time, double Value)>>();
            foreach (var sample in series.Samples)
            {
                if (segments.Count == 0 || (sample.Time - segments[^1][^1].Time).TotalSeconds > 2.5) segments.Add(new());
                segments[^1].Add(sample);
            }
            double X(DateTime time) => left + Math.Clamp(1 - (_end - time).TotalSeconds / 60, 0, 1) * (right - left);
            double Y(double number) => bottom - Math.Clamp(number / max, 0, 1) * (bottom - top);
            foreach (var segment in segments)
            {
                using var fill = new QPainterPath();
                fill.MoveTo(X(segment[0].Time), bottom);
                foreach (var sample in segment) fill.LineTo(X(sample.Time), Y(sample.Value));
                fill.LineTo(X(segment[^1].Time), bottom);
                fill.CloseSubpath();
                using var fillColor = new QColor(series.Color);
                fillColor.SetAlpha(_series == null ? 35 : 18);
                p.SetPen(new QPen(Qt.PenStyle.NoPen));
                p.SetBrush(new QBrush(fillColor));
                p.DrawPath(fill);
                using var line = new QPainterPath();
                line.MoveTo(X(segment[0].Time), Y(segment[0].Value));
                foreach (var sample in segment.Skip(1)) line.LineTo(X(sample.Time), Y(sample.Value));
                using var pen = new QPen(new QColor(series.Color));
                pen.SetWidthF(1.8);
                p.SetPen(pen);
                p.SetBrush(new QBrush(Qt.BrushStyle.NoBrush));
                p.DrawPath(line);
            }
            }
            if (_series?.Count == 0 || _series == null && _samples.Count == 0)
            {
                p.SetPen(new QPen(new QColor("#94a3b8")));
                p.DrawText(left + 12, top + 30, I18n.T("stats.noData"));
            }
        }
        finally { p.End(); }
    }
}
