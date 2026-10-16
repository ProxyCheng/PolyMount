namespace PolyMount.Tray;

/// <summary>The line a card draws between two observations.
///
/// A card's series are not lines: each layer is an area whose boundary follows its readings, and Qt
/// builds that boundary out of the points it is given, one straight segment each. A window sampled
/// once a second is a handful of points across a few hundred pixels, so what a reader sees between
/// two readings is a corner where the readings came from a curve. Smoothing it is not something the
/// chart can be asked for: an area's boundary is a polyline whatever series it is built from — a
/// spline series handed to an area series is only read for its points (Qt's AreaChartItem wraps
/// either one in a LineChartItem and takes its path from that item's points).
///
/// So the points between two observations are worked out here, and the curve through them is a
/// monotone cubic (Fritsch–Carlson). Monotone is the whole point of the choice: it passes through
/// every observation at exactly the value that was measured, and between two of them it never rises
/// above the taller or falls below the lower, so a peak stays where the peak was, the axis scaled to
/// it still holds it, and a reader comparing the picture with the table beside it is comparing the
/// same numbers. What changes is only where the line runs between them.</summary>
internal static class SmoothCurve
{
    /// <summary>How long a drawn segment may be, in pixels. Longer than this and the corner between
    /// two of them is what the eye reads as the curve; shorter and the card is spending points on a
    /// stretch of straight line no reader can tell from a straight line. The whole window is
    /// therefore drawn in at most a point every few pixels, whatever it holds.</summary>
    internal const double PixelsPerSegment = 4;

    /// <summary>The most points one stretch between two observations is drawn in. A pause that just
    /// fits inside the gap the page allows is wide on the axis — a whole second or two of a short
    /// span — and it is still a bridge between two readings rather than a shape of its own.</summary>
    internal const int MostPerSegment = 8;

    /// <summary>How many points the stretch between two observations is drawn in: one for every few
    /// pixels it covers, and one — the observations themselves, nothing inserted — where they are
    /// already closer together than that.</summary>
    internal static int Subdivisions(double pixels) =>
        Math.Clamp((int)Math.Ceiling(pixels / PixelsPerSegment), 1, MostPerSegment);

    /// <summary>The values of the curve through <paramref name="nodes"/> at <paramref name="xs"/>.
    /// Both are positions on one axis and both increase; the queries are the grid a series is drawn
    /// on, which is the nodes themselves with whatever was inserted between them.
    ///
    /// A query that lands on a node comes back as that node's own value: the cubic's two ends are
    /// its two nodes, so this is exact rather than nearly so.</summary>
    internal static double[] Through(double[] nodes, double[] values, double[] xs)
    {
        var drawn = new double[xs.Length];
        if (nodes.Length == 0) return drawn;
        if (nodes.Length == 1)
        {
            for (int i = 0; i < xs.Length; i++) drawn[i] = values[0];
            return drawn;
        }
        var tangents = Tangents(nodes, values);
        int segment = 0;
        for (int i = 0; i < xs.Length; i++)
        {
            while (segment < nodes.Length - 2 && xs[i] > nodes[segment + 1]) segment++;
            drawn[i] = Hermite(nodes[segment], values[segment], tangents[segment],
                nodes[segment + 1], values[segment + 1], tangents[segment + 1], xs[i]);
        }
        return drawn;
    }

    /// <summary>The slope the curve leaves each node at: the secants around it, in the weighted
    /// harmonic mean a non-uniform grid calls for, and then Fritsch–Carlson's second stage, which
    /// pulls both ends of a segment back wherever they are steep enough between them to swing the
    /// cubic past one of them. That second stage is what makes the curve monotone, and being
    /// monotone is what makes it pass between the readings rather than around them.</summary>
    private static double[] Tangents(double[] nodes, double[] values)
    {
        int last = nodes.Length - 1;
        var secants = new double[last];
        for (int i = 0; i < last; i++)
        {
            double width = nodes[i + 1] - nodes[i];
            secants[i] = width > 0 ? (values[i + 1] - values[i]) / width : 0;
        }
        var tangents = new double[nodes.Length];
        tangents[0] = secants[0];
        tangents[last] = secants[last - 1];
        for (int i = 1; i < last; i++)
        {
            double before = secants[i - 1], after = secants[i];
            // A node the readings turn around at is a node the curve turns around at: anything else
            // is a slope that carries the line past the reading before it can come back.
            if (before * after <= 0) continue;
            double smaller = nodes[i] - nodes[i - 1], larger = nodes[i + 1] - nodes[i];
            double beforeWeight = 2 * larger + smaller, afterWeight = larger + 2 * smaller;
            tangents[i] = (beforeWeight + afterWeight) / (beforeWeight / before + afterWeight / after);
        }
        for (int i = 0; i < last; i++)
        {
            double secant = secants[i];
            if (secant == 0)
            {
                tangents[i] = 0;
                tangents[i + 1] = 0;
                continue;
            }
            double leaves = tangents[i] / secant, arrives = tangents[i + 1] / secant;
            if (leaves < 0) { tangents[i] = leaves = 0; }
            if (arrives < 0) { tangents[i + 1] = arrives = 0; }
            double steep = leaves * leaves + arrives * arrives;
            if (steep <= 9) continue;
            double pulled = 3 / Math.Sqrt(steep);
            tangents[i] = pulled * leaves * secant;
            tangents[i + 1] = pulled * arrives * secant;
        }
        return tangents;
    }

    /// <summary>The cubic between two nodes with their slopes at both ends, read at
    /// <paramref name="x"/>: the standard Hermite basis, which is one at its own end and nought at
    /// the other, whatever the slopes are.</summary>
    private static double Hermite(double x0, double y0, double m0,
        double x1, double y1, double m1, double x)
    {
        double width = x1 - x0;
        double t = width > 0 ? Math.Clamp((x - x0) / width, 0, 1) : 0;
        double square = t * t, cube = square * t;
        return (2 * cube - 3 * square + 1) * y0
            + (cube - 2 * square + t) * width * m0
            + (-2 * cube + 3 * square) * y1
            + (cube - square) * width * m1;
    }
}
