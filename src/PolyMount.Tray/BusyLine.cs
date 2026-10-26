using QtSharp.QtCore;
using QtSharp.QtGui;
using QtSharp.QtWidgets;
using Qt = QtSharp.QtCore.Qt;

namespace PolyMount.Tray;

/// <summary>A thin line that travels while a window is waiting for the service to answer.
///
/// Every action the tray sends is a round trip to another process, and that process may be busy
/// with the source: a mount coming up, a save that reloads the instance, a service that is being
/// started or stopped. The buttons go flat and the status line says what is happening, but a
/// sentence that appears the moment a button is pressed and then simply stays there reads like the
/// click did nothing at all — the reader has no way to tell a two-second save from a hung window.
/// This is the thing that moves.
///
/// It is drawn here rather than being a QProgressBar in its own indeterminate mode: the theme takes
/// every control over with a stylesheet, and a styled progress bar is drawn by the stylesheet's
/// rules, not by the style's busy animation. What is drawn is a track the width of the window and
/// one rounded segment sweeping along it, because nobody knows how long a stopped service takes to
/// come back — a bar filling to a percentage would be making the duration up.</summary>
internal sealed class BusyLine : QWidget
{
    /// <summary>One sweep, in milliseconds. Slow enough to read as motion rather than as a
    /// stutter, quick enough that a quarter-second round trip still shows something.</summary>
    private const int Sweep = 1100;

    private readonly QTimer _timer;
    private DateTime _started;
    private bool _busy;

    public BusyLine()
    {
        SetFixedHeight(4);
        SetSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Fixed);
        _timer = new QTimer(this);
        // ~30 frames a second: the segment moves a couple of pixels a frame at any window width,
        // and a hidden line costs nothing because the timer is stopped with it.
        _timer.SetInterval(33);
        _timer.Timeout += () => { if (_busy) Update(); };
        Hide();
    }

    /// <summary>Whether the window is waiting. Showing and hiding is the caller's whole job; the
    /// line stops its own timer when there is nothing to say.</summary>
    public void SetBusy(bool busy)
    {
        if (_busy == busy) return;
        _busy = busy;
        if (busy)
        {
            _started = DateTime.UtcNow;
            _timer.Start();
        }
        else _timer.Stop();
        SetVisible(busy);
        Update();
    }

    public override void PaintEvent(QPaintEvent e)
    {
        using var p = new QPainter(AsQPaintDevice());
        p.SetRenderHint(QPainter.RenderHint.Antialiasing);
        using var noPen = new QPen(Qt.PenStyle.NoPen);
        p.SetPen(noPen);
        int width = Width, height = Height;
        if (width <= 0) return;
        p.SetBrush(new QBrush(new QColor(TrayTheme.Tokens.SurfaceSunken)));
        p.DrawRoundedRect(0, 0, width, height, height / 2.0, height / 2.0);
        // The segment is a fifth of the window and travels from entirely off its left edge to
        // entirely off its right one, so it enters and leaves rather than bouncing off the ends.
        int segment = Math.Max(48, width / 5);
        double phase = (DateTime.UtcNow - _started).TotalMilliseconds % Sweep / Sweep;
        int x = (int)Math.Round(-segment + (width + segment) * phase);
        p.SetBrush(new QBrush(new QColor(TrayTheme.Tokens.Primary)));
        p.DrawRoundedRect(x, 0, segment, height, height / 2.0, height / 2.0);
    }
}
