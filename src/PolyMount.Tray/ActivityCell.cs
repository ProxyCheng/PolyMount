using QtSharp.QtCore;
using QtSharp.QtGui;
using QtSharp.QtWidgets;

namespace PolyMount.Tray;

/// <summary>The activity of one instance, drawn in its row of the instance list: a minute of
/// requests a second as a curve, and the newest reading beside it as a number.
///
/// It is a sparkline — no axes, no labels, no grid, no legend — because that is the whole of what
/// fits in a table cell, and because the row it sits in is one answer to one question: is anybody
/// using this instance. The number is there beside the curve for two reasons: a shape a dozen
/// pixels tall cannot be read to a value, and colour is not something a reader should have to be
/// able to see to use this.
///
/// Drawn rather than charted. The performance page's cards are Qt Charts and are rebuilt from
/// series on every reading, which is the right trade at four hundred pixels wide and the wrong one
/// here: this repaints once a second, at a size where a chart's own margins would leave no plot
/// area at all. What it paints is a polyline over a filled area, scaled to its own minute, which is
/// the same rule the cards follow — the top of the curve is the busiest second in the window.
///
/// It takes the mouse itself because a cell widget covers the row's own cell: a click selects the
/// row the reader pointed at (so the detail panel follows it, exactly as a click on the name does),
/// and a double click opens the instance's performance page, which is the next question after this
/// one.</summary>
internal sealed class ActivityCell : QWidget
{
    /// <summary>The band the newest reading is written in, the gap after the curve, and the inset
    /// that keeps the curve off the cell's own edges.</summary>
    private const int NumberWidth = 46, Gap = 8, Inset = 2;

    private readonly ActivitySeries _series;
    private readonly string _name;
    private readonly Action<string>? _select;
    private readonly Action<string>? _open;
    private bool _selected;
    private string _hint = "";
    /// <summary>The shared top of the column's scale, or zero to draw against this row's own peak.</summary>
    private double _scale;

    internal ActivityCell(string name, ActivitySeries series, Action<string>? select, Action<string>? open)
    {
        _name = name;
        _series = series;
        _select = select;
        _open = open;
        SetCursor(new QCursor(Qt.CursorShape.PointingHandCursor));
    }

    /// <summary>Whether the row this sits in is the one the detail panel is showing. The number is
    /// painted in the selection's own ink when it is, because the row behind it has changed colour
    /// and the theme's ordinary secondary text is chosen for a surface.</summary>
    internal void SetSelected(bool selected)
    {
        if (_selected == selected) return;
        _selected = selected;
        Update();
    }

    internal void Refresh() => Update();

    /// <summary>The top of the column's shared scale: every curve in the list is drawn against the
    /// same one, so a row at a hundred a second stands twice as tall as a row at fifty.
    ///
    /// A curve scaled to its own minute answers "is this one doing anything"; scaled to the list's
    /// it answers "compared with the others, how much" — and the second is the question a column of
    /// them can answer and a single row cannot. The price is that a quiet instance is a flat line
    /// beside a busy one, which is the truth of the comparison and the thing the reader asked for.
    /// Zero leaves the cell on its own peak, which is what a curve drawn on its own — a check, a
    /// fixture — should do.</summary>
    internal void SetScale(double top)
    {
        if (Math.Abs(_scale - top) < 0.0001) return;
        _scale = top;
        Update();
    }

    /// <summary>The sentence a hover shows. It is set by the window rather than built here: the
    /// wording belongs to the catalogue, and this widget knows only what it draws.</summary>
    internal void SetHint(string hint)
    {
        if (_hint == hint) return;
        _hint = hint;
        SetToolTip(hint);
    }

    /// <summary>What the number beside the curve says. Nothing at all while no reading is held:
    /// a zero would be a measurement, and the absence of one is not.</summary>
    internal string Reading =>
        _series.Count == 0 ? "" : I18n.F("activity.rate", Figure(_series.Latest));

    /// <summary>A rate as it is written in a cell or in a column header: whole numbers once there
    /// are enough of them, one decimal below ten, and "0" rather than "0.0" for none.</summary>
    internal static string Figure(double value) =>
        value <= 0 ? "0" : value < 10 ? value.ToString("0.0", I18n.Culture) : value.ToString("N0", I18n.Culture);

    /// <summary>The smallest rung that holds this much: steps of 1, 1.5, 2, 3, 5 and 7.5 times a
    /// power of ten. The shared scale moves in these steps rather than tracking the busiest second
    /// exactly, so the whole column does not rescale itself on every reading — a scale that breathes
    /// would make every curve look like it was doing something. The rungs are closer together than
    /// the usual 1-2-5 ladder because a coarse one turns a small rise into a large jump: the top of
    /// the scale going from 200 to 500 because one instance read 205 would shrink every other row
    /// to two fifths of its height for no reason the reader can see.</summary>
    internal static double NiceCeiling(double value)
    {
        if (!(value > 0)) return 1;
        double step = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (double factor in new[] { 1.0, 1.5, 2.0, 3.0, 5.0, 7.5 })
            if (value <= factor * step) return Math.Max(1, factor * step);
        return Math.Max(1, 10 * step);
    }

    public override void PaintEvent(QPaintEvent e)
    {
        using var p = new QPainter(AsQPaintDevice());
        p.SetRenderHint(QPainter.RenderHint.Antialiasing);
        var palette = TrayTheme.Tokens;
        int width = Width, height = Height;
        if (width < 32 || height < 12) return;

        // The number takes the right-hand band whatever is in it, so the curve's right edge stays
        // where it is from one reading to the next: a curve that changes width when the reading
        // goes from three digits to four would look like it was redrawn rather than scrolled.
        int number = Math.Min(NumberWidth, Math.Max(24, width / 2));
        int curveRight = Math.Max(Inset + 4, width - number - Gap);
        int top = Math.Max(3, height / 2 - 11), bottom = Math.Min(height - 3, height / 2 + 11);
        if (bottom - top < 8) { top = 3; bottom = height - 3; }

        // The baseline is the zero line, and it is drawn even when nothing has been read: a row
        // that is mounted and quiet says so by holding this line still, which is a different thing
        // from a row with no graph in it at all.
        using (var axis = new QPen(new QColor(palette.BorderSoft)))
        {
            p.SetPen(axis);
            p.DrawLine(Inset, bottom, curveRight, bottom);
        }

        int count = _series.Count;
        if (count > 0)
        {
            // The top of the scale is the column's, with a floor of one a second: without the floor
            // the noise of a single request would be stretched over the whole height, and a list
            // doing nothing worth mentioning would look busy.
            double peak = _scale > 0 ? _scale : Math.Max(_series.Peak(), 1.0);
            // One step a second, so the curve slides at a steady pace whether it holds three
            // readings or sixty; what has not been read yet is empty space on the left.
            double step = (curveRight - Inset) / (double)(ActivitySeries.Capacity - 1);
            double first = curveRight - (count - 1) * step;
            using var line = new QPainterPath();
            using var area = new QPainterPath();
            for (int i = 0; i < count; i++)
            {
                double x = first + i * step;
                double y = bottom - Math.Clamp(_series[i] / peak, 0, 1) * (bottom - top);
                if (i == 0) { line.MoveTo(x, y); area.MoveTo(x, y); }
                else { line.LineTo(x, y); area.LineTo(x, y); }
            }
            if (!_series.Stale)
            {
                area.LineTo(curveRight, bottom);
                area.LineTo(first, bottom);
                area.CloseSubpath();
                using var fill = new QBrush(new QColor(palette.PrimaryTint));
                using var noPen = new QPen(Qt.PenStyle.NoPen);
                p.SetBrush(fill);
                p.SetPen(noPen);
                p.DrawPath(area);
            }
            using var stroke = new QPen(new QColor(_series.Stale ? palette.Faint : palette.Primary));
            stroke.SetWidthF(1.4);
            stroke.SetCapStyle(Qt.PenCapStyle.RoundCap);
            stroke.SetJoinStyle(Qt.PenJoinStyle.RoundJoin);
            p.SetPen(stroke);
            p.SetBrush(new QBrush(Qt.BrushStyle.NoBrush));
            p.DrawPath(line);
        }

        string reading = Reading;
        if (reading.Length > 0)
        {
            using var font = new QFont("Segoe UI", 8);
            p.SetFont(font);
            p.SetPen(new QPen(new QColor(_selected ? palette.SelectedText : palette.Muted)));
            p.DrawText(new QRect(curveRight + Gap, 0, width - curveRight - Gap, height),
                (int)Qt.Alignment.AlignRight | (int)Qt.Alignment.AlignVCenter, reading);
        }
    }

    /// <summary>A click on the curve is a click on the row: the detail panel follows the row the
    /// reader pointed at, which is what a click on the name would have done.
    ///
    /// Taken rather than passed on. An event a widget ignores is handed to the one above it, and the
    /// one above is the table, which would take the same double click as the row's own gesture and
    /// open the mount in Explorer beside the page this opens.</summary>
    public override void MouseReleaseEvent(QMouseEvent e)
    {
        e.Accept();
        _select?.Invoke(_name);
    }

    /// <summary>And a double click is the row's own gesture on the name — except that here it opens
    /// what the curve is about rather than the folder.</summary>
    public override void MouseDoubleClickEvent(QMouseEvent e)
    {
        e.Accept();
        _open?.Invoke(_name);
    }
}
