using PolyMount.Core;

namespace PolyMount.Plugins.Svn;

internal sealed class SvnEntry
{
    public string Name = string.Empty;
    public bool IsDir;
    public long Size;
    public DateTime TimeUtc;
}

/// <summary>The requested repository path does not exist at the requested revision.</summary>
internal sealed class SvnNotFoundException : Exception
{
    public SvnNotFoundException(string message) : base(message) { }
}

/// <summary>
/// The backend could not transport the request (authentication, protocol or network).
/// Callers may retry the operation through a different backend.
/// </summary>
internal sealed class SvnTransportException : Exception
{
    public SvnTransportException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Repository access used by <see cref="SvnLayer"/>, implemented over the Subversion
/// HTTP/WebDAV protocol or by invoking the svn command line client.
/// <para>
/// Every operation takes the diagnostics to trace into. A request served on the caller's
/// thread has an ambient scope and can pass none; read-ahead runs on a worker thread with
/// no ambient scope, so it passes the one it captured when the work was queued.
/// </para>
/// </summary>
internal interface ISvnBackend : IDisposable
{
    /// <summary>Youngest revision of the repository, equivalent to <c>svn info --show-item revision</c>.</summary>
    long GetHeadRevision(RequestDiagnostics? diagnostics = null);

    /// <summary>Immediate children of <paramref name="rel"/> at <paramref name="rev"/>.</summary>
    IReadOnlyList<SvnEntry> List(long rev, string rel, RequestDiagnostics? diagnostics = null);

    /// <summary>
    /// Metadata of <paramref name="rel"/> itself at <paramref name="rev"/>, without listing its
    /// parent, or null when the path does not exist at that revision. A header is an object's
    /// own data, so a caller that wants one asks for the path; <see cref="List"/> answers a
    /// whole directory and is what enumeration uses.
    /// </summary>
    SvnEntry? Info(long rev, string rel, RequestDiagnostics? diagnostics = null);

    /// <summary>Writes the content of <paramref name="rel"/> at <paramref name="rev"/> to <paramref name="dest"/>.</summary>
    void CatToFile(long rev, string rel, string dest, RequestDiagnostics? diagnostics = null);
}

/// <summary>
/// Serves requests from a preferred backend and permanently switches to a fallback
/// when the preferred one cannot transport a request. Servers without WebDAV support
/// and mounts that rely on the svn credential cache are handled without configuration.
/// </summary>
internal sealed class FallbackSvnBackend(ISvnBackend preferred, Func<ISvnBackend> fallback) : ISvnBackend
{
    private readonly object _gate = new();
    private ISvnBackend _current = preferred;
    private ISvnBackend? _fallback;

    /// <summary>The backend currently serving requests; exposed so tests can assert switching.</summary>
    internal ISvnBackend Current => _current;

    private T Run<T>(Func<ISvnBackend, T> operation)
    {
        var backend = _current;
        try
        {
            return operation(backend);
        }
        catch (SvnTransportException) when (!ReferenceEquals(backend, _fallback))
        {
            lock (_gate)
            {
                if (ReferenceEquals(_current, backend))
                {
                    _fallback ??= fallback();
                    _current = _fallback;
                    backend.Dispose();
                }
            }
            return operation(_current);
        }
    }

    private void Run(Action<ISvnBackend> operation) =>
        Run<object?>(backend => { operation(backend); return null; });

    public long GetHeadRevision(RequestDiagnostics? diagnostics = null) => Run(backend => backend.GetHeadRevision(diagnostics));
    public IReadOnlyList<SvnEntry> List(long rev, string rel, RequestDiagnostics? diagnostics = null) =>
        Run(backend => backend.List(rev, rel, diagnostics));
    public SvnEntry? Info(long rev, string rel, RequestDiagnostics? diagnostics = null) =>
        Run(backend => backend.Info(rev, rel, diagnostics));
    public void CatToFile(long rev, string rel, string dest, RequestDiagnostics? diagnostics = null) =>
        Run(backend => backend.CatToFile(rev, rel, dest, diagnostics));

    // Once a switch happens the replaced backend is disposed immediately, so whatever
    // _current points at is the only live instance left to release.
    public void Dispose()
    {
        lock (_gate) _current.Dispose();
    }
}
