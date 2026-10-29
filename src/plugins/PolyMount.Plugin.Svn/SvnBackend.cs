using PolyMount.Plugins;

namespace PolyMount.Plugins.Svn;

internal sealed class SvnEntry
{
    public string Name = string.Empty;
    public bool IsDir;
    public long Size;
    public DateTime TimeUtc;
    /// <summary>The revision this entry last changed in, when the transport reports it. It is
    /// the revision whose content the path has, which is what a cache can compare across
    /// global revisions; empty means the transport did not say, and callers fall back to the
    /// generation. No layer above the plugin interprets it.</summary>
    public string ContentVersion = string.Empty;
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
/// Every operation takes the tracing to trace into. A request served on the caller's
/// thread has an ambient scope and can pass none; read-ahead runs on a worker thread with
/// no ambient scope, so it passes the one it captured when the work was queued.
/// </para>
/// </summary>
internal interface ISvnBackend : IDisposable
{
    /// <summary>Youngest revision of the repository, equivalent to <c>svn info --show-item revision</c>.</summary>
    long GetHeadRevision(IRequestTracing? tracing = null);

    /// <summary>Immediate children of <paramref name="rel"/> at <paramref name="rev"/>.</summary>
    IReadOnlyList<SvnEntry> List(long rev, string rel, IRequestTracing? tracing = null);

    /// <summary>
    /// Metadata of <paramref name="rel"/> itself at <paramref name="rev"/>, without listing its
    /// parent, or null when the path does not exist at that revision. A header is an object's
    /// own data, so a caller that wants one asks for the path; <see cref="List"/> answers a
    /// whole directory and is what enumeration uses.
    /// </summary>
    SvnEntry? Info(long rev, string rel, IRequestTracing? tracing = null);

    /// <summary>Writes the content of <paramref name="rel"/> at <paramref name="rev"/> to <paramref name="dest"/>.</summary>
    void CatToFile(long rev, string rel, string dest, IRequestTracing? tracing = null);
}
