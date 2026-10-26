using QtSharp.QtCore;
using QtSharp.QtGui;
using QtSharp.QtWidgets;

namespace PolyMount.Tray;

/// <summary>The performance page's cards, drawn from a fixture rather than from a service.
///
/// The page needs a service, a mounted instance and a minute of watching before it draws anything,
/// which is a lot to pay for looking at how a card is laid out: a change to a chart's axes, its
/// legend or its strips is a change to a picture, and the picture is what has to be looked at. This
/// feeds the same cards a deterministic set of readings, lays them out, saves each one and prints
/// the geometry each ended up with — the plot area against the rectangle the chart was given, which
/// is the one thing about a chart a reader cannot see.
///
/// Usage: PolyMount.Tray.exe --render &lt;directory&gt; [--size WxH] [--lang en|zh-CN]</summary>
internal sealed class ChartPreview : QWidget
{
    /// <summary>The cards the page draws, in the order it draws them: the six trends — read on
    /// the left, write on the right, one row per question — then the process section's two
    /// holdings (memory with the part no layer claims on top, and disk) and its own bytes. The
    /// page folds that section away; the preview lays it flat, because what is being looked at is
    /// the cards. The section's three level cards are not here: they are the same card with a
    /// level's unit, and the ones drawn are the shapes a chart can be wrong in.</summary>
    private static readonly (string TitleKey, string Color, string Unit, ChartSummary Summary)[] Cards =
    {
        ("stats.reads", "#2563eb", "/s", ChartSummary.Volume),
        ("stats.writes", "#dc2626", "/s", ChartSummary.Volume),
        ("stats.readBytes", "#059669", "B", ChartSummary.Volume),
        ("stats.writeBytes", "#0891b2", "B", ChartSummary.Volume),
        ("stats.readLatency", "#8b5cf6", "ms", ChartSummary.Latency),
        ("stats.writeLatency", "#d97706", "ms", ChartSummary.Latency),
        ("stats.layerHeld.memory", "#7c3aed", "B", ChartSummary.Gauge),
        ("stats.layerHeld.disk", "#c026d3", "B", ChartSummary.Gauge),
        ("stats.process.moved", "#475569", "B", ChartSummary.Volume)
    };
    /// <summary>The layers the fixture reads from, in the order a chain of them would be reported:
    /// the same names a mounted instance shows, one of them flat at zero because a layer that
    /// answered nothing is a shape a card has to draw honestly.</summary>
    private static readonly (string Name, string Color)[] Layers =
    {
        ("memory-cache", "#2563eb"), ("overlay", "#059669"), ("predict", "#8b5cf6"),
        ("cache", "#0891b2"), ("svn", "#f59e0b")
    };
    private readonly List<PerformanceChart> _cards = new();
    private readonly LayerAccessView _access = new();
    private readonly double _span;

    private ChartPreview(int width, int height, double span)
    {
        _span = span;
        SetObjectName("dashboard");
        
        var grid = new QGridLayout(this);
        grid.SetContentsMargins(24, 22, 24, 20);
        grid.SetSpacing(16);
        // Every card is drawn at the size it is given rather than at whatever the window leaves it:
        // what a chart does with its rectangle is the question, and a card stretched to fill a
        // window answers a different one each time.
        foreach (var (title, color, unit, summary) in Cards)
        {
            var card = new PerformanceChart(title, color, unit, summary);
            card.SetFixedSize(width, height);
            _cards.Add(card);
        }
        for (int i = 0; i < 6; i++) grid.AddWidget(_cards[i], i / 2, i % 2);
        grid.AddWidget(_cards[6], 3, 0);
        grid.AddWidget(_cards[7], 3, 1);
        grid.AddWidget(_cards[8], 4, 0, 1, 2);
        grid.AddWidget(_access, 5, 0, 1, 2);
        Resize(2 * width + 48 + 16, 5 * height + 42 + 4 * 16 + 240);
    }

    /// <summary>Draws every card once and writes it out, printing what each one's layout came to.
    /// Nothing is asked of a service and nothing is remembered: the fixture is what a page that has
    /// been watching a busy instance for one window would hold.</summary>
    internal static int Run(string directory, int width, int height, string? language)
    {
        string original = I18n.Language;
        if (language != null) I18n.SetLanguage(language);
        var preview = new ChartPreview(width, height, 60);
        preview.Show();
        // An axis is scaled from the height its plot is given, so the layout has to have run before
        // a fixture reaches a card.
        Pump();
        preview.Feed(DateTime.UtcNow);
        Pump();
        foreach (var card in preview._cards)
        {
            string name = "chart-" + card.TitleKey.Replace("stats.", "").Replace('.', '-') + ".png";
            using var painted = card.Grab();
            painted.Save(Path.Combine(directory, name));
            Console.WriteLine($"{name,-28} {card.Areas}{(card.LabelsTruncated ? "  LABELS TRUNCATED" : "")}");
            Console.WriteLine($"    {card.Domain}");
        }
        // The pie is set after the cards so its own layout runs with them; a pump here is what
        // gives it the geometry and the legend the chart lays out for itself.
        preview.PlaceAccess();
        Pump();
        using (var pie = preview._access.Grab()) pie.Save(Path.Combine(directory, "chart-pie.png"));
        // The same readings drawn the other way: the rows are a grid of columns by one click and one
        // stacked bar per caller by the next, and both are pictures somebody has to look at.
        preview._access.SetAligned(false);
        Pump();
        using (var stacked = preview._access.Grab()) stacked.Save(Path.Combine(directory, "chart-pie-stacked.png"));
        preview._access.SetAligned(true);
        preview.Hide();
        if (language != null) I18n.SetLanguage(original);
        Console.WriteLine($"Rendered {preview._cards.Count} chart cards and the pie to {directory}.");
        return 0;
    }

    private void PlaceAccess()
    {
        _access.SetSlices(new List<AccessSlice>
        {
            new("memory-cache", "#2563eb", 48213, 0), new("cache", "#0891b2", 12044, 0),
            new("overlay", "#059669", 3120, 908), new("svn", "#f59e0b", 1402, 0)
        },
        // Who asked over the same window: two tools doing the reading, one of them writing too, a
        // background service that only shows up because it read something, a folded tail, and last
        // the service's own read-ahead, which no process asked for. The pieces are arranged so that
        // each layer's column adds up to that layer's slice of the circle above — which is what the
        // card is for, and what a fixture has to get right or the picture lies.
        new List<AccessRow>
        {
            Row("Code.exe", ("memory-cache", 30000, 0), ("cache", 3904, 0), ("overlay", 2600, 620)),
            Row("Explorer.EXE", ("memory-cache", 11000, 0), ("cache", 4200, 0), ("overlay", 300, 288)),
            Row("python.exe", ("memory-cache", 4300, 0), ("cache", 1802, 0)),
            Row("MsMpEng.exe", ("memory-cache", 1500, 0), ("cache", 700, 0), ("overlay", 220, 0)),
            Row(I18n.T("stats.access.other"), ("cache", 1438, 0), ("memory-cache", 413, 0)),
            Row(I18n.T("stats.process.unattributed"), ("svn", 1402, 0), ("memory-cache", 1000, 0))
        });
    }

    /// <summary>One caller's row from the counts a reader would see: the pieces the bar is made of,
    /// in the colours the layer list gives them.</summary>
    private static AccessRow Row(string name, params (string Layer, long Reads, long Writes)[] parts) =>
        new(name, parts.Select(part => new AccessPart(part.Layer, Colour(part.Layer), part.Reads, part.Writes)).ToList());

    /// <summary>The colour a layer is drawn in, from the same list the circle and the trend cards are
    /// drawn from.</summary>
    private static string Colour(string layer)
    {
        foreach (var (name, color) in Layers) if (name == layer) return color;
        return "#64748B";
    }

    /// <summary>One window of readings, one per second: what the layers served, what they hold and
    /// what the process moved. Each card is given the magnitude its unit is read at, so a byte card
    /// is scaled in thousands and a rate card in single figures — the same shape at the sizes the
    /// two are read at.</summary>
    private void Feed(DateTime end)
    {
        for (int index = 0; index < _cards.Count; index++)
        {
            var card = _cards[index];
            card.SpanSeconds = _span;
            card.GapSeconds = 2.5;
            card.SetSeries(Fixture(Cards[index].TitleKey, Cards[index].Unit, end), end);
        }
        // A recording's edges are drawn by the chart as annotation rather than as data, so a picture
        // of a card is the only place they can be looked at.
        _cards[0].SetMarkers(new[]
        {
            new PerformanceChart.ChartMarker(end.AddSeconds(-_span * 0.55), "record.marker.start", "#dc2626"),
            new PerformanceChart.ChartMarker(end.AddSeconds(-_span * 0.12), "record.marker.stop", "#64748b")
        });
    }

    private List<ChartSeries> Fixture(string titleKey, string unit, DateTime end)
    {
        // The process's own bytes are two directions of one thing, drawn from the zero line rather
        // than stacked: the shape a reader checks a rate against.
        if (titleKey == "stats.process.moved")
            return new List<ChartSeries>
            {
                new(I18n.T("stats.process.moved.read"), "#0f766e", Wave(end, 900 * 1024, 0)),
                new(I18n.T("stats.process.moved.write"), "#b45309", Wave(end, 300 * 1024, 1.9)) { Stacked = false }
            };
        // What one layer's reading is worth on this card: the byte cards are read in thousands, a
        // latency in milliseconds and the rates in single figures. The two directions are given
        // different magnitudes so a picture of a read card beside a write card shows which is
        // which, the way a busy instance's two accounts differ.
        bool written = titleKey.StartsWith("stats.write", StringComparison.Ordinal);
        double peak = unit switch { "B" => 4096, "ms" => 1.2, "/s" => 2.6, _ => 6 };
        if (written) peak *= 0.45;
        var series = new List<ChartSeries>();
        for (int layer = 0; layer < Layers.Length; layer++)
        {
            var (name, color) = Layers[layer];
            double amplitude = layer == Layers.Length - 1 ? 0 : peak * (1 - layer * 0.18);
            series.Add(new ChartSeries(name, color, Wave(end, amplitude, layer * 0.9 + (written ? 1.1 : 0))));
        }
        // The read-ahead layer's guesses that were evicted unread are drawn below the line, hatched
        // in that layer's own colour. The service sends this label; the fixture spells it out.
        if (titleKey == "stats.reads")
            series.Add(new ChartSeries("wasted · predict", "#f59e0b", Wave(end, -peak * 0.7, 2.4)) { Hatched = true });
        // The part of the process's memory no layer claims: the card's top edge is the process's
        // own reading, and this band is what the layer bands below it leave over.
        if (titleKey == "stats.layerHeld.memory")
            series.Insert(0, new ChartSeries(I18n.T("stats.memory.unattributed"), "#94A3B8",
                Wave(end, peak * 0.8, 1.4)));
        return series;
    }

    private List<(DateTime Time, double Value)> Wave(DateTime end, double amplitude, double phase)
    {
        var samples = new List<(DateTime, double)>();
        for (double back = _span; back >= 0; back -= 1)
        {
            double wave = Math.Sin(back / 7.0 + phase) * 0.5 + Math.Sin(back / 2.3 + phase) * 0.2;
            samples.Add((end.AddSeconds(-back), Math.Max(0, amplitude * (1 + wave * 0.6))));
        }
        return samples;
    }

    /// <summary>Lets Qt lay the widgets out and paint them: a chart's axes are its own layout's
    /// answer, and its legend's height is part of that answer.</summary>
    private static void Pump()
    {
        for (int round = 0; round < 12; round++)
        {
            QCoreApplication.ProcessEvents();
            System.Threading.Thread.Sleep(8);
        }
    }
}
