using QtSharp.Api;
using QtSharp.Charts;

namespace PolyMount.Tray;

internal sealed record AccessSlice(string Name, string Color, long Reads, long Writes);

/// <summary>Which layer answered the reads, and which answered the writes: two pies rather than one
/// share of both. They are different accounts — a layer that served a thousand reads and no writes
/// is a share of the reads and nothing at all in the writes — and a circle that adds them up answers
/// with a number that belongs to neither. Each pie is the charts module's, and each is drawn inside
/// the page's own kind of card: the frame and the corner title the trend charts above are drawn in,
/// so the section reads as one more row of the same page rather than as a picture pasted into it.
/// </summary>
internal sealed class LayerAccessPie : QWidget
{
    private readonly PieCard _reads = new(reads: true), _writes = new(reads: false);
    private List<AccessSlice> _slices = new();

    public LayerAccessPie()
    {
        var row = new QHBoxLayout(this);
        row.SetContentsMargins(0, 0, 0, 0);
        // Two cards to a row, with the gap the grid of trend cards is laid out with.
        row.SetSpacing(16);
        row.AddWidget(_reads);
        row.AddWidget(_writes);
        // A language change re-reads both titles and both legends from the catalog.
        I18n.Changed += () => SetSlices(_slices);
    }

    /// <summary>What each layer answered over the window the page is reading. Both cards are given
    /// the same layers and each counts the direction it is about; a layer that answered none of a
    /// direction is not in its circle at all.</summary>
    public void SetSlices(List<AccessSlice> slices)
    {
        _slices = slices;
        _reads.SetSlices(slices, I18n.T("stats.access.reads"));
        _writes.SetSlices(slices, I18n.T("stats.access.writes"));
    }

    /// <summary>One of the two shares, as a card of the page's own kind: the frame, the corner title
    /// and the chart inside it, which is how every card on this page is laid out. The title is the
    /// card's rather than the chart's — a chart's own title is centred over the middle of what it
    /// draws, and no other card here names itself in the middle of itself.</summary>
    private sealed class PieCard : QWidget
    {
        /// <summary>What the card keeps for its own furniture: the strip above the chart, which
        /// holds the title, and the frame's inset at the sides and under it. The trend cards keep the
        /// same strip with a headline in it; a pie has nothing above its chart but its name.</summary>
        private const int Header = 40, Inset = 4, Footer = 4;
        private readonly Pie _pie;
        private string _title = "";

        public PieCard(bool reads)
        {
            _pie = new Pie(reads);
            // The card is as tall as the trend cards next to it, and it is this card that decides
            // that rather than the chart inside it: a chart view comes with a size hint of 648x524,
            // and a row of cards with nothing else in it will take it — a circle half a metre tall
            // among cards of 280 pixels is not the same page. So the chart is told that its own
            // hint is worth nothing here and it takes whatever the card's layout gives it.
            _pie.SetSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Ignored);
            SetMinimumSize(330, 280);
            var layout = new QVBoxLayout(this);
            layout.SetContentsMargins(Inset, Header, Inset, Footer);
            layout.AddWidget(_pie);
        }

        public void SetSlices(List<AccessSlice> slices, string title)
        {
            _title = title;
            _pie.SetSlices(slices);
            Update();
        }

        public override void PaintEvent(QPaintEvent e)
        {
            using var p = new QPainter(AsQPaintDevice());
            try { CardFace.Paint(p, Width, Height, _title); }
            finally { p.End(); }
        }
    }

    /// <summary>One of the two circles: whole, with its own legend beside it.</summary>
    private sealed class Pie : QChartView
    {
        private readonly QChart _chart = new();
        private readonly QPieSeries _series = new();
        private readonly bool _reads;

        public Pie(bool reads)
        {
            _reads = reads;
            SetChart(_chart);
            SetRenderHint(QPainter.RenderHint.Antialiasing);
            // The pie sits on the card's white, not on the platform's window colour: the chart
            // itself draws nothing behind its slices.
            SetBackgroundBrush(new QBrush(new QColor("#ffffff")));
            _chart.SetAnimationOptions(QChart.AnimationOptions.NoAnimation);
            _chart.SetBackgroundVisible(false);
            _chart.SetPlotAreaBackgroundVisible(false);
            _chart.SetPlotAreaBackgroundBrush(new QBrush(new QColor("#ffffff")));
            // The card around it keeps the same inset the trend cards keep, and the chart keeps
            // nothing more: its own padding is a floor, so what is asked for here is a little of it
            // taken back, the way the line charts do.
            _chart.SetMargins(new QMargins(-8, -8, -8, -8));
            // The share of every layer is what the pie is read for, so the slices are named in its
            // own legend rather than beside it — and the legend stands beside the circle rather than
            // under it: under it, the row is only as wide as the circle and Qt elides every name to
            // three letters and a dot to fit them all in; beside it, the list is as wide as the
            // longest name needs and nothing is cut.
            _chart.Legend().SetAlignment(Qt.Alignment.AlignRight);
            _chart.Legend().SetBackgroundVisible(false);
            _chart.Legend().SetLabelColor(new QColor("#334155"));
            // A size down from the cards' legends: it is what the longest of these names needs to be
            // read whole, and a list of elided names is a pie nobody can read.
            _chart.Legend().SetFont(new QFont("Segoe UI", 7));
            // A whole circle rather than a ring: a share is read off the angle a slice covers, and a
            // hole in the middle of it is space the reader is not counting anything in — it only
            // makes the angle harder to see. The circle fills its box.
            _series.SetHoleSize(0);
            _series.SetPieSize(0.82);
            _series.SetLabelsVisible(false);
            _chart.AddSeries(_series);
        }

        public void SetSlices(List<AccessSlice> slices)
        {
            long Count(AccessSlice slice) => _reads ? slice.Reads : slice.Writes;
            _series.Clear();
            long total = slices.Sum(Count);
            if (total == 0)
            {
                using var empty = new QPieSlice();
                empty.SetValue(1);
                empty.SetBrush(new QBrush(new QColor("#e2e8f0")));
                empty.SetPen(new QPen(Qt.PenStyle.NoPen));
                empty.SetLabel(I18n.T(_reads ? "stats.access.noreads" : "stats.access.nowrites"));
                _series.Append(empty);
                return;
            }
            foreach (var slice in slices.Where(item => Count(item) > 0))
            {
                using var piece = new QPieSlice();
                piece.SetValue(Count(slice));
                piece.SetBrush(new QBrush(new QColor(slice.Color)));
                piece.SetPen(new QPen(Qt.PenStyle.NoPen));
                // Which layer it is and what share of this direction it answered, in the one place
                // the chart prints words of its own.
                piece.SetLabel(I18n.F("stats.access.slice", slice.Name, Count(slice), 100.0 * Count(slice) / total));
                _series.Append(piece);
            }
        }
    }
}
