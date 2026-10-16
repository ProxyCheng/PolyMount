using QtSharp.Api;
using QtSharp.Charts;

namespace PolyMount.Tray;

internal sealed record AccessSlice(string Name, string Color, long Reads, long Writes);

/// <summary>Which layer answered the reads and writes, as a share of them. The pie is the charts
/// module's — it divides the circle, colours the slices, keeps the hole in the middle and lists
/// what the slices are in its own legend — and all this holds is the data and which way round the
/// shares read.</summary>
internal sealed class LayerAccessPie : QChartView
{
    private readonly QChart _chart = new();
    private readonly QPieSeries _series = new();
    private List<AccessSlice> _slices = new();
    private string _title = "";

    public LayerAccessPie()
    {
        SetFixedSize(300, 260);
        SetChart(_chart);
        SetRenderHint(QPainter.RenderHint.Antialiasing);
        // The pie sits on the page's white, not on the platform's window colour: the chart itself
        // draws nothing behind its slices.
        SetBackgroundBrush(new QBrush(new QColor("#ffffff")));
        _chart.SetAnimationOptions(QChart.AnimationOptions.NoAnimation);
        _chart.SetBackgroundVisible(false);
        _chart.SetPlotAreaBackgroundVisible(false);
        _chart.SetPlotAreaBackgroundBrush(new QBrush(new QColor("#ffffff")));
        _chart.SetMargins(new QMargins(0, 0, 0, 0));
        // The share of every layer is what the pie is read for, so the slices are named in its own
        // legend rather than beside it: the legend is the chart's, and so is the layout that fits
        // it under the circle.
        _chart.Legend().SetAlignment(Qt.Alignment.AlignBottom);
        _chart.Legend().SetBackgroundVisible(false);
        _chart.Legend().SetLabelColor(new QColor("#334155"));
        _chart.Legend().SetFont(new QFont("Segoe UI", 8));
        _series.SetHoleSize(0.55);
        _series.SetPieSize(0.62);
        _series.SetLabelsVisible(false);
        _chart.AddSeries(_series);
        I18n.Changed += () => SetSlices(_slices, _title);
    }

    public void SetSlices(List<AccessSlice> slices, string title)
    {
        _slices = slices;
        _title = title;
        if (_chart.Title != title)
        {
            _chart.SetTitle(title);
            using var heading = new QFont("Segoe UI", 9);
            _chart.SetTitleFont(heading);
            _chart.SetTitleBrush(new QBrush(new QColor("#334155")));
        }
        _series.Clear();
        long total = slices.Sum(slice => slice.Reads + slice.Writes);
        if (total == 0)
        {
            using var empty = new QPieSlice();
            empty.SetValue(1);
            empty.SetBrush(new QBrush(new QColor("#e2e8f0")));
            empty.SetPen(new QPen(Qt.PenStyle.NoPen));
            empty.SetLabel(I18n.T("stats.noData"));
            _series.Append(empty);
            return;
        }
        foreach (var slice in slices.Where(slice => slice.Reads + slice.Writes > 0))
        {
            using var piece = new QPieSlice();
            piece.SetValue(slice.Reads + slice.Writes);
            piece.SetBrush(new QBrush(new QColor(slice.Color)));
            piece.SetPen(new QPen(Qt.PenStyle.NoPen));
            // What the layer did and what share of the window it is, in the one place the chart
            // prints words of its own.
            piece.SetLabel(I18n.F("stats.access.slice", slice.Name, slice.Reads, slice.Writes,
                100.0 * (slice.Reads + slice.Writes) / total));
            _series.Append(piece);
        }
    }
}
