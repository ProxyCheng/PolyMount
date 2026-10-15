using PolyMount.Core.Ipc;
using QtSharp.Api;

namespace PolyMount.Tray;

/// <summary>
/// The recording page: opening it starts collecting, and it keeps collecting until the reader
/// saves or ends it. What it shows is evidence rather than decoration — every source it is
/// copying, how much of each it holds, and every stretch it knows is missing.
/// </summary>
internal sealed class RecordingWindow : QWidget, ICloseGuard
{
    /// <summary>What the live charts cover. The recording itself has no horizon; a per-second
    /// series hours long is not something a chart 900 pixels wide can say anything about, and the
    /// whole-span facts belong in the table and the bundle.</summary>
    private const int ChartSeconds = 60;
    private const int TableRowHeight = 34;
    private const int EvidenceRows = 4;
    private const int LayerColumns = 8;
    private static string T(string key) => I18n.T(key);

    private readonly string _name;
    private readonly QPushButton _save = new(), _discard = new(), _restart = new();
    private readonly RecordingLamp _lamp = new();
    private readonly QLabel _state = new(), _elapsed = new(), _range = new(), _collected = new();
    private readonly QLabel _capacityLine = new(), _working = new(), _saved = new(), _message = new();
    private readonly QLabel _heading = new();
    private readonly QTableWidget _evidence, _gaps, _layers;
    private readonly PerformanceChart[] _charts;
    private readonly Dictionary<string, List<LayerSampler.Sample>> _history = new();
    private readonly QTimer _timer;
    private RecordingSession? _session;
    private Task<RecordingPulse>? _pending;
    private Task<ExportOutcome>? _pendingSave;
    private RecordingPulse? _latest;
    private string _saveDirectory;
    private string _lastSaved = "", _startError = "", _transient = "";
    private DateTime _next = DateTime.MinValue;
    /// <summary>Set by a smoke run: the page then starts nothing and never opens a dialog, so a
    /// scripted run of the whole UI cannot leave a session behind or block on a question.</summary>
    internal bool Quiet;

    public RecordingWindow(string name, bool start = true)
    {
        _name = name;
        _saveDirectory = StatsExport.LoadDirectory();
        SetObjectName("dashboard");
        SetStyleSheet(TrayTheme.StyleSheet);
        SetWindowIcon(TrayIcons.App);
        I18n.Bind(SetWindowTitle, "record.title");
        Resize(1180, 980);
        var root = new QVBoxLayout(WindowScrollArea.Create(this, 900, 900));
        root.SetContentsMargins(24, 22, 24, 20);
        root.SetSpacing(14);

        var header = new QHBoxLayout();
        var titles = new QVBoxLayout();
        _heading.SetObjectName("pageTitle");
        _heading.SetText(I18n.F("record.instance", name));
        titles.AddWidget(_heading);
        titles.AddWidget(Label("record.subtitle", "muted"));
        header.AddLayout(titles);
        header.AddStretch(1);
        _restart.SetIcon(TrayIcons.Get("play"));
        _restart.Clicked += _ => Begin();
        header.AddWidget(_restart);
        _discard.SetIcon(TrayIcons.Get("stop"));
        _discard.Clicked += _ => Discard();
        header.AddWidget(_discard);
        _save.SetObjectName("primary");
        _save.SetIcon(TrayIcons.Get("save", "#ffffff"));
        _save.Clicked += _ => Save();
        header.AddWidget(_save);
        root.AddLayout(header);

        root.AddWidget(BuildStatus());
        root.AddWidget(Label("record.hint", "muted"));

        var evidenceHeading = new QHBoxLayout();
        evidenceHeading.AddWidget(Label("record.evidence", "sectionTitle"));
        evidenceHeading.AddStretch(1);
        _capacityLine.SetObjectName("muted");
        evidenceHeading.AddWidget(_capacityLine);
        root.AddLayout(evidenceHeading);
        _evidence = ContinuousScrolling.Table();
        _evidence.SetColumnCount(5);
        Prepare(_evidence);
        _evidence.SetFixedHeight(HeaderRows(EvidenceRows));
        root.AddWidget(_evidence);

        _gaps = ContinuousScrolling.Table();
        _gaps.SetColumnCount(3);
        Prepare(_gaps);
        _gaps.Hide();
        root.AddWidget(_gaps);

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
        root.AddLayout(grid, 1);
        root.AddWidget(Label("record.charts", "muted"));

        root.AddWidget(Label("record.totals", "sectionTitle"));
        _layers = ContinuousScrolling.Table();
        _layers.SetColumnCount(LayerColumns);
        Prepare(_layers);
        root.AddWidget(_layers);

        root.AddWidget(Prepare(_message));
        root.AddWidget(Selectable(_working));
        root.AddWidget(Selectable(_saved));

        _timer = new QTimer(this);
        _timer.Timeout += Tick;
        _timer.Start(200);
        Translate();
        if (start) Begin();
    }

    private QWidget BuildStatus()
    {
        var card = new QWidget();
        card.SetObjectName("surface");
        card.SetAttribute(Qt.WidgetAttribute.WA_StyledBackground);
        var layout = new QHBoxLayout(card);
        layout.SetContentsMargins(18, 14, 18, 14);
        layout.SetSpacing(14);
        layout.AddWidget(_lamp);
        var texts = new QVBoxLayout();
        texts.SetSpacing(4);
        _state.SetObjectName("metricValue");
        texts.AddWidget(_state);
        _collected.SetObjectName("muted");
        texts.AddWidget(_collected);
        layout.AddLayout(texts, 1);
        var times = new QVBoxLayout();
        times.SetSpacing(4);
        _elapsed.SetText("00:00");
        var font = new QFont("Consolas", 24);
        font.SetBold(true);
        _elapsed.SetFont(font);
        _elapsed.SetAlignment(Qt.Alignment.AlignRight | Qt.Alignment.AlignVCenter);
        times.AddWidget(_elapsed);
        _range.SetObjectName("muted");
        _range.SetAlignment(Qt.Alignment.AlignRight | Qt.Alignment.AlignVCenter);
        times.AddWidget(_range);
        layout.AddLayout(times);
        return card;
    }

    private static void Prepare(QTableWidget table)
    {
        table.SetEditTriggers(QAbstractItemView.EditTriggers.NoEditTriggers);
        table.SetShowGrid(false);
        table.SetSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows);
        table.VerticalHeader().Hide();
        table.HorizontalHeader().SetSectionResizeMode(QHeaderView.ResizeMode.Stretch);
        table.HorizontalHeader().SetDefaultAlignment(Qt.Alignment.AlignLeft | Qt.Alignment.AlignVCenter);
        table.SetVerticalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff);
    }

    private static int HeaderRows(int rows) => rows * TableRowHeight + 34;

    private static QLabel Label(string key, string name)
    {
        var label = new QLabel();
        label.SetObjectName(name);
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

    /// <summary>Prepared once at construction: the page's own labels are reused for the life of
    /// the page, so the translation bindings are installed by the helpers below.</summary>
    private static QLabel Prepare(QLabel label)
    {
        label.SetObjectName("muted");
        label.SetWordWrap(true);
        label.SetTextFormat(Qt.TextFormat.PlainText);
        return label;
    }

    /// <summary>Text the reader may want to paste somewhere, so it selects.</summary>
    private static QLabel Selectable(QLabel label)
    {
        label.SetTextInteractionFlags(Qt.TextInteractionFlags.TextSelectableByMouse);
        return label;
    }

    internal void Capture(string path) { using var image = Grab(); image.Save(path); }

    /// <summary>Starts a recording, or says why it could not. A page that refuses to start still
    /// explains itself, rather than looking like a recording that is doing nothing.</summary>
    private void Begin()
    {
        _session?.Suspend();
        RecordingSessions.Forget(_session);
        _session = null;
        _startError = "";
        _lastSaved = "";
        _transient = "";
        _latest = null;
        _history.Clear();
        try
        {
            _session = RecordingSession.Start(_name);
            RecordingSessions.Track(_session);
        }
        catch (Exception ex) { _startError = ex.Message; }
        _next = DateTime.MinValue;
        Render();
    }

    /// <summary>Only a recording that is still running, or one that has not started, is polled:
    /// a session the service ended keeps what it has and waits to be saved.</summary>
    private bool Collecting => _session != null &&
        (_latest == null || _latest.State is RecordingState.Recording or RecordingState.Unreachable);

    private void Tick()
    {
        if (_pending is { IsCompleted: true })
        {
            var task = _pending;
            _pending = null;
            try { _latest = task.GetAwaiter().GetResult(); }
            catch (Exception ex) { _startError = ex.Message; }
            if (_latest?.Sample is { } sample) LayerSampler.Add(_history, _name, sample, ChartSeconds);
            if (IsVisible()) Render();
        }
        if (_pendingSave is { IsCompleted: true })
        {
            var task = _pendingSave;
            _pendingSave = null;
            ReportSave(task);
        }
        // Deliberately not gated on IsVisible(): the recording has to keep collecting while its
        // tab is not the one being looked at, which is most of the time it runs.
        if (_pending == null && _pendingSave == null && Collecting && DateTime.UtcNow >= _next)
        {
            _next = DateTime.UtcNow.AddSeconds(1);
            var session = _session!;
            _pending = Task.Run(() => session.Poll(DateTime.UtcNow));
        }
    }

    /// <summary>Writes the whole recording out and ends it. The folder is chosen before the
    /// session is frozen: the bundle describes the moment it was frozen, and a cancelled dialog
    /// has to leave a recording running rather than one that stopped because a folder was asked for.</summary>
    private void Save()
    {
        var session = _session;
        if (session == null || _pendingSave != null) return;
        string? parent = QFileDialog.GetExistingDirectory(this, T("record.save.choose"), _saveDirectory);
        if (string.IsNullOrEmpty(parent)) return;
        _saveDirectory = parent;
        StatsExport.SaveDirectory(parent);
        _save.SetEnabled(false);
        _discard.SetEnabled(false);
        _transient = T("record.save.running");
        Render();
        _pendingSave = Task.Run(() =>
        {
            var submission = session.Freeze(DateTime.UtcNow);
            return StatsExport.Write(parent, submission.ToSnapshot(I18n.Language));
        });
    }

    private void ReportSave(Task<ExportOutcome> task)
    {
        try
        {
            var outcome = task.GetAwaiter().GetResult();
            _lastSaved = outcome.Directory;
            _transient = I18n.F("record.save.done", outcome.Directory);
            bool opened = Quiet || QMessageBox.Information(this, T("record.save.title"),
                I18n.F("record.save.body", System.Net.WebUtility.HtmlEncode(outcome.Directory), outcome.Samples, outcome.Logs),
                T("stats.export.open"), T("stats.export.close"), null, 0, 1) == 0;
            if (opened && !Quiet) QDesktopServices.OpenUrl(QUrl.FromLocalFile(outcome.Directory));
        }
        catch (Exception ex) { _transient = T("record.save.failed") + ex.Message; }
        Render();
    }

    private void Discard()
    {
        var session = _session;
        if (session == null) return;
        if (!Quiet && QMessageBox.Question(this, T("record.discard.title"), T("record.discard.body"),
                T("record.discard.confirm"), T("cancel"), null, 1, 1) != 0) return;
        session.Discard();
        RecordingSessions.Forget(session);
        _session = null;
        _latest = null;
        _transient = T("record.discarded");
        Render();
    }

    /// <summary>Closing the tab while a recording runs is a plausible way of saying "stop" and a
    /// plausible accident. It asks, because the evidence is the whole point of the page.</summary>
    void ICloseGuard.RequestClose(Action proceed)
    {
        var session = _session;
        if (Quiet || session == null || _latest?.State is RecordingState.Stopped or RecordingState.Truncated)
        {
            Close(proceed, session);
            return;
        }
        if (QMessageBox.Question(this, T("record.close.title"), I18n.F("record.close.body", Elapsed()),
                T("record.close.keep"), T("record.close.discard"), null, 0, 0) == 0) return;
        Close(proceed, session);
    }

    private void Close(Action proceed, RecordingSession? session)
    {
        if (session != null)
        {
            session.Discard();
            RecordingSessions.Forget(session);
            _session = null;
        }
        proceed();
    }

    private void Render()
    {
        var pulse = _latest;
        string state = _startError.Length > 0 ? "record.state.failed"
            : _session == null ? "record.state.idle"
            : pulse == null ? "record.state.starting"
            : pulse.State switch
            {
                RecordingState.Recording => "record.state.recording",
                RecordingState.Unreachable => "record.state.unreachable",
                RecordingState.Truncated => "record.state.truncated",
                _ => "record.state.stopped"
            };
        _state.SetText(T(state) + " · " + _name);
        _lamp.SetMode(pulse?.State == RecordingState.Recording ? RecordingLamp.LampMode.Live : RecordingLamp.LampMode.Idle);
        _elapsed.SetText(Elapsed());
        _range.SetText(_session == null || pulse == null ? "" : I18n.F("record.range",
            _session.StartedUtc.ToLocalTime().ToString("HH:mm:ss"), pulse.Time.ToLocalTime().ToString("HH:mm:ss")));
        bool ended = _session == null || pulse?.State is RecordingState.Stopped or RecordingState.Truncated;
        _save.SetEnabled(!ended && _pendingSave == null);
        _discard.SetEnabled(!ended);
        _restart.SetEnabled(ended && _pendingSave == null);
        _restart.SetText(T(_session == null ? "record.start" : "record.restart"));
        _collected.SetText(pulse == null ? T("record.starting") : I18n.F("record.collected",
            ByteUnits.Format(pulse.RequestBytes), pulse.RequestLines, pulse.SecondRows));
        RenderEvidence(pulse);
        RenderGaps(pulse);
        RenderTotals(pulse);
        RenderCharts(pulse);
        string message = _transient.Length > 0 ? _transient
            : _startError.Length > 0 ? T("record.start.failed") + _startError
            : pulse?.Error is { Length: > 0 } error ? T("record.state.unreachable") + " · " + error
            : pulse?.Note is { Length: > 0 } recorded ? T("record.note") + recorded
            : T(_session == null ? "record.idle" : "record.recording");
        if (_message.Text != message) _message.SetText(message);
        _working.SetText(_session == null ? "" : I18n.F("record.working", _session.WorkingDirectory));
        _saved.SetText(_lastSaved.Length == 0 ? "" : I18n.F("record.savedTo", _lastSaved));
    }

    /// <summary>What is being collected, and how much of it there is. This is the page's claim
    /// that the recording is complete, so it names each part and whether anything went wrong.</summary>
    private void RenderEvidence(RecordingPulse? pulse)
    {
        string status = T(pulse?.State switch
        {
            RecordingState.Recording => "record.evidence.live",
            RecordingState.Unreachable => "record.evidence.waiting",
            RecordingState.Truncated => "record.evidence.truncated",
            RecordingState.Stopped => "record.evidence.closed",
            _ => "record.evidence.idle"
        });
        var rows = new[]
        {
            new[] { T("record.evidence.requests"), _name + "/requests-000.log", ByteUnits.Format(pulse?.RequestBytes ?? 0),
                Count(pulse?.RequestLines ?? 0), status },
            new[] { T("record.evidence.service"), "service/polymount-service-000.log", ByteUnits.Format(pulse?.ServiceBytes ?? 0),
                Count(pulse?.ServiceLines ?? 0), status },
            new[] { T("record.evidence.samples"), "samples.csv", "", Count(pulse?.SampleRows ?? 0), status },
            new[] { T("record.evidence.seconds"), "seconds.csv", "", Count(pulse?.SecondRows ?? 0), status }
        };
        Fill(_evidence, rows.Length, index => rows[index]);
        long capacity = pulse?.CapacityBytes ?? 0;
        long used = pulse?.RequestBytes ?? 0;
        _capacityLine.SetText(capacity == 0 ? "" : I18n.F("record.capacity", ByteUnits.Format(used), ByteUnits.Format(capacity)));
        // Near its ceiling the figure turns red where it stands: by the time the channel stops
        // itself the reader should already have seen it coming.
        _capacityLine.SetStyleSheet(capacity > 0 && used * 5 > capacity * 4 ? "color:#dc2626;" : "color:#718096;");
    }

    private string Count(long value) => value.ToString("N0", I18n.Culture);

    /// <summary>Every stretch the copy knows is missing. Empty is the normal case, and then the
    /// table is not there at all: a heading that is always empty teaches the reader to skip it.</summary>
    private void RenderGaps(RecordingPulse? pulse)
    {
        var gaps = pulse?.Gaps ?? Array.Empty<TailGap>();
        _gaps.SetVisible(gaps.Count > 0);
        if (gaps.Count == 0) return;
        _gaps.SetFixedHeight(HeaderRows(Math.Min(gaps.Count, 6)));
        Fill(_gaps, gaps.Count, row => new[]
        {
            Local(gaps[row].FromUtc) + " → " + Local(gaps[row].ToUtc),
            gaps[row].Reason,
            gaps[row].MissingBytes is { } bytes ? ByteUnits.Format(bytes) : "—"
        });
    }

    private static string Local(string utc) => DateTimeOffset.TryParse(utc, out var at)
        ? at.ToLocalTime().ToString("HH:mm:ss") : utc;

    private void RenderTotals(RecordingPulse? pulse)
    {
        var totals = pulse?.Totals ?? Array.Empty<LayerTotalRecord>();
        _layers.SetVisible(totals.Count > 0);
        if (totals.Count == 0) return;
        _layers.SetFixedHeight(HeaderRows(totals.Count));
        long reads = totals.Sum(total => total.Reads);
        Fill(_layers, totals.Count, row =>
        {
            var total = totals[row];
            return new[]
            {
                string.IsNullOrEmpty(total.DisplayName) ? total.Layer : total.DisplayName,
                Count(total.Reads),
                (reads == 0 ? 0 : 100.0 * total.Reads / reads).ToString("0.#", I18n.Culture),
                total.AvgCostMs.ToString("0.##", I18n.Culture),
                total.MaxCostMs.ToString("0.##", I18n.Culture),
                ByteUnits.Format(total.Bytes),
                ByteUnits.Format(total.WriteBytes),
                total.Accelerating ? I18n.F("stats.accelerated.cell", total.AcceleratedPercent, ByteUnits.Format(total.AcceleratedBytes)) : ""
            };
        });
    }

    private void RenderCharts(RecordingPulse? pulse)
    {
        var samples = _history.GetValueOrDefault(_name) ?? new();
        DateTime end = samples.LastOrDefault()?.Time ?? DateTime.UtcNow;
        var totals = pulse?.Totals ?? Array.Empty<LayerTotalRecord>();
        for (int chart = 0; chart < _charts.Length; chart++)
        {
            int metric = chart;
            var series = totals.Select(total => new ChartSeries(
                string.IsNullOrEmpty(total.DisplayName) ? total.Layer : total.DisplayName,
                string.IsNullOrEmpty(total.Color) ? "#64748B" : total.Color,
                samples.Select(sample =>
                {
                    var layer = sample.Layers.GetValueOrDefault(_name + "/" + total.Layer);
                    return (sample.Time, layer == null ? 0 : metric switch
                    {
                        0 => layer.Reads,
                        1 => layer.Bytes,
                        2 => layer.Cost,
                        _ => layer.WriteBytes
                    });
                }).ToList())).ToList();
            _charts[chart].SetSeries(series, end);
        }
    }

    /// <summary>Fills a table from a row factory, reusing the items so a page that redraws every
    /// second does not churn the widget tree.</summary>
    private static void Fill(QTableWidget table, int count, Func<int, string[]> row)
    {
        bool changed = table.RowCount != count;
        if (changed) { table.SetUpdatesEnabled(false); table.SetRowCount(count); }
        for (int index = 0; index < count; index++)
        {
            var values = row(index);
            for (int column = 0; column < values.Length && column < table.ColumnCount; column++)
            {
                var item = table.Item(index, column);
                if (item == null)
                {
                    item = new QTableWidgetItem(values[column]);
                    item.SetTextAlignment(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter);
                    table.SetItem(index, column, item);
                }
                else if (item.Text() != values[column]) item.SetText(values[column]);
            }
            table.SetRowHeight(index, TableRowHeight);
        }
        if (changed) table.SetUpdatesEnabled(true);
    }

    private string Elapsed()
    {
        var started = _session?.StartedUtc;
        if (started == null) return "00:00";
        var span = (_latest?.Time ?? DateTime.UtcNow) - started.Value;
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    private void Translate()
    {
        _heading.SetText(I18n.F("record.instance", _name));
        _save.SetText(T("record.save"));
        _discard.SetText(T("record.discard"));
        _restart.SetText(T(_session == null ? "record.start" : "record.restart"));
        _evidence.SetHorizontalHeaderLabels(new[]
        {
            T("record.evidence.kind"), T("record.evidence.file"), T("record.evidence.bytes"),
            T("record.evidence.lines"), T("record.evidence.state")
        });
        _gaps.SetHorizontalHeaderLabels(new[] { T("record.gap.range"), T("record.gap.reason"), T("record.gap.missing") });
        _layers.SetHorizontalHeaderLabels(new[]
        {
            T("layer"), T("hits"), T("share"), T("avg"), T("max"), T("bytes"), T("record.writes"), T("stats.accelerated")
        });
        Render();
    }

    public override void ChangeEvent(QEvent e)
    {
        base.ChangeEvent(e);
        if (e.type() == QEvent.Type.LanguageChange && _layers != null) Translate();
    }

    /// <summary>Self-check for a smoke run. The page describes a recording through three tables
    /// and a set of strings, and a run with no service cannot produce either, so this renders a
    /// synthetic second and asserts what a reader would then see.</summary>
    internal void Verify()
    {
        if (_evidence.ColumnCount != 5 || _evidence.RowCount != EvidenceRows)
            throw new InvalidOperationException($"Recording evidence table shows {_evidence.RowCount}×{_evidence.ColumnCount}");
        if (_layers.ColumnCount != LayerColumns)
            throw new InvalidOperationException("Recording layer table lost a column");
        if (I18n.T("record.instance") == "record.instance")
            throw new InvalidOperationException("Recording strings are not in the translation catalog");
        // Asked for by name, so a glyph that silently fell back to the default one is caught here
        // rather than by a reader wondering why the record button looks like a stack of layers.
        foreach (string icon in new[] { "record", "save" })
            if (TrayIcons.Get(icon).Handle == TrayIcons.Get("no-such-glyph").Handle)
                throw new InvalidOperationException($"Recording icon '{icon}' is not drawn");
        bool wasQuiet = Quiet;
        Quiet = true;
        _latest = Synthetic();
        Render();
        Quiet = wasQuiet;
        if (_layers.RowCount != 3) throw new InvalidOperationException($"Recording layer totals show {_layers.RowCount} rows of 3");
        if (_evidence.Item(0, 0)?.Text() != T("record.evidence.requests"))
            throw new InvalidOperationException("Recording evidence table does not name the request channel");
        // The evidence table is the page's account of what it holds, so the figures have to be the
        // ones it was given: the size of the request channel and the rows of each sample file.
        if (_evidence.Item(0, 2)?.Text() != ByteUnits.Format(4096) || _evidence.Item(0, 3)?.Text() != Count(40))
            throw new InvalidOperationException("Recording evidence table does not report the request channel's size and rows");
        if (_evidence.Item(2, 3)?.Text() != Count(20))
            throw new InvalidOperationException("Recording evidence table does not count the samples it holds");
        if (_layers.Item(2, 7)!.Text().Length == 0)
            throw new InvalidOperationException("A block cache's row does not report what it kept out of the stream");
        // Whatever it is doing, the page says so in one of the two sentences that describe it.
        if (_message.Text != T("record.idle") && _message.Text != T("record.recording"))
            throw new InvalidOperationException("Recording page does not say what it is doing: " + _message.Text);
    }

    /// <summary>One second as a recording of two layers would report it, for the check above.</summary>
    private RecordingPulse Synthetic()
    {
        var now = DateTime.UtcNow;
        var totals = new List<LayerTotalRecord>
        {
            new(_name, "svn", "SVN", "#2563eb", false, 120, 4096, 0, 12.5, 40, 0, 0),
            new(_name, "cache", "Cache", "#059669", false, 30, 1024, 0, 1.5, 8, 0, 0),
            new(_name, "ram", "Memory", "#0891b2", true, 0, 0, 0, 0, 0, 75, 6144)
        };
        return new RecordingPulse(RecordingState.Recording, null, now, 3.5, null, new List<MountMetrics>(),
            new ServiceProcessReading(1, now.ToString("O"), 10, 12, 4096, 8192, 8, 100),
            4096, 40, 1024, 20, 20, 7, 0, 512L * 1024 * 1024, 0, Array.Empty<TailGap>(), totals, null);
    }
}
