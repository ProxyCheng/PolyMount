using PolyMount.Core.Ipc;
using QtSharp.Api;

namespace PolyMount.Tray;

/// <summary>Custom-painted stacked-cylinder diagram of a path's per-layer state.</summary>
internal sealed class CylinderWidget : QWidget
{
    private const int LeftPad = 30, TopPad = 20, CylWidth = 200, SegHeight = 58, CapRy = 14, LabelGap = 20;

    private ProbeResult? _probe;

    public CylinderWidget() : base()
    {
        SetMinimumSize(560, 240);
    }

    public void SetProbe(ProbeResult probe)
    {
        _probe = probe;
        int n = Math.Max(1, probe.Layers.Count);
        SetMinimumHeight(TopPad * 2 + n * SegHeight + CapRy);
        Update();
    }

    public override void PaintEvent(QPaintEvent e)
    {
        var p = new QPainter(AsQPaintDevice());
        p.SetRenderHint(QPainter.RenderHint.Antialiasing);
        try
        {
            if (_probe == null) return;
            int x = LeftPad, y = TopPad;
            foreach (var layer in _probe.Layers)
            {
                DrawSegment(p, x, y, layer);
                DrawLabel(p, x + CylWidth + LabelGap, y, layer);
                y += SegHeight;
            }
            // bottom cap
            p.SetBrush(new QBrush(new QColor("#D9D9D9")));
            p.SetPen(new QPen(new QColor("#555555")));
            p.DrawEllipse(x, y - CapRy, CylWidth, CapRy * 2);
        }
        finally { p.End(); }
    }

    private void DrawSegment(QPainter p, int x, int y, LayerProbe layer)
    {
        var fill = new QColor(StateFill(layer));
        p.SetPen(new QPen(new QColor("#555555")));
        p.SetBrush(new QBrush(fill));
        p.DrawRect(x, y, CylWidth, SegHeight);
        p.DrawEllipse(x, y - CapRy, CylWidth, CapRy * 2);
        if (layer.Tombstoned)
        {
            p.SetPen(new QPen(new QColor("#FFFFFF")));
            p.DrawText(x + CylWidth / 2 - 6, y + SegHeight / 2 + 6, "X");
        }
    }

    private void DrawLabel(QPainter p, int x, int y, LayerProbe layer)
    {
        // identity-color chip
        p.SetPen(new QPen(new QColor("#555555")));
        p.SetBrush(new QBrush(new QColor(string.IsNullOrEmpty(layer.Color) ? "#999999" : layer.Color)));
        p.DrawRect(x, y + 6, 12, 12);

        p.SetPen(new QPen(new QColor("#000000")));
        string writable = layer.Writable ? " (writable)" : "";
        p.DrawText(x + 20, y + 16, $"{layer.Name}{writable}  [{StateLabel(layer)}]");
        p.SetPen(new QPen(new QColor("#555555")));
        string detail = layer.Tombstoned ? "tombstone: hides below"
            : layer.Exists ? (layer.IsDir ? "directory" : $"file · {layer.Size} B")
            : "no entry here (falls through)";
        p.DrawText(x + 20, y + 34, detail);
        p.DrawText(x + 20, y + 50, "gen: " + layer.Generation);
    }

    private static string StateFill(LayerProbe l) =>
        l.Tombstoned ? "#C63A3A" : l.Effective ? "#3CA84B" : l.Exists ? "#C99A2E" : "#D9D9D9";

    private static string StateLabel(LayerProbe l) =>
        l.Tombstoned ? "deleted" : l.Effective ? "effective" : l.Exists ? "shadowed" : "unknown";
}

/// <summary>Window wrapping the cylinder diagram plus per-layer management actions.</summary>
internal sealed class LayerVisualizer
{
    private readonly string _path;
    private readonly QWidget _window;
    private readonly QLabel _header;
    private readonly QLabel _legend;
    private readonly CylinderWidget _cylinder;

    private LayerVisualizer(string path)
    {
        _path = path;
        _window = new QWidget();
        _window.SetWindowTitle("PolyMount Layer View");
        _window.Resize(620, 560);

        var root = new QVBoxLayout(_window);
        _header = new QLabel();
        root.AddWidget(_header);

        _cylinder = new CylinderWidget();
        root.AddWidget(_cylinder);

        _legend = new QLabel("green=effective  amber=shadowed  red=deleted(tombstone)  gray=unknown");
        root.AddWidget(_legend);

        root.AddLayout(BuildActions());
        Refresh();
    }

    public static bool Open(string path)
    {
        var v = new LayerVisualizer(path);
        v._window.Show();
        v._window.Raise();
        v._window.ActivateWindow();
        return true;
    }

    private QHBoxLayout BuildActions()
    {
        var bar = new QHBoxLayout();
        AddAction(bar, "Restore (overlay)", "overlay", LayerActions.Restore);
        AddAction(bar, "Delete/tombstone (overlay)", "overlay", LayerActions.Hide);
        AddAction(bar, "Discard local (overlay)", "overlay", LayerActions.DropLocal);
        AddAction(bar, "Evict cache", "cache", LayerActions.EvictCache);
        bar.AddStretch(1);
        return bar;
    }

    private void AddAction(QHBoxLayout bar, string text, string layer, string action)
    {
        var b = new QPushButton(text);
        b.Clicked += _ =>
        {
            try { ControlClient.LayerAction(_path, layer, action); }
            catch { /* ignore; refresh shows state */ }
            Refresh();
        };
        bar.AddWidget(b);
    }

    private void Refresh()
    {
        try
        {
            var resp = ControlClient.Probe(_path);
            if (!resp.Ok || resp.Probe == null)
            {
                _header.SetText("probe failed: " + (resp.Error ?? "no data"));
                return;
            }
            var pr = resp.Probe;
            _header.SetText($"{pr.MountPoint}  rel=\"{(pr.Rel.Length == 0 ? "<root>" : pr.Rel)}\"  " +
                            (pr.Found ? $"effective: {pr.EffectiveLayer}" : "not visible (no effective layer / tombstoned)"));
            _cylinder.SetProbe(pr);
        }
        catch (Exception ex)
        {
            _header.SetText("service unreachable: " + ex.Message);
        }
    }
}
