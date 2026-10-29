using PolyMount.Core.Ipc;
using QtSharp.QtCore;
using QtSharp.QtGui;
using QtSharp.QtWidgets;

namespace PolyMount.Tray;

/// <summary>The inside of a page one plugin owns: the plugin says what to show, this draws it and
/// sends the button presses back.
///
/// Nothing here knows what any panel is about. The body asks an instance's process to fill the
/// panel in (<c>pluginPanel</c>), lays out the answer — a sentence, a few named facts, a list and
/// some buttons — and sends each button's control request to the same instance. A button that needs
/// a folder or a file makes this window show the picker and puts the choice where the plugin asked
/// for it, which is why a plugin can ask for a directory without ever learning about a file dialog.
///
/// It exists because management was spread across the control plane: a program could be put under
/// the shim and could be taken out of it, but only through an MCP tool or a registry key. A plugin
/// that changes the machine is a plugin a person has to be able to look at.
///
/// Two hosts use it and neither is special: the plugin manager unfolds it under the plugin's own
/// row, and a plugin that asked for a page of its own gets it there (<see cref="PluginPanelPage"/>).
/// Which instance answers is not passed in — a panel is filled in by a process that owns a mount,
/// so the first one that is mounted is the one asked, and a machine with none says so.
///
/// What the answer becomes is **built again from nothing on every render**, rather than shown and
/// hidden in a fixed set of containers. A widget that was hidden when its parent was measured keeps
/// contributing nothing to the layout that holds it, and the page a reader opened stayed an empty
/// band with a sentence in it. Building the pieces already visible is also what makes the band grow
/// to the height of the answer without anyone asking it to.
///
/// Answers arrive on a background task and are applied by the timer, never from the task itself: a
/// body that went away while a request was in flight is a deleted widget, and the timer dies with
/// it.
/// </summary>
internal sealed class PluginPanelBody : QWidget
{
    private readonly string _panelId;
    private readonly bool _flat;
    private readonly QVBoxLayout _content;
    private readonly BusyLine _busy;
    private readonly QTimer _timer;
    /// <summary>Everything the last render put into the layout, so the next one can take it away.</summary>
    private readonly List<QWidget> _pieces = new();

    private Task<Load>? _pending;
    private Load? _loaded;
    private bool _reloadAfter;
    private string? _failure;
    private string? _shownError;
    private bool _asked;

    /// <summary>What one question produced: which instance answered, and what it said.</summary>
    private sealed record Load(string? Instance, ControlResponse? Response);

    private static string T(string key) => I18n.T(key);

    public PluginPanelBody(string panelId, bool flat = false)
    {
        _panelId = panelId;
        _flat = flat;
        _content = new QVBoxLayout(this);
        _content.SetContentsMargins(0, 0, 0, 0);
        _content.SetSpacing(8);

        _busy = new BusyLine();
        _busy.SetVisible(false);
        _content.AddWidget(_busy);

        _timer = new QTimer(this);
        _timer.Timeout += Tick;
        _timer.Start(100);
        I18n.OnChanged(Retranslate, this);
    }

    internal string PanelId => _panelId;

    /// <summary>The last answer, for the smoke run: it is the whole of what the body can say.</summary>
    internal PluginPanelView? View => _loaded?.Response?.Panel;

    /// <summary>When no page came back, what the body is holding instead: the sentence on the error
    /// line, so a check that fails says why rather than only that nothing arrived.</summary>
    internal string? LastError => _shownError;

    /// <summary>Whether a request is in flight: a picture taken mid-answer shows the progress bar
    /// and half a page.</summary>
    internal bool Busy => _pending != null;

    /// <summary>Asks for the panel again. Called when the row is unfolded, when the page is entered
    /// and after every action, because what a panel shows is state rather than a document.</summary>
    internal void Reload()
    {
        if (_pending != null) return;
        _asked = true;
        _pending = Task.Run(() =>
        {
            string? instance = FirstMounted();
            if (instance == null) return new Load(null, null);
            try
            {
                return new Load(instance, InstanceDirectory.ClientFor(instance).Send(new ControlRequest
                {
                    Cmd = ControlCommands.PluginPanel,
                    Action = _panelId
                }));
            }
            catch (Exception ex) { return new Load(instance, new ControlResponse { Ok = false, Error = ex.Message }); }
        });
        _busy.SetVisible(true);
    }

    /// <summary>The instance whose worker answers for a panel: the first one that is mounted. Asked
    /// on the background task that loads the panel, because the answer costs a pipe round trip and
    /// this runs while a window is being looked at.</summary>
    private static string? FirstMounted()
    {
        try { return InstanceDirectory.All().FirstOrDefault(entry => entry.MountPoint.Length > 0)?.Name; }
        catch (Exception) { return null; }
    }

    private void Tick()
    {
        if (_pending is not { IsCompleted: true } pending) return;
        _pending = null;
        Load load;
        try { load = pending.GetAwaiter().GetResult(); }
        catch (Exception ex) { load = new Load(null, new ControlResponse { Ok = false, Error = ex.Message }); }
        _busy.SetVisible(false);
        // The request that just landed was an action rather than a load: its answer is a report,
        // not a page. What the reader sees next is the page asked for again, which is also how a
        // refusal shows up (the plugin's own state is the same either way).
        if (_reloadAfter)
        {
            _reloadAfter = false;
            _failure = load.Response is { Ok: false } refused ? refused.Error : null;
            Reload();
            return;
        }
        _loaded = load;
        Fill();
    }

    private void Fill()
    {
        foreach (var piece in _pieces)
        {
            _content.RemoveWidget(piece);
            piece.Hide();
            piece.DeleteLater();
        }
        _pieces.Clear();
        _shownError = null;

        if (_loaded is null)
        {
            Add(Message(T("panel.loading"), "muted"));
            Settle();
            return;
        }
        if (_loaded.Instance is null)
        {
            Add(Message(T("panel.needsInstance"), "emptyState"));
            Settle();
            return;
        }

        var view = _loaded.Response?.Panel;
        string failure = _loaded.Response is { Ok: false } answer ? answer.Error ?? T("panel.failed") : "";
        if (view is { Error: { } panelError }) failure = panelError.Resolve(I18n.Language);
        // A load that worked says the machine is answering again, so what an earlier action
        // complained about is no longer news.
        if (failure.Length == 0) _failure = null;
        if (_failure is { Length: > 0 } refused) failure = refused;
        _shownError = failure;

        if (view?.Summary?.Resolve(I18n.Language) is { Length: > 0 } summary) Add(Message(summary, "muted"));
        if (failure.Length > 0) Add(Message(failure, "errorBox"));
        if (view is { Facts.Count: > 0 }) Add(FactsCard(view.Facts));
        if (view is { Rows.Count: > 0 }) Add(RowsCard(view));
        else if (view != null) Add(Message(view.Empty?.Resolve(I18n.Language) ?? T("panel.empty"), "emptyState"));
        if (view is { Actions.Count: > 0 }) Add(ActionsRow(view.Actions));
        Settle();
    }

    /// <summary>Panels and rows are put together as answers arrive, and a layout that has already
    /// been measured does not notice on its own: asking the body and everything above it to measure
    /// again is what makes the band the page opened into grow to what is in it.</summary>
    private void Settle()
    {
        UpdateGeometry();
        Layout()?.Activate();
        QWidget? parent = ParentWidget();
        for (int level = 0; level < 6 && parent != null; level++)
        {
            parent.Layout()?.Activate();
            parent.UpdateGeometry();
            parent = parent.ParentWidget();
        }
    }

    private void Add(QWidget piece)
    {
        _pieces.Add(piece);
        _content.AddWidget(piece);
    }

    private QWidget FactsCard(List<PluginPanelFact> facts)
    {
        var card = Panel();
        var column = new QVBoxLayout(card);
        column.SetContentsMargins(_flat ? 0 : 14, _flat ? 0 : 10, _flat ? 0 : 14, _flat ? 0 : 10);
        column.SetSpacing(5);
        foreach (var fact in facts)
        {
            var line = new QHBoxLayout();
            line.SetSpacing(12);
            var label = Message(fact.Label.Resolve(I18n.Language), "detailLabel");
            label.SetWordWrap(false);
            label.SetMinimumWidth(130);
            label.SetMaximumWidth(200);
            line.AddWidget(label);
            var value = Message(fact.Value.Resolve(I18n.Language), "detailValue");
            value.SetTextInteractionFlags(Qt.TextInteractionFlags.TextSelectableByMouse);
            line.AddWidget(value, 1);
            column.AddLayout(line);
        }
        return card;
    }

    private QWidget RowsCard(PluginPanelView view)
    {
        var card = Panel();
        var column = new QVBoxLayout(card);
        column.SetContentsMargins(0, 2, 0, 2);
        column.SetSpacing(0);
        for (int index = 0; index < view.Rows.Count; index++)
        {
            if (index > 0) column.AddWidget(Separator());
            column.AddWidget(RowWidget(view.Rows[index], view.Columns));
        }
        return card;
    }

    private QWidget ActionsRow(List<PluginPanelAction> actions)
    {
        var row = new QWidget();
        var line = new QHBoxLayout(row);
        line.SetContentsMargins(0, 0, 0, 0);
        line.SetSpacing(8);
        line.AddStretch(1);
        foreach (var action in actions) line.AddWidget(ActionButton(action, primary: true));
        return row;
    }

    /// <summary>One line of the list: the first cell is what the row is, the columns after it are
    /// the detail, and the row's own buttons sit at the end.</summary>
    private QWidget RowWidget(PluginPanelRow row, List<LocalizedText> columns)
    {
        var widget = new QWidget();
        widget.SetObjectName("pluginRow");
        widget.SetAttribute(Qt.WidgetAttribute.WA_StyledBackground);
        var column = new QVBoxLayout(widget);
        column.SetContentsMargins(6, 9, 6, 9);
        column.SetSpacing(3);

        var top = new QHBoxLayout();
        top.SetSpacing(10);
        var name = Message(row.Cells.Count > 0 ? row.Cells[0] : row.Id, "rowTitle");
        top.AddWidget(name, 1);
        foreach (var action in row.Actions) top.AddWidget(ActionButton(action, primary: false));
        column.AddLayout(top);

        // The detail is one line, shortened in the middle where the ends are what a reader reads:
        // a path cut at the right loses the file name, which is the whole point of showing it. What
        // did not fit is in the tooltip, together with whatever the plugin wanted said on hover.
        var details = new List<string>();
        var full = new List<string>();
        for (int index = 1; index < row.Cells.Count; index++)
        {
            string heading = index < columns.Count ? columns[index].Resolve(I18n.Language) : "";
            full.Add(heading.Length > 0 ? heading + ": " + row.Cells[index] : row.Cells[index]);
            details.Add(heading.Length > 0 ? heading + ": " + Shorten(row.Cells[index]) : Shorten(row.Cells[index]));
        }
        if (details.Count > 0)
        {
            var detail = Message(Shorten(string.Join("  ·  ", details), 150), "muted");
            detail.SetWordWrap(false);
            detail.SetTextInteractionFlags(Qt.TextInteractionFlags.TextSelectableByMouse);
            column.AddWidget(detail);
        }
        var tooltip = new List<string>(full);
        if (row.Tooltip?.Resolve(I18n.Language) is { Length: > 0 } note) tooltip.Add(note);
        if (tooltip.Count > 0) widget.SetToolTip(string.Join("\n", tooltip));
        return widget;
    }

    /// <summary>A value that is too long for a line, with its middle taken out. Long here means a
    /// path: the drive and the file name are what identifies it, and the folders between them are
    /// what the tooltip is for.</summary>
    private static string Shorten(string text, int limit = 96)
    {
        if (text.Length <= limit) return text;
        int head = (limit - 8) / 2;
        return text[..head] + " … " + text[^(limit - head - 8)..];
    }

    /// <summary>A button whose press is the plugin's request. The path picker is asked for here
    /// because a plugin cannot show one; the request is unchanged otherwise.
    ///
    /// A row button that asked for confirmation is drawn as a link in the danger colour: twelve
    /// bordered buttons down a list read as twelve invitations, and the one thing a reader must not
    /// press by accident is the one that undoes something.</summary>
    private QPushButton ActionButton(PluginPanelAction action, bool primary)
    {
        var button = new QPushButton();
        button.SetText(action.Label.Resolve(I18n.Language));
        button.SetObjectName(primary ? "primary" : action.Confirm != null ? "linkDanger" : "");
        button.SetCursor(new QCursor(Qt.CursorShape.PointingHandCursor));
        if (action.Icon is { Length: > 0 } icon)
            button.SetIcon(TrayIcons.Get(icon, primary ? TrayTheme.Tokens.OnPrimary : TrayTheme.Tokens.Icon));
        button.Clicked += _ => Run(action);
        return button;
    }

    private void Run(PluginPanelAction action)
    {
        if (_pending != null) return;
        string? instance = _loaded?.Instance;
        if (string.IsNullOrEmpty(instance)) return;

        var request = new ControlRequest
        {
            Cmd = action.Request.Cmd,
            Action = action.Request.Action,
            Path = action.Request.Path,
            Layer = action.Request.Layer,
            Rev = action.Request.Rev,
            Boundary = action.Request.Boundary,
            Name = action.Request.Name
        };
        if (action.Input != PluginPanelInput.None)
        {
            string caption = action.Label.Resolve(I18n.Language);
            string? chosen = action.Input == PluginPanelInput.Directory
                ? QFileDialog.GetExistingDirectory(this, caption, "")
                : QFileDialog.GetOpenFileName(this, caption, "", "");
            if (string.IsNullOrWhiteSpace(chosen)) return;
            request.Path = chosen;
        }
        if (action.Confirm is { } confirm &&
            QMessageBox.Question(this, T("panel.confirm"), confirm.Resolve(I18n.Language),
                T("confirm"), T("cancel"), null, 1, 1) != 0)
            return;

        _pending = Task.Run(() =>
        {
            try { return new Load(instance, InstanceDirectory.ClientFor(instance).Send(request)); }
            catch (Exception ex) { return new Load(instance, new ControlResponse { Ok = false, Error = ex.Message }); }
        });
        _busy.SetVisible(true);
        // The answer to the action is read by the next tick, which then asks for the panel again;
        // drawing it now would be a page built from a reply that is not a panel.
        _reloadAfter = true;
    }

    private void Retranslate()
    {
        if (_asked) Reload();
        Fill();
    }

    // ---- small builders -----------------------------------------------------------------

    private static QLabel Message(string text, string name)
    {
        var label = new QLabel();
        label.SetText(text);
        label.SetObjectName(name);
        label.SetWordWrap(true);
        return label;
    }

    /// <summary>A card for the facts and the list, unless this body is already inside one: the
    /// plugin manager unfolds it inside the plugin's own row, and a card in a card reads as two.</summary>
    private QWidget Panel()
    {
        var panel = new QWidget();
        if (_flat) return panel;
        panel.SetObjectName("surface");
        panel.SetAttribute(Qt.WidgetAttribute.WA_StyledBackground);
        return panel;
    }

    private static QWidget Separator()
    {
        var separator = new QFrame();
        separator.SetObjectName("pluginSeparator");
        separator.SetFrameShape(QFrame.Shape.HLine);
        return separator;
    }
}

/// <summary>A plugin page that asked to be a destination of its own: the same body, with the frame
/// every section wears and the plugin's title. The plugin manager shows the same body unfolded
/// under the plugin's row; this exists for the page a plugin wants the reader to find in the
/// sidebar.</summary>
internal sealed class PluginPanelPage : QWidget
{
    private readonly PluginPanelDescriptor _descriptor;
    private readonly QLabel _heading;
    private readonly PluginPanelBody _body;

    public PluginPanelPage(PluginPanelDescriptor descriptor)
    {
        _descriptor = descriptor;
        var root = PageChrome.Apply(this, out _heading, out var header);
        _body = new PluginPanelBody(descriptor.Id);
        var refresh = new QPushButton();
        refresh.SetObjectName("iconButton");
        refresh.SetIcon(TrayIcons.Get("refresh", TrayTheme.Tokens.Icon));
        refresh.SetToolTip(I18n.T("refresh.tip"));
        refresh.SetAccessibleName(I18n.T("refresh.tip"));
        refresh.SetCursor(new QCursor(Qt.CursorShape.PointingHandCursor));
        refresh.Clicked += _ => _body.Reload();
        header.AddWidget(refresh);

        root.AddWidget(_body);
        root.AddStretch(1);
        Relabel();
        I18n.OnChanged(Relabel, this);
    }

    internal string PanelId => _descriptor.Id;

    /// <summary>The body, for the smoke run: it is the whole of what the page can say.</summary>
    internal PluginPanelBody Body => _body;

    internal void Reload() => _body.Reload();

    private void Relabel() => _heading.SetText(_descriptor.Title.Resolve(I18n.Language));
}
