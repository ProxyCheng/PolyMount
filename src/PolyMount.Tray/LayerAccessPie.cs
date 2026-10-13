using QtSharp.Api;

namespace PolyMount.Tray;

internal sealed record AccessSlice(string Name, string Color, long Reads, long Writes);

internal sealed class LayerAccessPie : QWidget
{
    private List<AccessSlice> _slices = new();
    public LayerAccessPie()
    {
        SetFixedSize(200, 180);
        I18n.Changed += Update;
    }
    public void SetSlices(List<AccessSlice> slices) { _slices = slices; Update(); }
    public override void PaintEvent(QPaintEvent e)
    {
        using var painter = new QPainter(AsQPaintDevice());
        painter.SetRenderHint(QPainter.RenderHint.Antialiasing);
        try
        {
            long total = _slices.Sum(s => s.Reads + s.Writes);
            painter.SetPen(new QPen(Qt.PenStyle.NoPen));
            if (total == 0)
            {
                painter.SetBrush(new QBrush(new QColor("#e2e8f0")));
                painter.DrawEllipse(20, 10, 160, 160);
                painter.SetPen(new QPen(new QColor("#64748b")));
                painter.DrawText(36, 94, I18n.T("stats.noData"));
                return;
            }
            int start = 90 * 16;
            int consumed = 0;
            var active = _slices.Where(s => s.Reads + s.Writes > 0).ToList();
            for (int i = 0; i < active.Count; i++)
            {
                var slice = active[i];
                int span = i == active.Count - 1 ? 360 * 16 - consumed
                    : (int)Math.Round(360 * 16.0 * (slice.Reads + slice.Writes) / total);
                painter.SetBrush(new QBrush(new QColor(slice.Color)));
                painter.DrawPie(20, 10, 160, 160, start - consumed, -span);
                consumed += span;
            }
        }
        finally { painter.End(); }
    }
}
