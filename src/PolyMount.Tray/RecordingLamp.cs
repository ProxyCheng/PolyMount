using QtSharp.Api;

namespace PolyMount.Tray;

/// <summary>A dot that breathes while a recording runs. Qt needs no animation framework for this
/// and none is used: one shaded repaint every 600 ms says "live" without a frame budget, and a
/// recording page that cost a repaint per frame would be measuring itself.</summary>
internal sealed class RecordingLamp : QWidget
{
    internal enum LampMode
    {
        /// <summary>Nothing is being collected: the dot is there, dim and still.</summary>
        Idle,
        Live
    }

    private readonly QTimer _timer;
    private LampMode _mode = LampMode.Idle;
    private bool _bright = true;

    public RecordingLamp()
    {
        SetFixedSize(18, 18);
        _timer = new QTimer(this);
        _timer.Timeout += () =>
        {
            _bright = !_bright;
            if (_mode == LampMode.Live) Update();
        };
        _timer.Start(600);
    }

    public void SetMode(LampMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        _bright = true;
        Update();
    }

    public override void PaintEvent(QPaintEvent e)
    {
        using var p = new QPainter(AsQPaintDevice());
        p.SetRenderHint(QPainter.RenderHint.Antialiasing);
        try
        {
            bool live = _mode == LampMode.Live;
            using var color = new QColor(live ? TrayTheme.Tokens.DangerStrong : TrayTheme.Tokens.Faint);
            if (live && !_bright) color.SetAlpha(120);
            p.SetPen(new QPen(Qt.PenStyle.NoPen));
            p.SetBrush(new QBrush(color));
            p.DrawEllipse(2, 2, 14, 14);
        }
        finally { p.End(); }
    }
}
