using PolyMount.Core.Ipc;
using QtSharp.Api;
using System.Diagnostics;

namespace PolyMount.Tray;

/// <summary>
/// The recording, as a part of the performance panel rather than a page of its own: one card that
/// says what is being collected and how much of it there is, the actions that start it, save it
/// and end it, and the interruptions it knows about.
///
/// It owns its session and its own once-a-second poll. The performance panel keeps sampling
/// alongside it — one extra control round trip a second while a recording runs, which is the price
/// of the recording not being able to skew or stall the view the reader is watching.
/// </summary>
internal sealed class RecordingPanel : QWidget
{
    /// <summary>How often the session asks the service how it is doing. In between, the copy is
    /// driven by the files themselves, which is fresher and costs nothing.</summary>
    private const int TableRowHeight = 32;
    private static string T(string key) => I18n.T(key);

    private readonly string _name;
    private readonly QPushButton _start = new(), _save = new(), _discard = new();
    private readonly RecordingLamp _lamp = new();
    private readonly QLabel _state = new(), _elapsed = new(), _collected = new();
    private readonly QLabel _evidence = new(), _totals = new(), _capacity = new(), _message = new(), _paths = new();
    private readonly QTableWidget _gaps;
    private readonly QTimer _timer;
    private RecordingSession? _session;
    private Task<RecordingPulse>? _pending;
    private Task<ExportOutcome>? _pendingSave;
    /// <summary>A recording the service is running that this panel is not part of: one started by
    /// another front-end, or one a tray left behind when it died. The channel does not care whether
    /// anyone is watching, so the panel has to ask rather than assume.</summary>
    private Task<ControlResponse>? _pendingProbe;
    private RecordingSessionInfo? _elsewhere;
    private DateTime _nextProbe, _next;
    private RecordingPulse? _latest;
    private string _lastSaved = "", _startError = "", _transient = "";
    private string _saveDirectory = StatsExport.LoadDirectory();
    /// <summary>Set by a smoke run: the panel then never opens a dialog.</summary>
    internal bool Quiet;

    public RecordingPanel(string name)
    {
        _name = name;
        // A tray that was killed leaves its working copy behind, and one can be hundreds of
        // megabytes: clearing out old ones belongs here, off the thread that is drawing.
        Task.Run(() => RecordingSession.PruneWorkingCopies(TimeSpan.FromDays(7)));
        var card = new QWidget();
        card.SetObjectName("surface");
        card.SetAttribute(Qt.WidgetAttribute.WA_StyledBackground);
        var outer = new QVBoxLayout(this);
        outer.SetContentsMargins(0, 0, 0, 0);
        outer.AddWidget(card);
        var root = new QVBoxLayout(card);
        root.SetContentsMargins(18, 14, 18, 14);
        root.SetSpacing(8);

        var head = new QHBoxLayout();
        head.SetSpacing(14);
        head.AddWidget(_lamp);
        var texts = new QVBoxLayout();
        texts.SetSpacing(2);
        _state.SetObjectName("metricValue");
        texts.AddWidget(_state);
        _collected.SetObjectName("muted");
        texts.AddWidget(_collected);
        head.AddLayout(texts, 1);
        _elapsed.SetText("00:00");
        var font = new QFont("Consolas", 20);
        font.SetBold(true);
        _elapsed.SetFont(font);
        _elapsed.SetAlignment(Qt.Alignment.AlignRight | Qt.Alignment.AlignVCenter);
        head.AddWidget(_elapsed);
        _start.Clicked += _ => Begin();
        head.AddWidget(_start);
        _discard.SetIcon(TrayIcons.Get("stop"));
        _discard.Clicked += _ => Discard();
        head.AddWidget(_discard);
        _save.SetIcon(TrayIcons.Get("save", "#ffffff"));
        _save.Clicked += _ => Save();
        head.AddWidget(_save);
        root.AddLayout(head);

        _evidence.SetObjectName("muted");
        _evidence.SetWordWrap(true);
        _evidence.SetTextFormat(Qt.TextFormat.PlainText);
        root.AddWidget(_evidence);
        _totals.SetObjectName("muted");
        _totals.SetWordWrap(true);
        _totals.SetTextFormat(Qt.TextFormat.PlainText);
        root.AddWidget(_totals);
        _capacity.SetObjectName("muted");
        root.AddWidget(_capacity);
        _gaps = ContinuousScrolling.Table();
        _gaps.SetColumnCount(3);
        _gaps.SetEditTriggers(QAbstractItemView.EditTriggers.NoEditTriggers);
        _gaps.SetShowGrid(false);
        _gaps.VerticalHeader().Hide();
        _gaps.HorizontalHeader().SetSectionResizeMode(QHeaderView.ResizeMode.Stretch);
        _gaps.HorizontalHeader().SetDefaultAlignment(Qt.Alignment.AlignLeft | Qt.Alignment.AlignVCenter);
        _gaps.SetVerticalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff);
        _gaps.Hide();
        root.AddWidget(_gaps);
        _message.SetObjectName("muted");
        _message.SetWordWrap(true);
        _message.SetTextFormat(Qt.TextFormat.PlainText);
        root.AddWidget(_message);
        _paths.SetObjectName("muted");
        _paths.SetWordWrap(true);
        _paths.SetTextFormat(Qt.TextFormat.PlainText);
        _paths.SetTextInteractionFlags(Qt.TextInteractionFlags.TextSelectableByMouse);
        root.AddWidget(_paths);

        _timer = new QTimer(this);
        _timer.Timeout += Tick;
        _timer.Start(200);
        Translate();
    }

    private static QLabel Label(string key, string name)
    {
        var label = new QLabel();
        label.SetObjectName(name);
        I18n.Bind(label.SetText, key);
        return label;
    }

    /// <summary>Whether this panel is the one collecting, which is what the performance panel asks
    /// before it decides how to read a second of its own.</summary>
    internal bool Recording => _session != null;

    /// <summary>Grabs the panel on its own, for the smoke run's screenshots.</summary>
    internal void Capture(string path) { using var image = Grab(); image.Save(path); }

    internal void Translate()
    {
        _start.SetText(T(_session != null ? "record.restart" : _elsewhere != null ? "record.adopt" : "record.start"));
        _save.SetText(T("record.save"));
        _discard.SetText(T("record.discard"));
        _gaps.SetHorizontalHeaderLabels(new[] { T("record.gap.range"), T("record.gap.reason"), T("record.gap.missing") });
        Render();
    }

    /// <summary>Starts a recording, or takes over one the service is already running, or says why
    /// it could not. Nothing here starts by being looked at: a recording writes hundreds of
    /// megabytes a minute, and opening a panel is not a decision to spend that.</summary>
    private void Begin()
    {
        _session?.Suspend();
        RecordingSessions.Forget(_session);
        _session = null;
        _startError = "";
        _lastSaved = "";
        _transient = "";
        _latest = null;
        try
        {
            _session = RecordingSession.Start(_name);
            RecordingSessions.Track(_session);
            _elsewhere = null;
            // The panel's own view is a 60 second window; a recording is the whole of it, so the
            // reader should never have to wonder which window the charts are showing.
            _next = DateTime.MinValue;
        }
        catch (Exception ex) { _startError = ex.Message; }
        Render();
    }

    private void Tick()
    {
        if (_pending is { IsCompleted: true })
        {
            var task = _pending;
            _pending = null;
            try { _latest = task.GetAwaiter().GetResult(); }
            catch (Exception ex) { _startError = ex.Message; }
            if (IsVisible()) Render();
        }
        if (_pendingSave is { IsCompleted: true })
        {
            var task = _pendingSave;
            _pendingSave = null;
            ReportSave(task);
        }
        if (_pendingProbe is { IsCompleted: true })
        {
            var task = _pendingProbe;
            _pendingProbe = null;
            try { _elsewhere = task.GetAwaiter().GetResult().Recording is { Active: true } running ? running : null; }
            catch (Exception) { _elsewhere = null; }
            if (IsVisible()) Render();
        }
        // Idle is not the same as nothing running: the service keeps a channel attached whether or
        // not anything is watching it, so an idle panel asks, and offers to take it over.
        if (_session == null && _pendingProbe == null && _pendingSave == null && DateTime.UtcNow >= _nextProbe)
        {
            _nextProbe = DateTime.UtcNow.AddSeconds(5);
            _pendingProbe = Task.Run(() => ControlClient.RecordingStatus(_name));
        }
        // Not gated on IsVisible(): a recording collects while its tab is not the one being looked
        // at, which is most of the time it runs.
        if (_session != null && _pending == null && _pendingSave == null && DateTime.UtcNow >= _next && Collecting)
        {
            _next = DateTime.UtcNow.AddSeconds(1);
            var session = _session;
            _pending = Task.Run(() => session.Poll(DateTime.UtcNow));
        }
    }

    /// <summary>Only a recording that is still running, or one that has not started, is polled: a
    /// session the service ended keeps what it has and waits to be saved.</summary>
    private bool Collecting => _session != null &&
        (_latest == null || _latest.State is RecordingState.Recording or RecordingState.Unreachable);

    /// <summary>Writes the whole recording out and ends it. The folder is chosen before the session
    /// is frozen: the bundle describes the moment it was frozen, and a cancelled dialog has to leave
    /// a recording running rather than one that stopped because a folder was asked for.</summary>
    private void Save()
    {
        var session = _session;
        if (session == null || _pendingSave != null) return;
        string? parent = QFileDialog.GetExistingDirectory(this, T("record.save.choose"), _saveDirectory);
        if (string.IsNullOrEmpty(parent)) return;
        _saveDirectory = parent;
        StatsExport.SaveDirectory(parent);
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
            _session?.ReleaseWorkingCopy();
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

    /// <summary>Closing the panel's tab while a recording runs is a plausible way of saying "stop"
    /// and a plausible accident. It asks, because the evidence is the whole point of it.</summary>
    internal void RequestClose(Action proceed)
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
            : _session == null ? _elsewhere != null ? "record.state.elsewhere" : "record.state.idle"
            : pulse == null ? "record.state.starting"
            : pulse.State switch
            {
                RecordingState.Recording => "record.state.recording",
                RecordingState.Unreachable => "record.state.unreachable",
                RecordingState.Truncated => "record.state.truncated",
                _ => "record.state.stopped"
            };
        _state.SetText(T(state) + " · " + _name);
        _lamp.SetMode(pulse?.State == RecordingState.Recording || _elsewhere != null
            ? RecordingLamp.LampMode.Live : RecordingLamp.LampMode.Idle);
        _elapsed.SetText(Elapsed());
        bool ended = _session == null || pulse?.State is RecordingState.Stopped or RecordingState.Truncated;
        _save.SetEnabled(!ended && _pendingSave == null);
        _discard.SetEnabled(!ended);
        _start.SetEnabled(ended && _pendingSave == null);
        _start.SetText(T(_session != null ? "record.restart" : _elsewhere != null ? "record.adopt" : "record.start"));
        // The one thing to do on a panel with nothing running should look like the thing to do.
        Restyle(_start, _session == null, "record");
        Restyle(_save, _session != null && !ended, "save");

        // The card carries what is happening right now; the lines under it carry the evidence.
        _collected.SetText(pulse != null ? I18n.F("record.collected",
                ByteUnits.Format(pulse.RequestBytes), pulse.RequestLines, pulse.SecondRows)
            : _session != null ? T("record.starting")
            : _elsewhere != null ? I18n.F("record.elsewhere.figures", ByteUnits.Format(_elsewhere.Bytes), ElapsedSince(_elsewhere))
            : T("record.idle"));
        // An empty line still takes a line: an idle panel should be as tall as what it has to say.
        Show(_evidence, EvidenceLine(pulse));
        Show(_totals, TotalsLine(pulse));
        Show(_capacity, CapacityLine(pulse));
        _elapsed.SetVisible(_session != null || _elsewhere != null);
        RenderGaps(pulse);
        string message = _transient.Length > 0 ? _transient
            : _startError.Length > 0 ? T("record.start.failed") + _startError
            : pulse?.Error is { Length: > 0 } error ? T("record.state.unreachable") + " · " + error
            : pulse?.Note is { Length: > 0 } recorded ? T("record.note") + recorded
            : pulse != null && pulse.DroppedEvents > 0 ? I18n.F("record.dropped.warning", Count(pulse.DroppedEvents))
            : _session != null ? T("record.hint")
            : _elsewhere != null ? I18n.F("record.elsewhere", ElapsedSince(_elsewhere), ByteUnits.Format(_elsewhere.Bytes))
            : "";
        Show(_message, message);
        Show(_paths, _session == null || ended
            ? _lastSaved.Length == 0 ? "" : I18n.F("record.savedTo", _lastSaved)
            : I18n.F("record.working", _session.WorkingDirectory));
    }

    /// <summary>What is being collected and how much of it there is. This is the panel's claim that
    /// the recording is complete, so it names each part and whether anything went wrong with it.</summary>
    private string EvidenceLine(RecordingPulse? pulse)
    {
        if (pulse == null && _elsewhere == null) return "";
        string status = T(pulse?.State switch
        {
            RecordingState.Recording => "record.evidence.live",
            RecordingState.Unreachable => "record.evidence.waiting",
            RecordingState.Truncated => "record.evidence.truncated",
            RecordingState.Stopped => "record.evidence.closed",
            _ => "record.evidence.idle"
        });
        // A recording this panel is not part of has no figures here at all, and saying so is the
        // whole of what this line can honestly report about it.
        if (pulse == null) return T("record.evidence.elsewhere");
        // A recording that lost events is not the complete record it looks like, and the count of
        // what it lost belongs next to the claim rather than in a field of the bundle: the reader
        // should not have to go looking for the reason its numbers are low.
        string lost = pulse.DroppedEvents == 0 ? ""
            : " · " + I18n.F("record.evidence.dropped", Count(pulse.DroppedEvents));
        _evidence.SetStyleSheet(pulse.DroppedEvents > 0 ? "color:#dc2626;" : "color:#718096;");
        return I18n.F("record.evidence.line", status, ByteUnits.Format(pulse.RequestBytes), Count(pulse.RequestLines),
            ByteUnits.Format(pulse.ServiceBytes), Count(pulse.SampleRows), Count(pulse.SecondRows)) + lost;
    }

    /// <summary>The recording so far, layer by layer — the whole-session view the 60 second table
    /// below cannot give, in one line rather than a second table beside the first.</summary>
    private string TotalsLine(RecordingPulse? pulse)
    {
        var totals = pulse?.Totals ?? Array.Empty<LayerTotalRecord>();
        if (totals.Count == 0) return "";
        long reads = totals.Sum(total => total.Reads);
        return T("record.totals") + " " + string.Join(" · ", totals
            .Where(total => total.Reads > 0 || total.Bytes > 0)
            .Select(total => string.Format(I18n.Culture, "{0} {1} ({2:0.#}%)",
                string.IsNullOrEmpty(total.DisplayName) ? total.Layer : total.DisplayName,
                Count(total.Reads), reads == 0 ? 0 : 100.0 * total.Reads / reads)));
    }

    /// <summary>Sets a line and hides it when it has nothing to say, so the card is only as tall
    /// as what it is currently reporting.</summary>
    private static void Show(QLabel label, string text)
    {
        if (label.Text != text) label.SetText(text);
        label.SetVisible(text.Length > 0);
    }

    private string CapacityLine(RecordingPulse? pulse)
    {
        long capacity = pulse?.CapacityBytes ?? 0;
        long used = pulse?.RequestBytes ?? 0;
        // The rate is what turns a ceiling into a decision: with the sampling filter off a heavy
        // traversal writes megabytes a second, and the reader should see how long the recording
        // they started can still run rather than discover a limit they did not expect.
        double rate = Rate(pulse);
        // Near its ceiling the figure turns red where it stands: by the time the channel stops
        // itself the reader should already have seen it coming.
        _capacity.SetStyleSheet(capacity > 0 && used * 5 > capacity * 4 ? "color:#dc2626;" : "color:#718096;");
        return capacity == 0 ? ""
            : rate <= 0 ? I18n.F("record.capacity", ByteUnits.Format(used), ByteUnits.Format(capacity))
            : I18n.F("record.capacity.rate", ByteUnits.Format(used), ByteUnits.Format(capacity),
                ByteUnits.Format(rate), Duration(TimeSpan.FromSeconds((capacity - used) / rate)));
    }

    /// <summary>Every stretch the copy knows is missing. Empty is the normal case, and then the
    /// table is not there at all: a heading that is always empty teaches the reader to skip it.</summary>
    private void RenderGaps(RecordingPulse? pulse)
    {
        var gaps = pulse?.Gaps ?? Array.Empty<TailGap>();
        _gaps.SetVisible(gaps.Count > 0);
        if (gaps.Count == 0) return;
        _gaps.SetFixedHeight(Math.Min(gaps.Count, 4) * TableRowHeight + 34);
        Fill(gaps.Count, row => new[]
        {
            Local(gaps[row].FromUtc) + " → " + Local(gaps[row].ToUtc),
            gaps[row].Reason,
            gaps[row].MissingBytes is { } bytes ? ByteUnits.Format(bytes) : "—"
        });
    }

    private void Fill(int count, Func<int, string[]> row)
    {
        bool changed = _gaps.RowCount != count;
        if (changed) { _gaps.SetUpdatesEnabled(false); _gaps.SetRowCount(count); }
        for (int index = 0; index < count; index++)
        {
            var values = row(index);
            for (int column = 0; column < values.Length; column++)
            {
                var item = _gaps.Item(index, column);
                if (item == null)
                {
                    item = new QTableWidgetItem(values[column]);
                    item.SetTextAlignment(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter);
                    _gaps.SetItem(index, column, item);
                }
                else if (item.Text() != values[column]) item.SetText(values[column]);
            }
            _gaps.SetRowHeight(index, TableRowHeight);
        }
        if (changed) _gaps.SetUpdatesEnabled(true);
    }

    private double Rate(RecordingPulse? pulse)
    {
        if (pulse == null || _session == null) return 0;
        double seconds = (pulse.Time - _session.StartedUtc).TotalSeconds;
        return seconds >= 3 ? (pulse.RequestBytes + pulse.ServiceBytes) / seconds : 0;
    }

    /// <summary>The primary look is a stylesheet rule on the window, so a widget that changes role
    /// needs the window re-polished. Only a state change does this, never a tick.</summary>
    private void Restyle(QPushButton button, bool primary, string icon)
    {
        if ((button.ObjectName == "primary") == primary) return;
        button.SetObjectName(primary ? "primary" : "");
        button.SetIcon(TrayIcons.Get(icon, primary ? "#ffffff" : "#526786"));
        SetStyleSheet(TrayTheme.StyleSheet);
    }

    private string Count(long value) => value.ToString("N0", I18n.Culture);

    private static string Local(string utc) => DateTimeOffset.TryParse(utc, out var at)
        ? at.ToLocalTime().ToString("HH:mm:ss") : utc;

    private static string Duration(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours} h {span.Minutes} min"
        : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min" : $"{Math.Max(0, (int)span.TotalSeconds)} s";

    /// <summary>How long a recording that this panel is not part of has been running.</summary>
    private static string ElapsedSince(RecordingSessionInfo info)
    {
        if (!DateTimeOffset.TryParse(info.StartedUtc, out var started)) return "";
        var span = DateTimeOffset.UtcNow - started;
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} h {span.Minutes} min";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes} min";
        return $"{Math.Max(0, (int)span.TotalSeconds)} s";
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

    /// <summary>Runs the one action the panel exists for: start, then end without keeping it. Nothing
    /// else in a smoke run presses the button, and a break in the only way to begin a recording
    /// would reach the reader first.</summary>
    internal void SmokeStartAndDiscard(string? capture = null)
    {
        // A check must not open a dialog: nobody is there to answer one, and the run would wait.
        bool wasQuiet = Quiet;
        Quiet = true;
        Begin();
        try
        {
            if (_session == null) throw new InvalidOperationException("Recording did not start: " + _startError);
            Render();
            // Started but not yet polled reads as "connecting"; both are states that mean it is on.
            string starting = T("record.state.starting"), running = T("record.state.recording");
            if (!_state.Text.StartsWith(starting, StringComparison.Ordinal) &&
                !_state.Text.StartsWith(running, StringComparison.Ordinal))
                throw new InvalidOperationException("The recording does not show that it started: " + _state.Text);
            if (!_save.IsEnabled() || !_discard.IsEnabled() || _start.IsEnabled())
                throw new InvalidOperationException("The recording's actions do not follow its state");
            // The only moment this state exists is inside this check, so it is the only place a
            // screenshot of it can be taken.
            if (capture != null) Capture(capture);
        }
        finally
        {
            // A check that fails must not leave a recording attached: the service would keep
            // writing it with no page left to save it.
            if (_session != null) Discard();
            Quiet = wasQuiet;
        }
        if (_session != null || !_start.IsEnabled())
            throw new InvalidOperationException("The recording did not go back to idle");
    }

    /// <summary>Self-check for a smoke run. A recording with no service cannot produce a second of
    /// evidence, so this renders one and asserts what a reader would then see.</summary>
    internal void Verify()
    {
        if (_gaps.ColumnCount != 3) throw new InvalidOperationException("The interruption table lost a column");
        if (I18n.T("record.state.recording") == "record.state.recording")
            throw new InvalidOperationException("Recording strings are not in the translation catalog");
        // Asked for by name, so a glyph that silently fell back to the default one is caught here.
        foreach (string icon in new[] { "record", "save" })
            if (TrayIcons.Get(icon).Handle == TrayIcons.Get("no-such-glyph").Handle)
                throw new InvalidOperationException($"Recording icon '{icon}' is not drawn");
        // The card says what is happening; with nothing attached and nothing recorded that is the
        // sentence telling the reader what to do about it. A recording the service is running is
        // neither, and a smoke run cannot rule one out — it may even have left one behind itself.
        if (_elsewhere == null && _collected.Text != T("record.idle"))
            throw new InvalidOperationException("An idle panel does not say what to do: " + _collected.Text);
        bool wasQuiet = Quiet;
        Quiet = true;
        _latest = Synthetic();
        Render();
        Quiet = wasQuiet;
        string figures = I18n.F("record.collected", ByteUnits.Format(4096), 40, 7);
        if (_collected.Text != figures)
            throw new InvalidOperationException($"The panel reports '{_collected.Text}' instead of '{figures}'");
        // The evidence line has to account for every source the recording copies.
        string evidence = _evidence.Text;
        foreach (var part in new[] { ByteUnits.Format(4096), Count(40), Count(20) })
            if (!evidence.Contains(part, StringComparison.Ordinal))
                throw new InvalidOperationException($"The evidence line does not account for {part}: {evidence}");
        // The whole-recording view is the thing the 60 second table cannot give, in one line: the
        // layers that served reads, with their share of the whole session.
        if (!_totals.Text.Contains("SVN", StringComparison.Ordinal) ||
            !_totals.Text.Contains("100%", StringComparison.Ordinal) ||
            _totals.Text.Contains("Memory", StringComparison.Ordinal))
            throw new InvalidOperationException("The whole-recording line does not describe the layers it was given: " + _totals.Text);
        if (_capacity.Text.Length == 0 || _capacity.StyleSheet.Length == 0)
            throw new InvalidOperationException("The panel does not show how full the recording is");
    }

    /// <summary>One second as a recording of two layers would report it, for the checks above.</summary>
    private RecordingPulse Synthetic()
    {
        var now = DateTime.UtcNow;
        var totals = new List<LayerTotalRecord>
        {
            new(_name, "svn", "SVN", "#2563eb", false, 40, 4096, 0, 12.5, 40, 0, 0),
            new(_name, "ram", "Memory", "#0891b2", true, 0, 0, 0, 0, 0, 75, 6144)
        };
        return new RecordingPulse(RecordingState.Recording, null, now, 3.5, null, new List<MountMetrics>(),
            new ServiceProcessReading(1, now.ToString("O"), 10, 12, 4096, 8192, 8, 100),
            4096, 40, 1024, 20, 20, 7, 0, 512L * 1024 * 1024, 0, Array.Empty<TailGap>(), totals, null);
    }
}
