using QtSharp.Api;
using QtSharp.Charts;

namespace PolyMount.Tray;

internal sealed record AccessSlice(string Name, string Color, long Reads, long Writes);

/// <summary>One layer's share of one caller's traffic.</summary>
internal sealed record AccessPart(string Name, string Color, long Reads, long Writes);

/// <summary>One caller, and what each layer did on its behalf. A row's parts add up to everything
/// the layers served it, so every row here summed with every other is the circle beside it — which
/// is the one thing neither margin could be read for on its own. A row has no colour of its own:
/// what is drawn is its pieces, in the layers' colours.</summary>
internal sealed record AccessRow(string Name, IReadOnlyList<AccessPart> Parts);

/// <summary>Which layer answered the reads and the writes, and who asked: two cards, one per
/// direction, each reading that direction two ways.
///
/// The circle is what the layers served between them; the rows beside it are the same traffic split
/// by who asked, each row a bar in the layers' own colours. One circle and one list, rather than two
/// circles: two marginals of one table can be drawn side by side forever without a reader being able
/// to put them together — nothing on the page said which process was using which layer, and the two
/// circles did not even share a denominator (a layer serves the read-ahead nobody asked for, and a
/// process makes metadata calls no layer answers). The rows are the table's other half, and they add
/// up to the circle, so a reader can check that they do.
///
/// Each circle is the charts module's, and each card is the page's own kind — the frame and the
/// corner title the trend charts above are drawn in — so the section reads as one more row of the
/// same page. The rows are painted rather than charted: Qt Charts has no label at the end of a bar
/// and no gap between the pieces of one, and a series rebuilt on every sample is the blink this page
/// has already paid for twice. Rows of rectangles are a thing QPainter draws exactly.</summary>
internal sealed class LayerAccessPie : QWidget
{
    /// <summary>Which way a card reads one direction of the traffic.</summary>
    private enum Axis
    {
        Layer,
        Process
    }

    /// <summary>What both subjects of a card are: one that knows which of the two ways its direction
    /// is read, so that the caption over it and the numbers in it are decided the same way. An
    /// interface rather than a base class because the circle has to be a chart view and the rows
    /// have to be a widget, and .NET gives a class one base.</summary>
    private interface IReadsOneWay
    {
        /// <summary>The caption this subject is labelled with: which of the two ways it reads.</summary>
        string AxisKey { get; }
        /// <summary>One direction's count — reads on one card, writes on the other, which is the
        /// whole of what the two cards differ by.</summary>
        long Count(long reads, long writes);
    }

    private readonly SubjectCard _reads = new("stats.access.reads", reads: true);
    private readonly SubjectCard _writes = new("stats.access.writes", reads: false);
    private List<AccessSlice> _layers = new();
    private List<AccessRow> _rows = new();

    public LayerAccessPie()
    {
        var row = new QHBoxLayout(this);
        row.SetContentsMargins(0, 0, 0, 0);
        // Two cards to a row, with the gap the grid of trend cards above is laid out with.
        row.SetSpacing(16);
        row.AddWidget(_reads, 1);
        row.AddWidget(_writes, 1);
        // A language change re-reads the titles, the captions, the legends and what a subject says
        // when it has nothing to draw, for as long as this is on a page: the page goes when its tab
        // is closed.
        I18n.OnChanged(() => SetSlices(_layers, _rows), this);
    }

    /// <summary>What each layer answered and what each caller asked, over the window the page is
    /// reading. Both directions are in every row, each card counts the one it is about, and a row
    /// that answered none of a direction is not in that card's list at all.</summary>
    public void SetSlices(List<AccessSlice> layers, List<AccessRow> rows)
    {
        _layers = layers;
        _rows = rows;
        _reads.SetSlices(layers, rows);
        _writes.SetSlices(layers, rows);
    }

    /// <summary>Each card and the rectangle it was drawn in, beside the smallest it may be, for the
    /// smoke run: a card squeezed under its own minimum is one whose legend or whose row names have
    /// been cut, which is a card nobody can read rather than one that merely looks small.</summary>
    internal IEnumerable<(string Title, int Width, int Height, int MinWidth, int MinHeight)> Cards =>
        new[] { _reads, _writes }.Select(card => (card.Title, card.Width, card.Height, card.MinimumWidth, card.MinimumHeight));

    /// <summary>One direction, as a card of the page's own kind — the frame and the corner title
    /// every chart card wears — holding the circle and the rows that direction is read by. The title
    /// is the card's rather than a chart's: a chart's own title is centred over the middle of what it
    /// draws, and no other card here names itself in the middle of itself.</summary>
    private sealed class SubjectCard : QWidget
    {
        /// <summary>What the card keeps for its own furniture: the strip above the two subjects,
        /// which holds the title, and the frame's inset at the sides and under them.</summary>
        private const int Header = 40, Inset = 4, Footer = 4;
        /// <summary>The narrowest a card may be drawn: a circle with its legend and a list of rows
        /// with their names have to fit inside it, and a card narrower than this cuts the names a
        /// reader is looking for.
        /// </summary>
        private const int CardWidth = 400, CardHeight = 250;
        private readonly Pie _byLayer;
        private readonly Breakdown _byProcess;
        private readonly string _titleKey;
        private string _title = "";

        public SubjectCard(string titleKey, bool reads)
        {
            _titleKey = titleKey;
            _byLayer = new Pie(Axis.Layer, reads);
            _byProcess = new Breakdown(Axis.Process, reads);
            SetMinimumSize(CardWidth, CardHeight);
            // The height is capped, and that is what keeps the circle still: a card stretched by the
            // section it is in changes the rectangle its chart is laid out in, and a chart laid out
            // again is a pie that moves on every sample. The width is free to grow with the window,
            // which is a resize and not a refresh.
            SetMaximumHeight(CardHeight);
            var layout = new QVBoxLayout(this);
            layout.SetContentsMargins(Inset, Header, Inset, Footer);
            var row = new QHBoxLayout();
            row.SetContentsMargins(0, 0, 0, 0);
            row.SetSpacing(8);
            // An even split, which is what the two circles had: the rows need no more of the card
            // than the circle does, and every pixel taken from the circle is a letter taken off the
            // end of a layer's name in the legend both halves are read by.
            row.AddWidget(Captioned(_byLayer), 1);
            row.AddWidget(Captioned(_byProcess), 1);
            layout.AddLayout(row);
        }

        public string Title => _title;

        /// <summary>A subject with the axis it reads written over it: two subjects side by side
        /// inside one card are two questions, and the card's title only says which traffic they are
        /// about.
        /// </summary>
        private static QWidget Captioned(IReadsOneWay subject)
        {
            var holder = new QWidget();
            var column = new QVBoxLayout(holder);
            column.SetContentsMargins(0, 0, 0, 0);
            column.SetSpacing(2);
            var caption = new QLabel();
            caption.SetObjectName("muted");
            caption.SetAlignment(Qt.Alignment.AlignHCenter);
            I18n.Bind(caption.SetText, subject.AxisKey);
            column.AddWidget(caption);
            column.AddWidget((QWidget)subject, 1);
            return holder;
        }

        public void SetSlices(List<AccessSlice> layers, List<AccessRow> rows)
        {
            _title = I18n.T(_titleKey);
            _byLayer.SetSlices(layers);
            _byProcess.SetRows(rows);
            Update();
        }

        public override void PaintEvent(QPaintEvent e)
        {
            using var p = new QPainter(AsQPaintDevice());
            try { CardFace.Paint(p, Width, Height, _title); }
            finally { p.End(); }
        }
    }

    /// <summary>One of the two circles: whole, with its own legend.</summary>
    private sealed class Pie : QChartView, IReadsOneWay
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
            // where beside it the list is as wide as the longest name needs. It is also the legend
            // the rows beside it are read by: a bar's pieces are the same layers in the same colours.
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

        public string AxisKey => _processes ? "stats.access.byProcess" : "stats.access.byLayer";

        public long Count(long reads, long writes) => _reads ? reads : writes;

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
            Pin();
            var kept = slices.Where(item => Count(item.Reads, item.Writes) > 0).ToList();
            long total = kept.Sum(item => Count(item.Reads, item.Writes));
            string shape = total == 0
                // Which of the two empty circles this is: the layers' side has nothing to draw when
                // the instance is idle, and the rows beside it have nothing to draw when the reads
                // belong to nobody.
                ? "empty:" + I18n.T(_processes ? "stats.process.unattributed" : _reads ? "stats.access.noreads" : "stats.access.nowrites")
                : string.Join("|", kept.Select(item => item.Name + " " + item.Color));
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
                double value = Count(kept[index].Reads, kept[index].Writes);
                // Which one it is, how much it was, and what share of this direction — in the one
                // place the chart prints words of its own. A slice's label is what its legend marker
                // reads, so the row of names is rewritten with the numbers and nothing else moves.
                _pieces[index].SetValue(value);
                _pieces[index].SetLabel(I18n.F("stats.access.slice", kept[index].Name, value, 100.0 * value / total));
            }
        }
    }

    /// <summary>The callers, one row each: a bar in the layers' own colours, as long as that caller's
    /// traffic against the busiest caller's, with the count at its end. It is the table the circle
    /// beside it is the column total of.
    ///
    /// Painted rather than charted, and painted whole from the list it holds: a row is a name, a bar
    /// and a number, and Qt Charts offers neither a label at the end of a bar nor a gap between two
    /// pieces of one — and a chart handed a new series every second is the blink the pie above has
    /// already been cured of. Nothing here is built per sample: the rows are read, measured and
    /// drawn, so a sample that changes only the numbers changes only the numbers.</summary>
    private sealed class Breakdown : QWidget, IReadsOneWay
    {
        /// <summary>The surface showing between two pieces of one bar, and between a name and the
        /// bar it belongs to. Half of it is taken off each side of a join, so the seam is the same
        /// width seen from either piece.</summary>
        private const int Gap = 2;
        /// <summary>How tall a bar wants to be, and the range of rows the card will draw them in:
        /// below the floor a bar is a line, above the ceiling a list of three is spread out as if it
        /// were a list of ten.</summary>
        private const int BarHeight = 10, RowFloor = 14, RowCeiling = 26;
        /// <summary>The widest the names may take, as a share of the card: past this the bars they
        /// are read against stop being comparable, and a name is elided instead.</summary>
        private const double NameShare = 0.42;
        /// <summary>The least a bar may be worth drawing in: a bar a few pixels long is not a
        /// reading, and below this the card keeps its names and its numbers and drops the bars.
        /// </summary>
        private const int BarFloor = 24;

        private readonly bool _reads, _processes;
        private List<AccessRow> _rows = new();

        public Breakdown(Axis axis, bool reads)
        {
            _reads = reads;
            _processes = axis == Axis.Process;
        }

        public string AxisKey => _processes ? "stats.access.byProcess" : "stats.access.byLayer";

        public long Count(long reads, long writes) => _reads ? reads : writes;

        /// <summary>What one row is worth on this card: the layers' counts for the direction it
        /// reads, added up. A row that none of them served is not drawn at all.</summary>
        private long Total(AccessRow row) => row.Parts.Sum(part => Count(part.Reads, part.Writes));

        public void SetRows(List<AccessRow> rows)
        {
            _rows = rows;
            Update();
        }

        public override void PaintEvent(QPaintEvent e)
        {
            using var p = new QPainter(AsQPaintDevice());
            try { Draw(p); }
            finally { p.End(); }
        }

        private void Draw(QPainter p)
        {
            int width = Width, height = Height;
            if (width <= 8 || height <= 8) return;
            using var nameFont = new QFont("Segoe UI", 8);
            using var valueFont = new QFont("Segoe UI", 8);
            valueFont.SetBold(true);
            using var names = new QFontMetrics(nameFont);
            using var values = new QFontMetrics(valueFont);

            var rows = _rows.Where(row => Total(row) > 0).ToList();
            if (rows.Count == 0)
            {
                p.SetFont(nameFont);
                p.SetPen(new QPen(new QColor(TrayTheme.Tokens.Muted)));
                p.DrawText(new QRect(0, 0, width, height), (int)Qt.Alignment.AlignCenter,
                    I18n.T(_processes ? "stats.process.unattributed" : _reads ? "stats.access.noreads" : "stats.access.nowrites"));
                return;
            }

            // The bars are measured against the busiest row rather than against the whole: a row
            // drawn to its own total would be as long as every other row and would say nothing about
            // which caller is doing the work. The number at the end is what the length is read
            // against, and the circle beside it is what the bars add up to.
            long largest = rows.Max(Total);
            string[] figures = rows.Select(row => Total(row).ToString("N0", I18n.Culture)).ToArray();
            int figureWidth = figures.Max(text => values.HorizontalAdvance(text));
            int nameWidth = Math.Min(
                rows.Max(row => names.HorizontalAdvance(row.Name)),
                (int)(width * NameShare));
            int barLeft = nameWidth + 8;
            int barRight = width - figureWidth - 8;
            // A card too narrow to hold a name, a bar and a number keeps the names and the numbers:
            // a bar of a few pixels is not a reading, and the figure is what the row is here for.
            bool bars = barRight - barLeft >= BarFloor;
            if (!bars) nameWidth = 0;

            int rowHeight = Math.Clamp(height / rows.Count, RowFloor, RowCeiling);
            int barHeight = Math.Max(4, Math.Min(BarHeight, rowHeight - 6));
            int top = Math.Max(0, (height - rowHeight * rows.Count) / 2);
            using var noPen = new QPen(Qt.PenStyle.NoPen);
            for (int index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                int y = top + index * rowHeight;
                if (nameWidth > 0)
                {
                    p.SetFont(nameFont);
                    p.SetPen(new QPen(new QColor(TrayTheme.Tokens.Muted)));
                    p.DrawText(new QRect(0, y, nameWidth, rowHeight),
                        (int)(Qt.Alignment.AlignLeft | Qt.Alignment.AlignVCenter),
                        names.ElidedText(row.Name, Qt.TextElideMode.ElideRight, nameWidth));
                }

                long total = Total(row);
                if (bars && total > 0)
                {
                    int barWidth = (int)Math.Round((barRight - barLeft) * (double)total / largest);
                    int barY = y + (rowHeight - barHeight) / 2;
                    // The pieces are laid out back to back and the seam between them is taken off
                    // the two sides that meet, so the surface shows through as a hairline rather
                    // than as a border drawn around anything.
                    var pieces = row.Parts.Where(part => Count(part.Reads, part.Writes) > 0).ToList();
                    int x = barLeft;
                    for (int piece = 0; piece < pieces.Count; piece++)
                    {
                        int length = piece == pieces.Count - 1
                            ? barLeft + barWidth - x
                            : (int)Math.Round(barWidth * (double)Count(pieces[piece].Reads, pieces[piece].Writes) / total);
                        int from = x + (piece == 0 ? 0 : Gap / 2);
                        int to = x + length - (piece == pieces.Count - 1 ? 0 : Gap / 2);
                        if (to > from)
                        {
                            p.SetBrush(new QBrush(new QColor(pieces[piece].Color)));
                            p.SetPen(noPen);
                            p.DrawRect(from, barY, to - from, barHeight);
                        }
                        x += length;
                    }
                }

                p.SetFont(valueFont);
                p.SetPen(new QPen(new QColor(TrayTheme.Tokens.Body)));
                p.DrawText(new QRect(width - figureWidth, y, figureWidth, rowHeight),
                    (int)(Qt.Alignment.AlignRight | Qt.Alignment.AlignVCenter), figures[index]);
            }
        }
    }
}
