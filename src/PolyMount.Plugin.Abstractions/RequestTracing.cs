namespace PolyMount.Plugins;

/// <summary>A scope the host writes into its request log, as a layer sees it.
///
/// A source spends real time on the network, and that time is most of what a reader complains
/// about; opening a scope around the round trip is what puts it in the log beside the lookups
/// that asked for it. The host owns the log — where it goes, what gets sampled, how the parent
/// chain is threaded — and a layer only says "this operation took this long".
/// </summary>
public interface IRequestTracing
{
    /// <summary>Opens a scope for one operation. Disposing it ends the scope, so the ordinary
    /// spelling is <c>using var scope = tracing.Begin("SVN.list", url);</c>. Null when this
    /// tracing is not recording anything — <c>using</c> accepts it either way.</summary>
    IRequestScope? Begin(string operation, string? path);

    /// <summary>The caller this work answers to, or 0 when nobody asked for it — read-ahead and
    /// warm-up run for the host itself. A layer includes it when it names a process of its
    /// own.</summary>
    int ProcessId { get; }
}

/// <summary>One open operation, as the log will describe it when it ends.
///
/// Besides its lifetime, what a caller can say about a scope is which stage it reached — the name
/// written into the line when the scope closes, which is how a failure is told apart from a slow
/// success without the log having to know what the operation was.</summary>
public interface IRequestScope : IDisposable
{
    /// <summary>Names what the operation is doing now: "listing", "downloading", an exception's
    /// name. The last one set is what the line carries.</summary>
    void Stage(string stage);
}

/// <summary>The host's tracing, reached the way a layer does: through the chain it was given, or
/// through the ambient scope of the request being handled.
/// </summary>
public static class RequestTracing
{
    /// <summary>Where the ambient scope is read from. The host sets this once, at start-up; the
    /// indirection is what keeps the ambient scope in the host's hands (it already keeps one per
    /// request) instead of copying it into a second slot on the request path.</summary>
    public static Func<IRequestTracing?>? AmbientSource { get; set; }

    /// <summary>The scope of the request being handled on this thread, or null outside one.</summary>
    public static IRequestTracing? Ambient => AmbientSource?.Invoke();

    /// <summary>Opens a scope under <paramref name="owner"/>, or under the ambient one, or none at
    /// all when neither exists — so a caller never has to ask whether it is being traced.</summary>
    public static IRequestScope? Begin(string operation, string? path, IRequestTracing? owner = null) =>
        (owner ?? Ambient)?.Begin(operation, path);

    /// <summary>The process a stream id names. Stream ids are made by the file-system boundary as
    /// <c>pid &lt;&lt; 32 | local</c>, so this is a pure function of the id — a layer that kept one
    /// can say who it belonged to without asking the host.</summary>
    public static int ProcessOfStream(long streamId) => (int)(streamId >> 32);
}
