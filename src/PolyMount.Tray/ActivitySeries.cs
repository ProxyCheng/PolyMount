namespace PolyMount.Tray;

/// <summary>One instance's activity as the workspace holds it: a request rate per second, the last
/// minute of it, and nothing older.
///
/// A minute is as much as a curve two dozen pixels tall can show — one point a second across the
/// ninety-odd pixels a list row gives it — and it is as much history as a reader who is only asking
/// "is this one being used" can make sense of. What it is not is a sample of the instance's own
/// counters: those are asked for once a second and only while this window is showing, so a series
/// describes the last minute of watching rather than the last minute of the instance's life. That
/// is why the window holds this rather than drawing a line the service never measured.
///
/// Values are rates, so a sample is comparable to the one before it whatever the service took to
/// answer; the ring is a plain array because it is written once a second from the window's own
/// thread and read by the cell that paints it.</summary>
internal sealed class ActivitySeries
{
    /// <summary>A minute at one point a second.</summary>
    internal const int Capacity = 60;

    private readonly double[] _values = new double[Capacity];
    private int _count;
    private int _next;

    /// <summary>How many readings are held. A window that has just opened holds none, and a curve
    /// drawn from none of them would be a claim about a minute nobody watched.</summary>
    internal int Count => _count;

    /// <summary>The newest reading, or zero while nothing has been recorded. Zero is also a reading:
    /// a mounted instance with nothing asking it anything is honestly at zero, and the cell draws
    /// that as a flat line rather than as an absence.</summary>
    internal double Latest => _count == 0 ? 0 : this[_count - 1];

    /// <summary>Whether the instance answered the last sample at all. A worker that did not answer
    /// has no reading, which is not a reading of zero: the curve stops and goes quiet instead of
    /// dipping, because a dip is a thing the instance did and this is a thing that happened to the
    /// question.</summary>
    internal bool Stale { get; set; }

    /// <summary>Oldest first, so the cell can walk it from the left edge of the curve.</summary>
    internal double this[int index] => _values[((_next - _count + index) % Capacity + Capacity) % Capacity];

    internal void Add(double value)
    {
        _values[_next] = value;
        _next = (_next + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    internal void Clear()
    {
        _count = 0;
        _next = 0;
        Stale = false;
    }

    /// <summary>The highest reading held. The curve is scaled to this rather than to a fixed top:
    /// an instance doing four requests a second and one doing four thousand both have a shape, and
    /// a shared axis would show the first as a flat line.</summary>
    internal double Peak()
    {
        double peak = 0;
        for (int i = 0; i < _count; i++) peak = Math.Max(peak, this[i]);
        return peak;
    }

    internal double Average()
    {
        if (_count == 0) return 0;
        double sum = 0;
        for (int i = 0; i < _count; i++) sum += this[i];
        return sum / _count;
    }
}
