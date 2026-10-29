namespace PolyMount.Plugins;

/// <summary>
/// The priority the calling thread's reads belong to, and the classes this codebase gives names
/// to. A class is a number, low is urgent: a source that serves several of them keeps a share of
/// itself for each, so a class being busy is never the reason a reader waits — see
/// <see cref="ReadLaneGate"/>.
/// <para>
/// Marked at the few boundaries that know what a read is for — the resolver entering a
/// speculative load, the predictor's preload task, the listing warm-up — rather than carried as
/// a parameter through every signature in between, which is the same reasoning that puts the
/// tracing instance here. Nothing ever marks the reader: <see cref="Demand"/> is what work is
/// when nobody said otherwise, so a boundary that forgets to mark read-ahead costs accuracy in
/// the counters and never costs the reader its priority.
/// </para>
/// <para>
/// The value does not flow into a task started with the execution context suppressed, which is
/// how the predictor and the read-ahead workers are started on purpose. A boundary that hands
/// work to a thread of its own therefore has to mark the work there, not here.
/// </para>
/// </summary>
public static class ReadPriority
{
    /// <summary>A read somebody is waiting for.</summary>
    public const int Demand = 0;

    /// <summary>A read nobody has asked for yet: the whole purpose of it is to move a source
    /// request earlier than the reader would have made it.</summary>
    public const int Speculative = 1;

    private static readonly AsyncLocal<int> Current = new();

    /// <summary>The class the work on this thread belongs to.</summary>
    public static int Ambient => Current.Value;

    /// <summary>Marks everything on this thread until disposal as belonging to a less urgent
    /// class. Null when the ambient is already at that class or below, so a boundary nested in
    /// another costs nothing and the outer one still restores what it found.</summary>
    public static IDisposable? Enter(int priority)
    {
        int previous = Current.Value;
        if (previous >= priority) return null;
        Current.Value = priority;
        return new Scope(previous);
    }

    private sealed class Scope(int previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
