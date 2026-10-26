using System.Diagnostics;
using QtSharp.QtCore;
using QtSharp.QtGui;
using QtSharp.QtWidgets;
using Qt = QtSharp.QtCore.Qt;

namespace PolyMount.Tray;

/// <summary>Non-interactive drag surfaces. Screenshots are captured once, not on every mouse move.</summary>
internal sealed class TabDragVisuals : QObject
{
    private readonly DragCard _card = new();
    private readonly DropSurface _drop = new();
    private readonly QTimer _timer;
    private readonly Stopwatch _transition = new();
    private (double X, double Y, double W, double H) _from, _to, _current;
    private bool _showDrop;
    private double _alpha;

    public TabDragVisuals(QObject parent) : base(parent)
    {
        _timer = new QTimer(this);
        _timer.Timeout += Animate;
        parent.Destroyed += _ => { _card.DeleteLater(); _drop.DeleteLater(); };
    }

    public void Begin(QWidget widget, string title, int x, int y)
    {
        _card.Capture(widget, title);
        _card.Move(x - 72, y - 24);
        _card.SetWindowOpacity(0.8);
        _card.Show();
        _timer.Start(16);
    }

    public void Move(int x, int y, string hint)
    {
        _card.Move(x - 72, y - 24);
        _card.Hint = hint;
        _card.Update();
        _card.Raise();
    }

    public void DropTarget(int x, int y, int width, int height, string title)
    {
        var next = ((double)x, (double)y, (double)width, (double)height);
        if (!_showDrop || _to != next)
        {
            _from = _showDrop ? _current : (x + 8, y + 8, width - 16, height - 16);
            _to = next;
            _transition.Restart();
        }
        _drop.Title = title;
        _showDrop = true;
        _drop.Show();
    }

    public void ClearTarget() { _showDrop = false; }

    public void End()
    {
        _timer.Stop();
        _card.Hide();
        _drop.Hide();
        _card.Clear();
        _showDrop = false;
        _alpha = 0;
    }

    private void Animate()
    {
        _alpha = Math.Clamp(_alpha + (_showDrop ? 0.16 : -0.2), 0, 1);
        if (_alpha == 0) { _drop.Hide(); return; }
        double t = Math.Clamp(_transition.Elapsed.TotalMilliseconds / 150, 0, 1);
        double ease = 1 - Math.Pow(1 - t, 3);
        double Lerp(double a, double b) => a + (b - a) * ease;
        _current = (Lerp(_from.X, _to.X), Lerp(_from.Y, _to.Y), Lerp(_from.W, _to.W), Lerp(_from.H, _to.H));
        _drop.SetGeometry((int)_current.X, (int)_current.Y, (int)_current.W, (int)_current.H);
        _drop.SetWindowOpacity(_alpha);
        _drop.Update();
    }

    private static void Prepare(QWidget widget)
    {
        widget.SetWindowFlags((Qt.WindowFlags)(Qt.WindowType.ToolTip | Qt.WindowType.FramelessWindowHint));
        widget.SetAttribute(Qt.WidgetAttribute.WA_TranslucentBackground);
        widget.SetAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents);
        widget.SetAttribute(Qt.WidgetAttribute.WA_ShowWithoutActivating);
        widget.SetFocusPolicy(Qt.FocusPolicy.NoFocus);
    }

    private sealed class DragCard : QWidget
    {
        private QPixmap? _image;
        private QIcon? _icon;
        private string _title = "";
        public string Hint { get; set; } = "";
        public DragCard() => Prepare(this);

        public void Capture(QWidget widget, string title)
        {
            Clear();
            using var shot = widget.Grab();
            int width = Math.Clamp(widget.Width, 280, 480);
            int height = Math.Clamp((int)(width * (double)widget.Height / Math.Max(1, widget.Width)), 160, 300);
            _image = shot.Scaled(width - 24, height, Qt.AspectRatioMode.KeepAspectRatio, Qt.TransformationMode.SmoothTransformation);
            _icon = widget.WindowIcon;
            _title = title;
            Resize(width, height + 88);
        }

        public void Clear() { _image?.Dispose(); _image = null; }

        public override void PaintEvent(QPaintEvent e)
        {
            using var p = new QPainter(AsQPaintDevice());
            p.SetRenderHint(QPainter.RenderHint.Antialiasing);
            p.SetRenderHint(QPainter.RenderHint.SmoothPixmapTransform);
            using var noPen = new QPen(Qt.PenStyle.NoPen);
            p.SetPen(noPen);
            for (int i = 9; i >= 1; i--)
            {
                using var shadow = new QBrush(new QColor(15, 23, 42, 3));
                p.SetBrush(shadow);
                p.DrawRoundedRect(12 - i, 14 - i, Width - 24 + 2 * i, Height - 28 + 2 * i, 14 + i, 14 + i);
            }
            using var body = new QBrush(new QColor(TrayTheme.Tokens.Surface));
            using var edge = new QPen(new QColor(TrayTheme.Tokens.BorderStrong));
            p.SetBrush(body); p.SetPen(edge);
            p.DrawRoundedRect(10, 8, Width - 20, Height - 24, 12, 12);
            using var icon = _icon?.Pixmap(20, 20);
            if (icon != null) p.DrawPixmap(22, 20, icon);
            using var font = new QFont("Segoe UI", 10);
            font.SetBold(true); p.SetFont(font);
            using var text = new QPen(new QColor(TrayTheme.Tokens.Text));
            p.SetPen(text);
            var titleRect = new QRect(50, 16, Width - 72, 28);
            using var metrics = new QFontMetrics(font);
            p.DrawText(titleRect, (int)(Qt.Alignment.AlignLeft | Qt.Alignment.AlignVCenter), metrics.ElidedText(_title, Qt.TextElideMode.ElideRight, Width - 72));
            if (_image != null)
            {
                using var background = new QBrush(new QColor(TrayTheme.Tokens.AppBackground));
                p.FillRect(12, 52, Width - 24, Height - 104, background);
                p.DrawPixmap(12, 52, _image);
            }
            font.SetBold(false); font.SetPointSize(9); p.SetFont(font);
            using var muted = new QPen(new QColor(TrayTheme.Tokens.Icon)); p.SetPen(muted);
            var hintRect = new QRect(22, Height - 46, Width - 44, 24);
            p.DrawText(hintRect, (int)(Qt.Alignment.AlignLeft | Qt.Alignment.AlignVCenter), Hint);
        }
    }

    private sealed class DropSurface : QWidget
    {
        public string Title { get; set; } = "";
        public DropSurface() => Prepare(this);
        public override void PaintEvent(QPaintEvent e)
        {
            using var p = new QPainter(AsQPaintDevice());
            p.SetRenderHint(QPainter.RenderHint.Antialiasing);
            using var wash = new QBrush(new QColor(37, 99, 235, 30));
            using var edge = new QPen(new QColor(59, 130, 246, 210));
            edge.SetWidthF(1.5);
            p.SetBrush(wash); p.SetPen(edge);
            p.DrawRoundedRect(4, 4, Width - 8, Height - 8, 12, 12);
            using var inset = new QPen(new QColor(255, 255, 255, 160));
            p.SetPen(inset);
            using var empty = new QBrush(Qt.BrushStyle.NoBrush); p.SetBrush(empty);
            p.DrawRoundedRect(7, 7, Width - 14, Height - 14, 10, 10);
            using var font = new QFont("Segoe UI", 10); font.SetBold(true); p.SetFont(font);
            using var metrics = new QFontMetrics(font);
            int badgeWidth = Math.Min(Width - 24, metrics.HorizontalAdvance(Title) + 32);
            using var badge = new QBrush(new QColor(TrayTheme.Tokens.Primary)); p.SetBrush(badge); p.SetPen(edge);
            p.DrawRoundedRect((Width - badgeWidth) / 2, 20, badgeWidth, 34, 8, 8);
            using var text = new QPen(new QColor(TrayTheme.Tokens.Surface)); p.SetPen(text);
            var rect = new QRect((Width - badgeWidth) / 2, 20, badgeWidth, 34);
            p.DrawText(rect, (int)Qt.Alignment.AlignCenter, metrics.ElidedText(Title, Qt.TextElideMode.ElideRight, badgeWidth - 16));
        }
    }
}
