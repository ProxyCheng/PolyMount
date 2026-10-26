using QtSharp.QtCore;
using QtSharp.QtGui;
using QtSharp.QtWidgets;
using Qt = QtSharp.QtCore.Qt;

namespace PolyMount.Tray;

/// <summary>Minimize, maximize/restore and close, sharing the tab strip's row: the window has no
/// title bar of its own, so the strip is its first row and these sit where it ends. The marks are
/// painted rather than typed — the interface font has no "restore" glyph that lines up with the
/// others — and they take the theme's colours instead of the platform title bar's.</summary>
internal sealed class WindowChrome : QWidget
{
    private readonly ChromeButton _maximize;

    public WindowChrome(QWidget window)
    {
        var row = new QHBoxLayout(this);
        row.SetContentsMargins(0, 0, 4, 0);
        row.SetSpacing(2);
        var minimize = new ChromeButton(window, Mark.Minimize);
        _maximize = new ChromeButton(window, Mark.Maximize);
        var close = new ChromeButton(window, Mark.Close);
        minimize.Clicked += () => window.ShowMinimized();
        _maximize.Clicked += () => Toggle(window);
        close.Clicked += () => window.Close();
        row.AddWidget(minimize);
        row.AddWidget(_maximize);
        row.AddWidget(close);
        SetSizePolicy(QSizePolicy.Policy.Fixed, QSizePolicy.Policy.Preferred);
    }

    /// <summary>The maximize mark follows the window: one at its largest offers to restore, not to
    /// maximize again. A state changed by the window's own menu or by the keyboard does not come
    /// through the button that would have redrawn it, so the window asks for this when it resizes
    /// into or out of the maximum.</summary>
    public void Sync() => _maximize.Update();

    private static void Toggle(QWidget window)
    {
        if (window.IsMaximized()) window.ShowNormal();
        else window.ShowMaximized();
        window.Update();
    }

    private enum Mark { Minimize, Maximize, Close }

    private sealed class ChromeButton : QWidget
    {
        private readonly QWidget _window;
        private readonly Mark _mark;

        /// <summary>A press and a release inside the button, which is all a title-bar button does:
        /// a drag that leaves it does not fire it, and neither does a right click.</summary>
        public event Action? Clicked;

        public ChromeButton(QWidget window, Mark mark)
        {
            _window = window;
            _mark = mark;
            SetFixedSize(40, 28);
            SetFocusPolicy(Qt.FocusPolicy.NoFocus);
            SetCursor(new QCursor(Qt.CursorShape.ArrowCursor));
            SetAccessibleName(mark.ToString());
        }

        public override void MouseReleaseEvent(QMouseEvent e)
        {
            if (e.Button() == Qt.MouseButton.LeftButton && Rect.Contains(e.Pos())) Clicked?.Invoke();
            base.MouseReleaseEvent(e);
        }

        public override void EnterEvent(QEnterEvent e)
        {
            Update();
            base.EnterEvent(e);
        }

        public override void LeaveEvent(QEvent e)
        {
            Update();
            base.LeaveEvent(e);
        }

        public override void PaintEvent(QPaintEvent e)
        {
            var palette = TrayTheme.Tokens;
            bool hover = UnderMouse();
            bool close = _mark == Mark.Close;
            using var painter = new QPainter(AsQPaintDevice());
            painter.SetRenderHint(QPainter.RenderHint.Antialiasing);
            if (hover)
            {
                using var noPen = new QPen(Qt.PenStyle.NoPen);
                painter.SetPen(noPen);
                painter.SetBrush(new QBrush(new QColor(close ? palette.DangerStrong : palette.HoverSoft)));
                painter.DrawRoundedRect(0, 0, Width, Height, 5, 5);
            }
            using var pen = new QPen(new QColor(close && hover ? "#ffffff" : palette.Muted));
            pen.SetWidthF(1.2);
            pen.SetCapStyle(Qt.PenCapStyle.RoundCap);
            painter.SetPen(pen);
            int cx = Width / 2, cy = Height / 2;
            switch (_mark)
            {
                case Mark.Minimize:
                    painter.DrawLine(cx - 6, cy, cx + 6, cy);
                    break;
                case Mark.Maximize when _window.IsMaximized():
                    // Two sheets: the window at its largest, and the one it would come back to.
                    painter.DrawRect(cx - 3, cy - 5, 8, 8);
                    painter.DrawRect(cx - 6, cy - 2, 8, 8);
                    break;
                case Mark.Maximize:
                    painter.DrawRect(cx - 5, cy - 5, 11, 11);
                    break;
                case Mark.Close:
                    painter.DrawLine(cx - 5, cy - 5, cx + 5, cy + 5);
                    painter.DrawLine(cx + 5, cy - 5, cx - 5, cy + 5);
                    break;
            }
        }
    }
}
