using QtSharp.Api;
using QtSharp.Charts;

namespace PolyMount.Tray;

internal sealed record AccessSlice(string Name, string Color, long Reads, long Writes);

/// <summary>Which layer answered the reads, and which answered the writes: two pies rather than one
/// share of both. They are different accounts — a layer that served a thousand reads and no writes
/// is a share of the reads and nothing at all in the writes — and a circle that adds them up answers
/// with a number that belongs to neither. Each pie is the charts module's: it divides the circle,
/// colours the slices and names them in its own legend.</summary>
internal sealed class LayerAccessPie : QWidget
{
    private readonly Pie _reads = new(reads: true), _writes = new(reads: false);
    private List<AccessSlice> _slices = new();

    public LayerAccessPie()
    {
        var row = new QHBoxLayout(this);
        row.SetContentsMargins(0, 0, 0, 0);
        row.SetSpacing(8);
        row.AddStretch(1);
        row.AddWidget(_reads);
        row.AddWidget(_writes);
        row.AddStretch(1);
        // A language change re-reads both titles and both legends from the catalog.
        I18n.Changed += () => SetSlices(_slices);
    }

    /// <summary>What each layer answered over the window the page is reading. Both pies are given
    /// the same layers and each counts the direction it is about; a layer that answered none of a
    /// direction is not in its circle at all.</summary>
    public void SetSlices(List<AccessSlice> slices)
    {
        _slices = slices;
        _reads.SetSlices(slices, I18n.T("stats.access.reads"));
        _writes.SetSlices(slices, I18n.T("stats.access.writes"));
    }

    /// <summary>One of the two shares, as a whole circle with its own title and legend.</summary>
    private sealed class Pie : QChartView
    {
        private readonly QChart _chart = new();
        private readonly QPieSeries _series = new();
        private readonly bool _reads;

        public Pie(bool reads)
        {
            _reads = reads;
            // The section is the full width of the page and holds both circles side by side, so
            // each of them is given half of it: the layers are named in the legend under the circle
            // and a narrow card makes Qt elide every name to three letters and a dot.
            SetFixedSize(418, 230);
            SetChart(_chart);
            SetRenderHint(QPainter.RenderHint.Antialiasing);
            // The pie sits on the page's white, not on the platform's window colour: the chart
            // itself draws nothing behind its slices.
            SetBackgroundBrush(new QBrush(new QColor("#ffffff")));
            _chart.SetAnimationOptions(QChart.AnimationOptions.NoAnimation);
            _chart.SetBackgroundVisible(false);
            _chart.SetPlotAreaBackgroundVisible(false);
            _chart.SetPlotAreaBackgroundBrush(new QBrush(new QColor("#ffffff")));
            _chart.SetMargins(new QMargins(0, 0, 0, 0));
            // The share of every layer is what the pie is read for, so the slices are named in its
            // own legend rather than beside it — and the legend stands beside the circle rather than
            // under it: under it, the row is only as wide as the circle and Qt elides every name to
            // three letters and a dot to fit them all in; beside it, the list is as wide as the
            // longest name needs and nothing is cut.
            _chart.Legend().SetAlignment(Qt.Alignment.AlignRight);
            _chart.Legend().SetBackgroundVisible(false);
            _chart.Legend().SetLabelColor(new QColor("#334155"));
            _chart.Legend().SetFont(new QFont("Segoe UI", 7));
            // A whole circle rather than a ring: a share is read off the angle a slice covers, and a
            // hole in the middle of it is space the reader is not counting anything in — it only
            // makes the angle harder to see. The circle fills its box.
            _series.SetHoleSize(0);
            _series.SetPieSize(0.82);
            _series.SetLabelsVisible(false);
            _chart.AddSeries(_series);
        }

        public void SetSlices(List<AccessSlice> slices, string title)
        {
            if (_chart.Title != title)
            {
                _chart.SetTitle(title);
                using var heading = new QFont("Segoe UI", 9);
                _chart.SetTitleFont(heading);
                _chart.SetTitleBrush(new QBrush(new QColor("#334155")));
            }
            // The scene is the widget: a chart otherwise lays itself out against a rectangle of its
            // own making, which leaves its legend below the top of what the reader sees.
            SetSceneRect(new QRectF(0, 0, Width, Height));
            _chart.SetGeometry(0, 0, Width, Height);
            _series.Clear();
            long read(AccessSlice slice) => _reads ? slice.Reads : slice.Writes;
            long total = slices.Sum(read);
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
            foreach (var slice in slices.Where(item => read(item) > 0))
            {
                using var piece = new QPieSlice();
                piece.SetValue(read(slice));
                piece.SetBrush(new QBrush(new QColor(slice.Color)));
                piece.SetPen(new QPen(Qt.PenStyle.NoPen));
                // Which layer it is and what share of this direction it answered, in the one place
                // the chart prints words of its own.
                piece.SetLabel(I18n.F("stats.access.slice", slice.Name, read(slice), 100.0 * read(slice) / total));
                _series.Append(piece);
            }
        }
    }
}
