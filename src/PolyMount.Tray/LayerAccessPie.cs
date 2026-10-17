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
    private bool _aligned = true;

    public LayerAccessPie()
    {
        var row = new QHBoxLayout(this);
        row.SetContentsMargins(0, 0, 0, 0);
        // Two cards to a row, with the gap the grid of trend cards above is laid out with.
        row.SetSpacing(16);
        row.AddWidget(_reads, 1);
        row.AddWidget(_writes, 1);
        _reads.Switched += Toggle;
        _writes.Switched += Toggle;
        // A language change re-reads the titles, the captions, the legends and what a subject says
        // when it has nothing to draw, for as long as this is on a page: the page goes when its tab
        // is closed. The headings that also switch the drawing are rewritten here too, which is why
        // the mode is set again rather than only the data.
        I18n.OnChanged(() =>
        {
            SetSlices(_layers, _rows);
            SetAligned(_aligned);
        }, this);
    }

    /// <summary>Whether the rows are drawn as columns standing on the layers or as one bar per
    /// caller. Both are true readings of the same table and they answer different questions: the
    /// columns say how much of a layer one caller used and let a reader compare down the card, and
    /// the bar says how much one caller used altogether and lets them compare along it. Neither is
    /// the fallback for the other.</summary>
    public bool Aligned => _aligned;

    /// <summary>Raised when the reader switched, so the page can remember the choice.</summary>
    internal event Action? ModeChanged;

    public void SetAligned(bool aligned)
    {
        _aligned = aligned;
        _reads.SetAligned(aligned);
        _writes.SetAligned(aligned);
    }

    private void Toggle()
    {
        SetAligned(!_aligned);
        ModeChanged?.Invoke();
    }

    /// <summary>The switch as a click reaches it, for the smoke run: a check that only called
    /// <see cref="SetAligned"/> would test the setter and not the wiring the reader uses.</summary>
    internal void SmokeToggle() => Toggle();

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
        /// <summary>The heading over the rows, which is also the switch between the two ways they
        /// can be drawn: it says which one is showing, and clicking it shows the other.</summary>
        private readonly Caption _caption = new();
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
            row.AddWidget(Captioned(_byLayer, null), 1);
            row.AddWidget(Captioned(_byProcess, _caption), 1);
            layout.AddLayout(row);
            Translate();
        }

        public string Title => _title;

        /// <summary>Raised when the reader asked for the other way of drawing the rows.</summary>
        internal event Action? Switched;

        private void Toggle() => Switched?.Invoke();

        /// <summary>Which way the rows are drawn, said in the heading over them.</summary>
        public void SetAligned(bool aligned)
        {
            _byProcess.SetAligned(aligned);
            Translate();
        }

        /// <summary>The heading's own words, in the language the page is in: it names the subject
        /// and, when it is the switch, which of the two drawings is showing.</summary>
        public void Translate()
        {
            _title = I18n.T(_titleKey);
            _caption.SetText(I18n.T(_byProcess.Aligned ? "stats.access.byProcess.aligned" : "stats.access.byProcess.stacked"));
            _caption.SetToolTip(I18n.T("stats.access.byProcess.hint"));
            Update();
        }

        /// <summary>A subject with the axis it reads written over it: two subjects side by side
        /// inside one card are two questions, and the card's title only says which traffic they are
        /// about.
        ///
        /// <paramref name="control"/> is the heading that switches between the two drawings of the
        /// rows; a subject given none gets an ordinary heading that only names it.</summary>
        private QWidget Captioned(IReadsOneWay subject, Caption? control)
        {
            var holder = new QWidget();
            var column = new QVBoxLayout(holder);
            column.SetContentsMargins(0, 0, 0, 0);
            column.SetSpacing(2);
            QLabel caption;
            if (control != null)
            {
                // The rows are read two ways and the heading says which one is showing, so the
                // switch needs no button beside it: a reader who wants the other one clicks the
                // word that named this one. The tooltip says so, because a heading that is also a
                // control is only discoverable if something says it is.
                control.SetCursor(new QCursor(Qt.CursorShape.PointingHandCursor));
                control.Clicked += Toggle;
                caption = control;
            }
            else
            {
                var plain = new QLabel();
                I18n.Bind(plain.SetText, subject.AxisKey);
                caption = plain;
            }
            caption.SetObjectName("muted");
            caption.SetAlignment(Qt.Alignment.AlignHCenter);
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

    /// <summary>A heading that is also a control: it names the subject and, when clicked, shows the
    /// subject the other way. A label rather than a button because it is a line of text in a column
    /// of text, and the page's muted style is what it should look like.
    /// </summary>
    private sealed class Caption : QLabel
    {
        internal event Action? Clicked;

        public override void MousePressEvent(QMouseEvent e)
        {
            if (e.Button() == Qt.MouseButton.LeftButton) Clicked?.Invoke();
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

    /// <summary>The callers, one row each, against the layers, one column each: a cell is what one
    /// layer did for one caller, in that layer's colour, as long as that caller's use of the layer
    /// is against the busiest caller's use of the same layer. The count of everything the row used
    /// is at its end; the circle beside it is what the columns add up to.
    ///
    /// Two drawings of the same table, and which one is showing is the reader's to choose (the
    /// heading over it is the switch):
    ///
    /// - One column per layer, each a fixed x in every row. A stack puts a layer's piece at a
    ///   different place in every row — it starts after whatever the row spent on the layers before
    ///   it — so the same layer could not be read down the card, and reading a layer down the card
    ///   is the question. It costs the one thing the stack had: a row's length is not its total.
    /// - One bar per caller, the layers back to back in one order. The length is the caller's
    ///   total against the busiest caller's, so the card answers "who is doing the most work" at a
    ///   glance and gives up the alignment.
    ///
    /// The columns are not equal either. Each one takes its width from what it carried — the card is
    /// this direction's traffic laid out left to right — so a layer that served a tenth of it gets a
    /// tenth of the room, and the widths are readable as the layers' own shares while the cells
    /// inside a column are normalised on that column (the busiest caller of a layer fills it). So a
    /// cell says "how much of this layer, against whoever used it most": a reading down a column,
    /// with the column's width saying how big the layer was in the first place.
    ///
    /// Painted rather than charted, and painted whole from the list it holds: a row is a name, some
    /// cells and a number, and Qt Charts offers neither a label at the end of a bar nor a grid of
    /// bars with a heading over each column — and a chart handed a new series every second is the
    /// blink the pie above has already been cured of. Nothing here is built per sample: the rows are
    /// read, measured and drawn, so a sample that changes only the numbers changes only the
    /// numbers.</summary>
    private sealed class Breakdown : QWidget, IReadsOneWay
    {
        /// <summary>The surface showing between two columns, so two full cells do not read as one
        /// bar. It is taken out of the width before the columns are divided into it, so the cells
        /// keep their own places whatever the card's width.</summary>
        private const int ColumnGap = 2;
        /// <summary>How tall a bar wants to be, and the range of rows the card will draw them in:
        /// below the floor a bar is a line, above the ceiling a list of three is spread out as if it
        /// were a list of ten.</summary>
        private const int BarHeight = 10, RowFloor = 14, RowCeiling = 26;
        /// <summary>The strip above the first row that names the columns: a grid of bars without a
        /// heading over each column is a table nobody can read across. Only the columns with room
        /// for a word get one — a heading that is three letters and a dot names nothing, and the
        /// legend beside the circle is where a colour's full name is anyway.</summary>
        private const int HeaderHeight = 14, HeadingFloor = 34;
        /// <summary>The widest the names may take, as a share of the card: past this the cells they
        /// are read against stop being comparable, and a name is elided instead.</summary>
        private const double NameShare = 0.42;
        /// <summary>The least a column is given, however little it carried, and the width below
        /// which the grid is not drawn at all: a layer's column shares the room with the others in
        /// proportion to what it served, but a column a few pixels wide is a line, not a reading —
        /// so a small layer is given this much, and the card keeps its names and its numbers when
        /// even that does not fit.</summary>
        private const int ColumnFloor = 18;

        private readonly bool _reads, _processes;
        private List<AccessRow> _rows = new();
        private bool _aligned = true;

        public Breakdown(Axis axis, bool reads)
        {
            _reads = reads;
            _processes = axis == Axis.Process;
        }

        public string AxisKey => _processes ? "stats.access.byProcess" : "stats.access.byLayer";

        /// <summary>Which of the two drawings is showing: columns standing on the layers, or one
        /// bar per caller.</summary>
        public bool Aligned => _aligned;

        public void SetAligned(bool aligned)
        {
            _aligned = aligned;
            Update();
        }

        public long Count(long reads, long writes) => _reads ? reads : writes;

        /// <summary>What one row is worth on this card: the layers' counts for the direction it
        /// reads, added up. A row that none of them served is not drawn at all.</summary>
        private long Total(AccessRow row) => row.Parts.Sum(part => Count(part.Reads, part.Writes));

        /// <summary>What one row used of one column.</summary>
        private long Value(AccessRow row, string layer)
        {
            long value = 0;
            foreach (var part in row.Parts)
                if (string.Equals(part.Name, layer, StringComparison.OrdinalIgnoreCase))
                    value += Count(part.Reads, part.Writes);
            return value;
        }

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

            // The columns: every layer this direction has traffic on, in one order for the whole
            // card and the busiest first. It is the order the circle's legend beside it reads in, so
            // a reader who has found a colour there finds its column here.
            var columns = rows
                .SelectMany(row => row.Parts)
                .Where(part => Count(part.Reads, part.Writes) > 0)
                .GroupBy(part => part.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => (Name: group.Key, Color: group.First().Color,
                    Sum: group.Sum(part => Count(part.Reads, part.Writes))))
                .OrderByDescending(column => column.Sum)
                .ToList();

            string[] figures = rows.Select(row => Total(row).ToString("N0", I18n.Culture)).ToArray();
            int figureWidth = figures.Max(text => values.HorizontalAdvance(text));
            int nameWidth = Math.Min(
                rows.Max(row => names.HorizontalAdvance(row.Name)),
                (int)(width * NameShare));
            int gridLeft = nameWidth + 8;
            int gridRight = width - figureWidth - 8;
            int span = gridRight - gridLeft - (_aligned ? ColumnGap * Math.Max(0, columns.Count - 1) : 0);
            // The columns take their width from what they carried, not from how many of them there
            // are: the card is the whole of this direction's traffic laid out left to right, so a
            // layer that served a tenth of it gets a tenth of the room. That is what makes the grid
            // readable down a column and across the card at once — the widths say how big each layer
            // was, and the cells inside a column say who used it. A layer too small to have a
            // readable column is given one anyway (ColumnFloor): its bar would be a line, but a
            // column that is not there at all reads as a layer nothing used.
            long carried = columns.Sum(column => column.Sum);
            int[] widths = new int[columns.Count];
            if (columns.Count > 0 && span > 0)
            {
                for (int column = 0; column < columns.Count; column++)
                    widths[column] = Math.Max(ColumnFloor, (int)Math.Round(span * (double)columns[column].Sum / carried));
                // The floors can ask for more room than there is; when they do, every column gives
                // back the same share of what it was given, so the order and the proportions stay.
                int asked = widths.Sum();
                if (asked > span)
                    for (int column = 0; column < columns.Count; column++)
                        widths[column] = Math.Max(1, widths[column] * span / asked);
            }
            int[] lefts = new int[columns.Count];
            for (int column = 0, at = gridLeft; column < columns.Count; column++)
            {
                lefts[column] = at;
                at += widths[column] + ColumnGap;
            }
            // A card too narrow to stand the columns in keeps its names and its numbers: a bar of a
            // few pixels is not a reading, and the figure is what the row is here for.
            bool grid = _aligned && columns.Count > 0 && widths.Max() >= ColumnFloor;
            // The other drawing needs no columns: every row is one bar, its pieces in the same order
            // as everybody else's, measured against the busiest row.
            bool bars = !_aligned && columns.Count > 0 && span >= ColumnFloor;
            long widest = rows.Max(Total);

            int header = grid ? HeaderHeight : 0;
            int rowHeight = Math.Clamp((height - header) / rows.Count, RowFloor, RowCeiling);
            int barHeight = Math.Max(4, Math.Min(BarHeight, rowHeight - 6));
            int top = Math.Max(0, (height - (header + rowHeight * rows.Count)) / 2);

            if (grid)
            {
                p.SetFont(nameFont);
                p.SetPen(new QPen(new QColor(TrayTheme.Tokens.Muted)));
                for (int column = 0; column < columns.Count; column++)
                {
                    // A column too narrow for a name is not given a shrug of an ellipsis: the colour
                    // is what names it, and the legend beside the circle is where the names are. An
                    // ellipsis over a column would read as a name that had been left out.
                    if (widths[column] < HeadingFloor) continue;
                    p.DrawText(new QRect(lefts[column], top, widths[column], HeaderHeight),
                        (int)(Qt.Alignment.AlignLeft | Qt.Alignment.AlignVCenter),
                        names.ElidedText(columns[column].Name, Qt.TextElideMode.ElideRight, widths[column]));
                }
            }

            // One denominator per column, and it is that column's own busiest row. Columns share no
            // scale, which is the price of standing them all on one x: a cell says how much of this
            // layer this caller used against whoever used it most, and the row's own total is the
            // number at its end.
            long[] largest = new long[columns.Count];
            for (int column = 0; column < columns.Count; column++)
                foreach (var row in rows) largest[column] = Math.Max(largest[column], Value(row, columns[column].Name));

            using var noPen = new QPen(Qt.PenStyle.NoPen);
            for (int index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                int y = top + header + index * rowHeight;
                if (nameWidth > 0)
                {
                    p.SetFont(nameFont);
                    p.SetPen(new QPen(new QColor(TrayTheme.Tokens.Muted)));
                    p.DrawText(new QRect(0, y, nameWidth, rowHeight),
                        (int)(Qt.Alignment.AlignLeft | Qt.Alignment.AlignVCenter),
                        names.ElidedText(row.Name, Qt.TextElideMode.ElideRight, nameWidth));
                }

                if (grid)
                {
                    int barY = y + (rowHeight - barHeight) / 2;
                    for (int column = 0; column < columns.Count; column++)
                    {
                        long value = Value(row, columns[column].Name);
                        if (value <= 0) continue;
                        int length = largest[column] == 0
                            ? 0
                            : (int)Math.Round(widths[column] * (double)value / largest[column]);
                        // What was counted is drawn, however little of the column it fills.
                        p.SetBrush(new QBrush(new QColor(columns[column].Color)));
                        p.SetPen(noPen);
                        p.DrawRect(lefts[column], barY, Math.Max(1, length), barHeight);
                    }
                }
                else if (bars)
                {
                    // One bar, the layers in the same order across the whole card so the bands can
                    // be followed from row to row even though they do not line up: a layer is always
                    // the third band from the left, wherever the first two ended.
                    int barY = y + (rowHeight - barHeight) / 2;
                    int length = (int)Math.Round(span * (double)Total(row) / widest);
                    int x = gridLeft;
                    var pieces = columns.Where(column => Value(row, column.Name) > 0).ToList();
                    for (int piece = 0; piece < pieces.Count; piece++)
                    {
                        // The last piece takes what rounding left over, so a bar adds up to its own
                        // length rather than a pixel short of it.
                        int part = piece == pieces.Count - 1
                            ? gridLeft + length - x
                            : (int)Math.Round(length * (double)Value(row, pieces[piece].Name) / Total(row));
                        // The seam is taken off the two sides that meet, so the surface shows through
                        // as a hairline rather than as a border drawn around anything.
                        int from = x + (piece == 0 ? 0 : ColumnGap / 2);
                        int to = x + part - (piece == pieces.Count - 1 ? 0 : ColumnGap / 2);
                        if (to > from)
                        {
                            p.SetBrush(new QBrush(new QColor(pieces[piece].Color)));
                            p.SetPen(noPen);
                            p.DrawRect(from, barY, to - from, barHeight);
                        }
                        x += part;
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
