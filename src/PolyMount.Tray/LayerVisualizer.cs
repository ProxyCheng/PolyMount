using PolyMount.Core.Ipc;
using QtSharp.QtCore;
using QtSharp.QtGui;
using QtSharp.QtWidgets;
using Qt = QtSharp.QtCore.Qt;

namespace PolyMount.Tray;

/// <summary>Per-file provider cards with actions bound to actual layer identities.</summary>
internal sealed class LayerVisualizer : QWidget
{
    private static readonly List<LayerVisualizer> Windows = new();
    private readonly string _path;
    /// <summary>The process that owns the path this window was opened on: one instance, one pipe,
    /// and this window holds it. Re-pointed on every refresh, which is also where a service that
    /// was away comes back.</summary>
    private InstanceClient _client;
    private readonly QVBoxLayout _cardLayout;
    private readonly QLabel _header, _status;
    private readonly QTimer _timer;
    private readonly LayerBoundaryView _diagram;
    private readonly QPushButton _release;
    private readonly QComboBox _boundary;
    private readonly QLabel _boundarySummary;
    private bool _updatingBoundary;
    private Task<ControlResponse>? _pending;
    private ProbeResult? _probe;
    private List<PolyMount.Core.LayerDescriptor> _descriptors = new();
    private readonly List<QWidget> _rows = new();

    private LayerVisualizer(string path)
    {
        _path = path;
        _client = InstanceDirectory.ClientForPath(path);
        SetObjectName("dashboard");
        
        SetWindowIcon(TrayIcons.App);
        I18n.Bind(SetWindowTitle, "object.manageBoundary");
        Resize(700, 700);
        var root = new QVBoxLayout(WindowScrollArea.Create(this, 640, out _));
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
        _diagram = new LayerBoundaryView();
        root.AddWidget(_diagram);
        _boundary = ContinuousScrolling.Combo();
        _boundary.SetAccessibleName(I18n.T("object.boundaryPosition"));
        _boundary.CurrentIndexChanged += index =>
        {
            if (!_updatingBoundary && index >= 0) _diagram.SetBoundary(index);
        };
        root.AddWidget(_boundary);
        _boundarySummary = new QLabel();
        _boundarySummary.SetObjectName("layerChain");
        _boundarySummary.SetWordWrap(true);
        _boundarySummary.SetTextFormat(Qt.TextFormat.PlainText);
        root.AddWidget(_boundarySummary);
        _release = new QPushButton();
        _diagram.BoundaryChanged += UpdateBoundary;
        I18n.Bind(_release.SetText, "object.releaseAbove");
        _release.SetObjectName("primary");
        _release.SetEnabled(false);
        _release.Clicked += _ => Release();
        root.AddWidget(_release);
        // The cards go straight into the page. The window is already a scroll area, and the second
        // one here did nothing but take the room the cards needed: it held the stretch, so the panel
        // stayed as tall as whatever was left and grew a scrollbar of its own, inside a window whose
        // own bar was still at the top — the last card cut in half against a scrollbar nobody had
        // scrolled. What is taller than the window is the outer bar's business, and it is the one
        // the reader is already using.
        var cards = new QWidget();
        _cardLayout = new QVBoxLayout(cards);
        _cardLayout.SetSpacing(12);
        root.AddWidget(cards);
        _status = new QLabel();
        _status.SetTextFormat(Qt.TextFormat.PlainText);
        _status.SetWordWrap(true);
        root.AddWidget(_status);
        var refresh = new QPushButton();
        I18n.Bind(refresh.SetText, "refresh");
        refresh.SetIcon(TrayIcons.Get("refresh"));
        refresh.Clicked += _ => Refresh();
        root.AddWidget(refresh);
        _timer = new QTimer(this);
        _timer.Timeout += Complete;
        _timer.Start(100);
        Refresh();
    }

    public static bool Open(string path)
    {
        var view = new LayerVisualizer(path);
        Windows.Add(view);
        view.Show();
        view.Raise();
        view.ActivateWindow();
        return true;
    }

    private void Refresh()
    {
        if (_pending != null) return;
        _status.SetText(I18n.T("working"));
        _client = InstanceDirectory.ClientForPath(_path);
        var client = _client;
        _pending = Task.Run(() => client.Probe(_path));
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
            _descriptors = response.LayerDescriptors;
            _diagram.SetProbe(_probe);
            PopulateBoundary();
            _status.SetText(I18n.T("refreshed"));
            Render();
        }
        catch (Exception ex) { _status.SetText(I18n.T("probe.failed") + ex.Message); _boundary.SetEnabled(_probe != null); UpdateBoundary(); }
    }

    private void Render()
    {
        foreach (var row in _rows) { _cardLayout.RemoveWidget(row); row.Hide(); row.DeleteLater(); }
        _rows.Clear();
        if (_probe == null) return;
        _header.SetText(_path + "\n" + (_probe.Found ? I18n.T("probe.effective") + _probe.EffectiveLayer : I18n.T("probe.invisible")));
        foreach (var layer in _probe.Layers)
        {
            string color = layer.Effective ? TrayTheme.Tokens.Success : layer.Exists ? TrayTheme.Tokens.Warning : TrayTheme.Tokens.Faint;
            var card = new QWidget();
            card.SetAttribute(Qt.WidgetAttribute.WA_StyledBackground);
            card.SetStyleSheet($"background: {TrayTheme.Tokens.Surface}; border: 1px solid {TrayTheme.Tokens.Border}; border-left: 4px solid {color}; border-radius: 8px;");
            var layout = new QVBoxLayout(card);
            layout.SetContentsMargins(16, 14, 16, 14);
            var identity = new QHBoxLayout();
            var icon = new QLabel();
            var descriptor = _descriptors.FirstOrDefault(d => d.Type.Equals(layer.Type, StringComparison.OrdinalIgnoreCase));
            icon.SetPixmap(TrayIcons.Get(descriptor?.Icon ?? "layers", layer.Color).Pixmap(24, 24));
            icon.SetFixedSize(28, 28);
            identity.AddWidget(icon);
            var name = new QLabel(layer.Name + " · " + I18n.T(layer.Writable ? "writable" : "readonly"));
            name.SetStyleSheet("border: none; font-size: 16px; font-weight: 600;");
            name.SetTextFormat(Qt.TextFormat.PlainText);
            identity.AddWidget(name, 1);
            layout.AddLayout(identity);
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
    public override void ChangeEvent(QEvent e)
    {
        base.ChangeEvent(e);
        if (e.type() == QEvent.Type.LanguageChange && _boundary != null && _rows != null) { Render(); PopulateBoundary(); }
    }

    private void PopulateBoundary()
    {
        _updatingBoundary = true;
        _boundary.Clear();
        _boundary.AddItem(I18n.T("object.boundaryNone"));
        if (_probe != null)
            foreach (var layer in _probe.Layers) _boundary.AddItem(I18n.F("object.boundaryAfter", layer.Name));
        _boundary.SetCurrentIndex(_diagram.Boundary);
        _boundary.SetEnabled(_probe != null);
        _updatingBoundary = false;
        UpdateBoundary();
    }

    private void UpdateBoundary()
    {
        _updatingBoundary = true;
        _boundary.SetCurrentIndex(_diagram.Boundary);
        _updatingBoundary = false;
        var affected = _probe?.Layers.Take(_diagram.Boundary).Where(l => l.Manageable).Select(l => l.Name).ToArray() ?? Array.Empty<string>();
        _release.SetEnabled(_pending == null && affected.Length > 0);
        _boundarySummary.SetText((affected.Length == 0 ? I18n.T("object.boundaryNone") : I18n.F("object.boundaryAffected", string.Join(" → ", affected))) +
            "\n" + I18n.T("object.boundaryScope"));
    }

    private void Release()
    {
        if (_pending != null || _probe == null || _diagram.Boundary == 0) return;
        if (QMessageBox.Question(this, I18n.T("confirm"), _path + "\n\n" + _boundarySummary.Text + "\n\n" + I18n.T("object.releaseConfirm"),
            I18n.T("confirm"), I18n.T("cancel"), null, 1, 1) != 0) return;
        int boundary = _diagram.Boundary;
        var layers = _probe.Layers.Select(l => l.Name).ToList();
        _release.SetEnabled(false);
        _boundary.SetEnabled(false);
        _status.SetText(I18n.T("working"));
        var client = _client;
        _pending = Task.Run(() => client.ReleaseAbove(_path, boundary, layers));
    }

    private void AddAction(QHBoxLayout layout, LayerProbe layer, string key, string action, bool enabled)
    {
        var button = new QPushButton(I18n.T(key));
        button.SetEnabled(enabled);
        button.Clicked += _ =>
        {
            if (_pending != null) return;
            if (action == LayerActions.DropLocal && QMessageBox.Question(this, I18n.T("confirm"),
                I18n.T(key) + "\n" + _path, I18n.T("confirm"), I18n.T("cancel"), null, 1, 1) != 0) return;
            _status.SetText(I18n.T("working"));
            var client = _client;
            _pending = Task.Run(() => client.LayerAction(_path, layer.Name, action));
        };
        layout.AddWidget(button);
    }

    internal static void SmokeBoundary()
    {
        var view = new LayerBoundaryView();
        try
        {
            view.SetProbe(new ProbeResult { Layers = new() { new() { Name = "ram", Manageable = true }, new() { Name = "source" } } });
            view.SetBoundary(1);
            if (view.Boundary != 1) throw new InvalidOperationException("Object boundary selection was not applied");
            view.SetBoundary(99);
            if (view.Boundary != 2) throw new InvalidOperationException("Object boundary escaped the layer chain");
        }
        finally { view.DeleteLater(); }
    }
}

/// <summary>A draggable boundary across the actual provider records, not a fixed layer layout.</summary>
internal sealed class LayerBoundaryView : QWidget
{
    private ProbeResult? _probe;
    private bool _dragging;
    public int Boundary { get; private set; }
    public event Action? BoundaryChanged;
    private const int Top = 28, Row = 66;
    public LayerBoundaryView()
    {
        SetMinimumHeight(180);
        SetMouseTracking(true);
    }
    public override void ChangeEvent(QEvent e)
    {
        base.ChangeEvent(e);
        if (e.type() == QEvent.Type.LanguageChange) Update();
    }
    public void SetProbe(ProbeResult probe)
    {
        _probe = probe;
        Boundary = Math.Min(Boundary, probe.Layers.Count);
        SetFixedHeight(Top + probe.Layers.Count * Row + 28);
        Update();
    }
    public void SetBoundary(int boundary)
    {
        Boundary = Math.Clamp(boundary, 0, _probe?.Layers.Count ?? 0);
        Update(); BoundaryChanged?.Invoke();
    }
    private void MoveBoundary(QMouseEvent e)
    {
        SetBoundary((int)Math.Round((e.Position().Y() - Top) / Row));
    }
    public override void MousePressEvent(QMouseEvent e) { _dragging = true; MoveBoundary(e); }
    public override void MouseMoveEvent(QMouseEvent e) { if (_dragging) MoveBoundary(e); }
    public override void MouseReleaseEvent(QMouseEvent e) { MoveBoundary(e); _dragging = false; }
    public override void PaintEvent(QPaintEvent e)
    {
        using var p = new QPainter(AsQPaintDevice());
        p.SetRenderHint(QPainter.RenderHint.Antialiasing);
        try
        {
            if (_probe == null) return;
            for (int i = 0; i < _probe.Layers.Count; i++)
            {
                var layer = _probe.Layers[i];
                int y = Top + i * Row + 7;
                p.SetPen(new QPen(new QColor(layer.Effective ? TrayTheme.Tokens.Success : TrayTheme.Tokens.BorderStrong)));
                p.SetBrush(new QBrush(new QColor(i < Boundary && layer.Manageable ? TrayTheme.Tokens.DangerSoft : TrayTheme.Tokens.Surface)));
                p.DrawRoundedRect(20, y, Width - 40, Row - 14, 8, 8);
                p.SetBrush(new QBrush(TrayTheme.Series(string.IsNullOrEmpty(layer.Color) ? TrayTheme.Tokens.Faint : layer.Color)));
                p.DrawEllipse(34, y + 18, 14, 14);
                p.SetPen(new QPen(new QColor(TrayTheme.Tokens.Body)));
                p.DrawText(60, y + 21, layer.Name + " · " + I18n.T(layer.Effective ? "object.active" : layer.Exists ? "object.recorded" : "object.absent"));
                p.SetPen(new QPen(new QColor(TrayTheme.Tokens.Muted)));
                p.DrawText(60, y + 40, I18n.T(!layer.Manageable ? "object.skipReadonly" : i < Boundary ? "object.willRelease" : "object.keep"));
            }
            int lineY = Top + Boundary * Row;
            using var pen = new QPen(new QColor(TrayTheme.Tokens.Primary)); pen.SetWidthF(3); p.SetPen(pen);
            p.DrawLine(10, lineY, Width - 10, lineY);
            p.SetBrush(new QBrush(new QColor(TrayTheme.Tokens.Primary))); p.DrawEllipse(1, lineY - 7, 14, 14);
            p.SetPen(new QPen(new QColor(TrayTheme.Tokens.Primary)));
            p.DrawText(22, 18, I18n.T("object.dragBoundary"));
        }
        finally { p.End(); }
    }
}
