using QtSharp.Api;
using QtSharp.Charts;

namespace PolyMount.Tray;

internal sealed record AccessSlice(string Name, string Color, long Reads, long Writes);

/// <summary>Which layer answered the reads and the writes, and which process asked for them: two
/// cards, one per direction, each holding the two ways that direction is read.
///
/// The layer says what served the traffic, the process says whose work it was, and that is the
/// difference between an instance one tool is using and an instance five of them are — so the two
/// circles sit together under the direction they are about rather than in a card of their own.
///
/// Each circle is the charts module's, and each card is the page's own kind — the frame and the
/// corner title the trend charts above are drawn in — so the section reads as one more row of the
/// same page.</summary>
internal sealed class LayerAccessPie : QWidget
{
    /// <summary>Which way a circle reads one direction of the traffic.</summary>
    private enum Axis
    {
        Layer,
        Process
    }

    private readonly SubjectCard _reads = new("stats.access.reads", reads: true);
    private readonly SubjectCard _writes = new("stats.access.writes", reads: false);
    private List<AccessSlice> _layers = new(), _processes = new();

    public LayerAccessPie()
    {
        var row = new QHBoxLayout(this);
        row.SetContentsMargins(0, 0, 0, 0);
        // Two cards to a row, with the gap the grid of trend cards above is laid out with.
        row.SetSpacing(16);
        row.AddWidget(_reads, 1);
        row.AddWidget(_writes, 1);
        // A language change re-reads the titles, the captions, the legends and what a circle says
        // when it has nothing to draw, for as long as this is on a page: the page goes when its tab
        // is closed.
        I18n.OnChanged(() => SetSlices(_layers, _processes), this);
    }

    /// <summary>What each layer answered and what each process asked, over the window the page is
    /// reading. Both directions are in each list, each circle counts the one it is about, and a row
    /// that answered none of a direction is not in that circle at all.</summary>
    public void SetSlices(List<AccessSlice> layers, List<AccessSlice> processes)
    {
        _layers = layers;
        _processes = processes;
        _reads.SetSlices(layers, processes);
        _writes.SetSlices(layers, processes);
    }

    /// <summary>Each card and the rectangle it was drawn in, beside the smallest it may be, for the
    /// smoke run: a card squeezed under its own minimum is a pair of circles whose legends have been
    /// cut, which is a card nobody can read rather than one that merely looks small.</summary>
    internal IEnumerable<(string Title, int Width, int Height, int MinWidth, int MinHeight)> Cards =>
        new[] { _reads, _writes }.Select(card => (card.Title, card.Width, card.Height, card.MinimumWidth, card.MinimumHeight));

    /// <summary>One direction, as a card of the page's own kind — the frame and the corner title
    /// every chart card wears — holding the two circles that direction is read by. The title is the
    /// card's rather than a chart's: a chart's own title is centred over the middle of what it draws,
    /// and no other card here names itself in the middle of itself.</summary>
    private sealed class SubjectCard : QWidget
    {
        /// <summary>What the card keeps for its own furniture: the strip above the circles, which
        /// holds the title, and the frame's inset at the sides and under them.</summary>
        private const int Header = 40, Inset = 4, Footer = 4;
        /// <summary>The narrowest a card may be drawn: two circles and the legends beside them have
        /// to fit inside it, and a card narrower than this cuts the names a reader is looking for.
        /// </summary>
        private const int CardWidth = 400, CardHeight = 250;
        private readonly Pie _byLayer, _byProcess;
        private readonly string _titleKey;
        private string _title = "";

        public SubjectCard(string titleKey, bool reads)
        {
            _titleKey = titleKey;
            _byLayer = new Pie(Axis.Layer, reads);
            _byProcess = new Pie(Axis.Process, reads);
            SetMinimumSize(CardWidth, CardHeight);
            // The height is capped, and that is what keeps the circles still: a card stretched by
            // the section it is in changes the rectangle its charts are laid out in, and a chart
            // laid out again is a pie that moves on every sample. The width is free to grow with the
            // window, which is a resize and not a refresh.
            SetMaximumHeight(CardHeight);
            var layout = new QVBoxLayout(this);
            layout.SetContentsMargins(Inset, Header, Inset, Footer);
            var row = new QHBoxLayout();
            row.SetContentsMargins(0, 0, 0, 0);
            row.SetSpacing(8);
            row.AddWidget(Captioned(_byLayer), 1);
            row.AddWidget(Captioned(_byProcess), 1);
            layout.AddLayout(row);
        }

        public string Title => _title;

        /// <summary>A circle with the axis it reads written over it: two circles side by side inside
        /// one card are two questions, and the card's title only says which traffic they are about.
        /// </summary>
        private static QWidget Captioned(Pie pie)
        {
            var holder = new QWidget();
            var column = new QVBoxLayout(holder);
            column.SetContentsMargins(0, 0, 0, 0);
            column.SetSpacing(2);
            var caption = new QLabel();
            caption.SetObjectName("muted");
            caption.SetAlignment(Qt.Alignment.AlignHCenter);
            I18n.Bind(caption.SetText, pie.AxisKey);
            column.AddWidget(caption);
            column.AddWidget(pie, 1);
            return holder;
        }

        public void SetSlices(List<AccessSlice> layers, List<AccessSlice> processes)
        {
            _title = I18n.T(_titleKey);
            _byLayer.SetSlices(layers);
            _byProcess.SetSlices(processes);
            Update();
        }

        public override void PaintEvent(QPaintEvent e)
        {
            using var p = new QPainter(AsQPaintDevice());
            try { CardFace.Paint(p, Width, Height, _title); }
            finally { p.End(); }
        }
    }

    /// <summary>One of the four circles: whole, with its own legend.</summary>
    private sealed class Pie : QChartView
    {
        private readonly QChart _chart = new();
        private readonly QPieSeries _series = new();
        private readonly bool _reads, _processes;

        public Pie(Axis axis, bool reads)
        {
            _reads = reads;
            _processes = axis == Axis.Process;
            SetChart(_chart);
            SetRenderHint(QPainter.RenderHint.Antialiasing);
            // A card's circle is repainted whole rather than in the pieces Qt works out have
            // changed: what a partial update leaves showing is the frame before, which is the pie
            // blinking at every sample.
            SetViewportUpdateMode(QGraphicsView.ViewportUpdateMode.FullViewportUpdate);
            // The pie sits on the card's surface, not on the platform's window colour: the chart
            // itself draws nothing behind its slices.
            SetBackgroundBrush(new QBrush(new QColor(TrayTheme.Tokens.Surface)));
            _chart.SetAnimationOptions(QChart.AnimationOptions.NoAnimation);
            _chart.SetBackgroundVisible(false);
            _chart.SetPlotAreaBackgroundVisible(false);
            _chart.SetPlotAreaBackgroundBrush(new QBrush(new QColor(TrayTheme.Tokens.Surface)));
            // The card around it keeps the same inset the trend cards keep, and the chart keeps
            // nothing more: its own padding is a floor, so what is asked for here is a little of it
            // taken back, the way the line charts do.
            _chart.SetMargins(new QMargins(-8, -8, -8, -8));
            // The share of every slice is what a circle is read for, so the slices are named in its
            // own legend rather than beside it — and the legend stands beside the circle rather than
            // under it: under it the row is only as wide as the circle and Qt elides every name to
            // three letters and a dot (measured, with the full label and with the name alone),
            // where beside it the list is as wide as the longest name needs.
            _chart.Legend().SetAlignment(Qt.Alignment.AlignRight);
            _chart.Legend().SetBackgroundVisible(false);
            _chart.Legend().SetLabelColor(new QColor(TrayTheme.Tokens.Body));
            // A size down from the cards' legends: these names are longer than a chart series' is.
            _chart.Legend().SetFont(new QFont("Segoe UI", 7));
            // A legend line is elided rather than wrapped, so a reader whose line came out short is
            // given the whole of it by hovering.
            _chart.Legend().SetShowToolTips(true);
            // A whole circle rather than a ring: a share is read off the angle a slice covers, and a
            // hole in the middle of it is space nothing is counted in.
            _series.SetHoleSize(0);
            _series.SetPieSize(0.82);
            _series.SetLabelsVisible(false);
            _chart.AddSeries(_series);
        }

        /// <summary>The caption this circle is labelled with: which of the two ways it reads.</summary>
        public string AxisKey => _processes ? "stats.access.byProcess" : "stats.access.byLayer";

        /// <summary>The scene is the widget. A chart is a graphics widget and lays itself out against
        /// the rectangle the scene grew to, which is its own idea of how big it wants to be — wider
        /// than the card, so its legend lands past the right edge of what the reader is looking at.
        /// Pinned on every resize, because the card is stretched by the section it is in and the
        /// chart would otherwise keep the size it was first given.</summary>
        private int _pinnedWidth = -1, _pinnedHeight = -1;

        private void Pin()
        {
            if (Width == _pinnedWidth && Height == _pinnedHeight) return;
            _pinnedWidth = Width;
            _pinnedHeight = Height;
            SetSceneRect(new QRectF(0, 0, Width, Height));
            _chart.SetGeometry(0, 0, Width, Height);
            // Where the legend goes is the chart's own layout, and it runs on its own schedule: a
            // card that has just been stretched would paint one frame with the circle and no legend,
            // which is what a photograph of it catches even though a reader never sees it.
            _chart.Layout?.Activate();
        }

        public override void ResizeEvent(QResizeEvent e)
        {
            base.ResizeEvent(e);
            Pin();
        }

        /// <summary>What the circle is holding, so a sample that changes only the numbers moves them
        /// rather than building the circle again: a chart handed a new set of slices every second
        /// rebuilds its legend and its items every second, which is what a reader sees as the pie
        /// blinking. The names decide whether the set is the same one — an instance that gains a
        /// layer, or a process that shows up for the first time, is a new circle and is built as
        /// one.</summary>
        private readonly List<QPieSlice> _pieces = new();
        private string _shape = "";

        public void SetSlices(List<AccessSlice> slices)
        {
            long Count(AccessSlice slice) => _reads ? slice.Reads : slice.Writes;
            Pin();
            var kept = slices.Where(item => Count(item) > 0).ToList();
            long total = kept.Sum(Count);
            string shape = total == 0
                // Which of the two empty circles this is: a process circle with nothing in it while
                // the layers beside it are full is not an idle instance, it is one whose reads the
                // service fetched for itself, ahead of anybody having asked.
                ? "empty:" + I18n.T(_processes ? "stats.process.unattributed" : _reads ? "stats.access.noreads" : "stats.access.nowrites")
                : string.Join("|", kept.Select(item => item.Name + " " + item.Color));
            if (shape != _shape)
            {
                _shape = shape;
                _series.Clear();
                foreach (var piece in _pieces) piece.Dispose();
                _pieces.Clear();
                if (total == 0)
                {
                    var empty = new QPieSlice();
                    empty.SetValue(1);
                    empty.SetBrush(new QBrush(new QColor(TrayTheme.Tokens.Border)));
                    empty.SetPen(new QPen(Qt.PenStyle.NoPen));
                    empty.SetLabel(shape["empty:".Length..]);
                    _series.Append(empty);
                    _pieces.Add(empty);
                    return;
                }
                foreach (var slice in kept)
                {
                    var piece = new QPieSlice();
                    piece.SetBrush(new QBrush(new QColor(slice.Color)));
                    piece.SetPen(new QPen(Qt.PenStyle.NoPen));
                    _series.Append(piece);
                    _pieces.Add(piece);
                }
            }
            for (int index = 0; index < kept.Count; index++)
            {
                double value = Count(kept[index]);
                // Which one it is, how much it was, and what share of this direction — in the one
                // place the chart prints words of its own. A slice's label is what its legend marker
                // reads, so the row of names is rewritten with the numbers and nothing else moves.
                _pieces[index].SetValue(value);
                _pieces[index].SetLabel(I18n.F("stats.access.slice", kept[index].Name, value, 100.0 * value / total));
            }
        }
    }
}
