using PolyMount.Core.Ipc;
using QtSharp.Api;

namespace PolyMount.Tray;

/// <summary>Per-file provider cards with actions bound to actual layer identities.</summary>
internal sealed class LayerVisualizer
{
    private static readonly List<LayerVisualizer> Windows = new();
    private readonly string _path;
    private readonly QWidget _window;
    private readonly QVBoxLayout _cardLayout;
    private readonly QLabel _header, _status;
    private readonly QTimer _timer;
    private Task<ControlResponse>? _pending;
    private ProbeResult? _probe;
    private readonly List<QWidget> _rows = new();

    private LayerVisualizer(string path)
    {
        _path = path;
        _window = new QWidget();
        _window.SetObjectName("dashboard");
        _window.SetStyleSheet(TrayTheme.StyleSheet);
        _window.SetWindowIcon(TrayIcons.App);
        I18n.Bind(_window.SetWindowTitle, "probe.title");
        _window.Resize(700, 700);
        var root = new QVBoxLayout(_window);
        root.SetContentsMargins(24, 24, 24, 20);
        var title = new QLabel();
        title.SetObjectName("pageTitle");
        I18n.Bind(title.SetText, "probe.title");
        root.AddWidget(title);
        _header = new QLabel(path);
        _header.SetWordWrap(true);
        _header.SetTextFormat(Qt.TextFormat.PlainText);
        root.AddWidget(_header);
        var legend = new QLabel();
        legend.SetObjectName("muted");
        legend.SetWordWrap(true);
        I18n.Bind(legend.SetText, "probe.legend");
        root.AddWidget(legend);
        var scroll = new QScrollArea();
        scroll.SetWidgetResizable(true);
        scroll.SetFrameShape(QFrame.Shape.NoFrame);
        var cards = new QWidget();
        _cardLayout = new QVBoxLayout(cards);
        _cardLayout.SetSpacing(12);
        scroll.SetWidget(cards);
        root.AddWidget(scroll, 1);
        _status = new QLabel();
        _status.SetTextFormat(Qt.TextFormat.PlainText);
        _status.SetWordWrap(true);
        root.AddWidget(_status);
        var refresh = new QPushButton();
        I18n.Bind(refresh.SetText, "refresh");
        refresh.SetIcon(TrayIcons.Get("refresh"));
        refresh.Clicked += _ => Refresh();
        root.AddWidget(refresh);
        _timer = new QTimer(_window);
        _timer.Timeout += Complete;
        _timer.Start(100);
        I18n.Changed += Render;
        Refresh();
    }

    public static bool Open(string path)
    {
        var view = new LayerVisualizer(path);
        Windows.Add(view);
        view._window.Show();
        view._window.Raise();
        view._window.ActivateWindow();
        return true;
    }

    private void Refresh()
    {
        if (_pending != null) return;
        _status.SetText(I18n.T("working"));
        _pending = Task.Run(() => ControlClient.Probe(_path));
    }

    private void Complete()
    {
        if (_pending is not { IsCompleted: true }) return;
        var task = _pending;
        _pending = null;
        try
        {
            var response = task.GetAwaiter().GetResult();
            if (!response.Ok || response.Probe == null) throw new InvalidOperationException(response.Error ?? "No probe data");
            _probe = response.Probe;
            _status.SetText(I18n.T("refreshed"));
            Render();
        }
        catch (Exception ex) { _status.SetText(I18n.T("probe.failed") + ex.Message); }
    }

    private void Render()
    {
        foreach (var row in _rows) { _cardLayout.RemoveWidget(row); row.Hide(); row.DeleteLater(); }
        _rows.Clear();
        if (_probe == null) return;
        _header.SetText(_path + "\n" + (_probe.Found ? I18n.T("probe.effective") + _probe.EffectiveLayer : I18n.T("probe.invisible")));
        foreach (var layer in _probe.Layers)
        {
            string color = layer.Effective ? "#059669" : layer.Exists ? "#d97706" : "#94a3b8";
            var card = new QWidget();
            card.SetAttribute(Qt.WidgetAttribute.WA_StyledBackground);
            card.SetStyleSheet($"background: #ffffff; border: 1px solid #e2e8f0; border-left: 4px solid {color}; border-radius: 8px;");
            var layout = new QVBoxLayout(card);
            layout.SetContentsMargins(16, 14, 16, 14);
            var name = new QLabel(layer.Name + " · " + I18n.T(layer.Writable ? "writable" : "readonly"));
            name.SetStyleSheet("border: none; font-size: 16px; font-weight: 600;");
            name.SetTextFormat(Qt.TextFormat.PlainText);
            layout.AddWidget(name);
            string state = I18n.T(layer.Effective ? "probe.effective" : layer.Exists ? "probe.shadowed" : "probe.unknown");
            var description = new QLabel(state + " · " + (layer.Exists ? layer.IsDir ? I18n.T("probe.directory") : layer.Size + " B" : I18n.T("probe.fallthrough")));
            description.SetStyleSheet($"border: none; color: {color};");
            layout.AddWidget(description);
            var actions = new QHBoxLayout();
            if (layer.Type == "overlay" || layer.Name == "overlay")
            {
                AddAction(actions, layer, "probe.drop", LayerActions.DropLocal, layer.Exists);
            }
            if (layer.Type == "cache" || layer.Name == "cache") AddAction(actions, layer, "probe.evict", LayerActions.EvictCache, layer.Exists);
            layout.AddLayout(actions);
            _cardLayout.AddWidget(card);
            _rows.Add(card);
        }
    }

    private void AddAction(QHBoxLayout layout, LayerProbe layer, string key, string action, bool enabled)
    {
        var button = new QPushButton(I18n.T(key));
        button.SetEnabled(enabled);
        button.Clicked += _ =>
        {
            if (_pending != null) return;
            if (action == LayerActions.DropLocal && QMessageBox.Question(_window, I18n.T("confirm"),
                I18n.T(key) + "\n" + _path, I18n.T("confirm"), I18n.T("cancel"), null, 1, 1) != 0) return;
            _status.SetText(I18n.T("working"));
            _pending = Task.Run(() => ControlClient.LayerAction(_path, layer.Name, action));
        };
        layout.AddWidget(button);
    }
}
