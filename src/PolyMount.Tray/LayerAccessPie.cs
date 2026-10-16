using QtSharp.Api;
using QtSharp.Charts;

namespace PolyMount.Tray;

internal sealed record AccessSlice(string Name, string Color, long Reads, long Writes);

/// <summary>Which layer answered the reads and the writes, and which process asked for them: four
/// circles rather than one share of everything.
///
/// The two directions are two accounts — a layer that served a thousand reads and no writes is a
/// share of the reads and nothing at all in the writes — and adding them up answers with a number
/// that belongs to neither. Who asked is the other question the same traffic is read for: the layer
/// says what served it, the process says whose work it was, and that is the difference between an
/// instance one tool is using and an instance five of them are.
///
/// Each circle is the charts module's, and each is drawn inside the page's own kind of card — the
/// frame and the corner title the trend charts above are drawn in — so the section reads as one more
/// row of the same page rather than as a picture pasted into it.</summary>
internal sealed class LayerAccessPie : QWidget
{
    /// <summary>What one of the four is about: the direction it counts, and whether its slices are
    /// the layers that answered or the processes that asked.</summary>
    private enum Subject
    {
        ReadsByLayer,
        ReadsByProcess,
        WritesByLayer,
        WritesByProcess
    }

    private readonly PieCard _readsByLayer = new(Subject.ReadsByLayer);
    private readonly PieCard _readsByProcess = new(Subject.ReadsByProcess);
    private readonly PieCard _writesByLayer = new(Subject.WritesByLayer);
    private readonly PieCard _writesByProcess = new(Subject.WritesByProcess);
    private List<AccessSlice> _layers = new(), _processes = new();

    public LayerAccessPie()
    {
        // Two to a row, the way the trend cards are laid out above: what asked stands beside what
        // answered, so the two readings of one direction are one step apart — and a card at this
        // width gives its circle a legend that fits. Four across was tried and measured: at a
        // quarter of the page the legend is cut to three letters and a dot whichever side of the
        // circle it is put on, bottom or right.
        var grid = new QGridLayout(this);
        grid.SetContentsMargins(0, 0, 0, 0);
        grid.SetSpacing(16);
        grid.AddWidget(_readsByLayer, 0, 0);
        grid.AddWidget(_readsByProcess, 0, 1);
        grid.AddWidget(_writesByLayer, 1, 0);
        grid.AddWidget(_writesByProcess, 1, 1);
        // A language change re-reads every title, every legend and what a circle says when it has
        // nothing to draw, for as long as this is on a page: the page goes when its tab is closed.
        I18n.OnChanged(() => SetSlices(_layers, _processes), this);
    }

    /// <summary>What each layer answered and what each process asked, over the window the page is
    /// reading. Both directions are in each list, each card counts the one it is about, and a row
    /// that answered none of a direction is not in that circle at all.</summary>
    public void SetSlices(List<AccessSlice> layers, List<AccessSlice> processes)
    {
        _layers = layers;
        _processes = processes;
        foreach (var card in CardsOf()) card.SetSlices(card.SubjectIsProcess ? processes : layers);
    }

    /// <summary>Each circle's title and the rectangle it was drawn in, beside the smallest it may
    /// be, for the smoke run: a pie squeezed under its own minimum is a pie whose legend has been
    /// cut, and that is a card nobody can read rather than one that merely looks small.</summary>
    internal IEnumerable<(string Title, int Width, int Height, int MinWidth, int MinHeight)> Cards =>
        CardsOf().Select(card => (card.Title, card.Width, card.Height, card.MinimumWidth, card.MinimumHeight));

    private IEnumerable<PieCard> CardsOf()
    {
        yield return _readsByLayer;
        yield return _readsByProcess;
        yield return _writesByLayer;
        yield return _writesByProcess;
    }

    /// <summary>One of the four, as a card of the page's own kind: the frame, the corner title and
    /// the chart inside it, which is how every card on this page is laid out. The title is the
    /// card's rather than the chart's — a chart's own title is centred over the middle of what it
    /// draws, and no other card here names itself in the middle of itself.</summary>
    private sealed class PieCard : QWidget
    {
        /// <summary>What the card keeps for its own furniture: the strip above the chart, which
        /// holds the title, and the frame's inset at the sides and under it.</summary>
        private const int Header = 40, Inset = 4, Footer = 4;
        /// <summary>The narrowest a card may be drawn: the width a trend card may be, since these
        /// are laid out two to a row the same way, and what the legend beside the circle needs to
        /// show a process name, its count and its share without cutting any of them.</summary>
        private const int CardWidth = 330, CardHeight = 240;
        private readonly Pie _pie;
        private readonly Subject _subject;
        private string _title = "";

        public PieCard(Subject subject)
        {
            _subject = subject;
            _pie = new Pie(subject);
            SetMinimumSize(CardWidth, CardHeight);
            var layout = new QVBoxLayout(this);
            layout.SetContentsMargins(Inset, Header, Inset, Footer);
            layout.AddWidget(_pie);
        }

        public bool SubjectIsProcess => _subject is Subject.ReadsByProcess or Subject.WritesByProcess;
        public string Title => _title;

        public void SetSlices(List<AccessSlice> slices)
        {
            _title = I18n.T(_subject switch
            {
                Subject.ReadsByLayer => "stats.access.reads",
                Subject.ReadsByProcess => "stats.access.readsProcesses",
                Subject.WritesByLayer => "stats.access.writes",
                _ => "stats.access.writesProcesses"
            });
            _pie.SetSlices(slices, _subject is Subject.ReadsByLayer or Subject.ReadsByProcess);
            Update();
        }

        public override void PaintEvent(QPaintEvent e)
        {
            using var p = new QPainter(AsQPaintDevice());
            try { CardFace.Paint(p, Width, Height, _title); }
            finally { p.End(); }
        }
    }

    /// <summary>One of the four circles: whole, with its own legend under it.</summary>
    private sealed class Pie : QChartView
    {
        private readonly QChart _chart = new();
        private readonly QPieSeries _series = new();
        private readonly bool _reads, _processes;

        public Pie(Subject subject)
        {
            _reads = subject is Subject.ReadsByLayer or Subject.ReadsByProcess;
            _processes = subject is Subject.ReadsByProcess or Subject.WritesByProcess;
            SetChart(_chart);
            SetRenderHint(QPainter.RenderHint.Antialiasing);
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
            // where beside it the list is as wide as the longest name needs and nothing is cut.
            _chart.Legend().SetAlignment(Qt.Alignment.AlignRight);
            _chart.Legend().SetBackgroundVisible(false);
            _chart.Legend().SetLabelColor(new QColor(TrayTheme.Tokens.Body));
            // A size down from the cards' legends: these names are longer than a chart series' is,
            // and a legend that elides every one of them is a circle nobody can read.
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

        /// <summary>The scene is the widget. A chart is a graphics widget and lays itself out
        /// against the rectangle the scene grew to, which is its own idea of how big it wants to be
        /// — wider than the card, so its legend lands past the right edge of what the reader is
        /// looking at. Pinned on every resize, because the card is stretched by the section it is
        /// in and the chart would otherwise keep the size it was first given.</summary>
        private void Pin()
        {
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

        public void SetSlices(List<AccessSlice> slices, bool reads)
        {
            long Count(AccessSlice slice) => reads ? slice.Reads : slice.Writes;
            Pin();
            _series.Clear();
            long total = slices.Sum(Count);
            if (total == 0)
            {
                using var empty = new QPieSlice();
                empty.SetValue(1);
                empty.SetBrush(new QBrush(new QColor(TrayTheme.Tokens.Border)));
                empty.SetPen(new QPen(Qt.PenStyle.NoPen));
                // A process circle with nothing in it while the layers beside it are full is not an
                // idle instance: it is one whose reads the service fetched for itself, ahead of
                // anybody having asked. Saying which of the two it is is the card's whole value.
                empty.SetLabel(I18n.T(_processes
                    ? "stats.process.unattributed"
                    : reads ? "stats.access.noreads" : "stats.access.nowrites"));
                _series.Append(empty);
                return;
            }
            foreach (var slice in slices.Where(item => Count(item) > 0))
            {
                using var piece = new QPieSlice();
                piece.SetValue(Count(slice));
                piece.SetBrush(new QBrush(new QColor(slice.Color)));
                piece.SetPen(new QPen(Qt.PenStyle.NoPen));
                // Which one it is, how much it was, and what share of this direction — in the one
                // place the chart prints words of its own.
                piece.SetLabel(I18n.F("stats.access.slice", slice.Name, Count(slice), 100.0 * Count(slice) / total));
                _series.Append(piece);
            }
        }
    }
}
