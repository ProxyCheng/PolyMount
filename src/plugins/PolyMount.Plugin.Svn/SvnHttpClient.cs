using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using PolyMount.Core;

namespace PolyMount.Plugins.Svn;

/// <summary>
/// Repository access over the Subversion HTTP/WebDAV protocol.
/// <para>
/// Invoking the svn command line client spawns one process per request, and process
/// startup dominates the round trip: roughly 136 ms of the ~144 ms a metadata request
/// takes on Windows, against ~8 ms of actual network time. Serving the same requests
/// from a pooled <see cref="HttpClient"/> keeps connections alive and removes that cost
/// from every listing and every file read.
/// </para>
/// <para>
/// Revisions are pinned through repository-revision URLs (<c>!svn/rvr/&lt;rev&gt;</c>).
/// The plain resource URL and the <c>Label</c> request header both serve the youngest
/// revision instead of the requested one, so neither can be used for a pinned mount.
/// </para>
/// </summary>
internal sealed class SvnHttpClient : ISvnBackend
{
    /// <summary>Requests this transport keeps open at once. It is also what the layer counts as
    /// the source's capacity when it shares that capacity out between classes, so the two cannot
    /// drift apart.</summary>
    internal const int MaxConnections = 16;
    private const string ListingBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><propfind xmlns=\"DAV:\"><prop>" +
        "<resourcetype/><getcontentlength/><getlastmodified/><version-name/></prop></propfind>";
    private const string VersionNameBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><propfind xmlns=\"DAV:\"><prop><version-name/></prop></propfind>";
    private const string ConfigurationBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><propfind xmlns=\"DAV:\"><prop><version-controlled-configuration/></prop></propfind>";

    private static readonly XNamespace Dav = "DAV:";
    private readonly Uri _base;
    private readonly HttpClient _client;
    private readonly object _gate = new();
    private readonly bool _hasCredentials;
    private string? _repositoryRoot;
    private string _pathPrefix = string.Empty;

    public SvnHttpClient(string url, string trustFailures, string? username, string? password)
    {
        _base = new Uri(url.TrimEnd('/'), UriKind.Absolute);
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = MaxConnections,
            ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) =>
                TrustsCertificateFailures(trustFailures, certificate, chain, errors)
        };
        // No account means anonymous, which is enough for a repository that does not ask. The
        // caller resolves the cached Subversion credentials before constructing this; a challenge
        // that still arrives is reported as such rather than retried.
        if (!string.IsNullOrEmpty(username))
        {
            _hasCredentials = true;
            handler.Credentials = new NetworkCredential(username, password ?? string.Empty);
            handler.PreAuthenticate = true;
        }
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
    }

    public long GetHeadRevision(RequestDiagnostics? diagnostics = null)
    {
        var (root, _) = Repository();
        using var request = RequestDiagnostics.Begin("SVN.info", root, diagnostics);
        var document = PropFind(root, "0", VersionNameBody);
        var value = document.Descendants(Dav + "version-name").FirstOrDefault()?.Value;
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision))
            throw new SvnTransportException($"repository '{root}' did not report a youngest revision");
        return revision;
    }

    public IReadOnlyList<SvnEntry> List(long rev, string rel, RequestDiagnostics? diagnostics = null)
    {
        var (root, prefix) = Repository();
        var url = ResourceUrl(root, prefix, rev, rel);
        using var request = RequestDiagnostics.Begin("SVN.list", url, diagnostics);
        var document = PropFind(url, "1", ListingBody);
        // A directory never contains a child with its own name, so the collection that
        // WebDAV returns first can be dropped by name whatever form its href takes.
        var self = rel.Length > 0 ? VfsPath.Name(rel) : VfsPath.Name(prefix);
        var entries = ParseListing(document, PathOf(url), self);
        request?.Stage($"entries={entries.Count}");
        return entries;
    }

    public SvnEntry? Info(long rev, string rel, RequestDiagnostics? diagnostics = null)
    {
        var (root, prefix) = Repository();
        var url = ResourceUrl(root, prefix, rev, rel);
        using var request = RequestDiagnostics.Begin("SVN.info", url, diagnostics);
        // Depth 0 describes the resource itself. The same body as a listing: WebDAV returns the
        // requested properties either way, and only the number of responses differs.
        var found = ParseEntry(PropFind(url, "0", ListingBody), PathOf(url));
        request?.Stage(found == null ? "missing" : $"kind={(found.IsDir ? "dir" : "file")}");
        return found;
    }

    public void CatToFile(long rev, string rel, string dest, RequestDiagnostics? diagnostics = null)
    {
        var (root, prefix) = Repository();
        var url = ResourceUrl(root, prefix, rev, rel);
        using var request = RequestDiagnostics.Begin("SVN.cat", url, diagnostics);
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = Send(message, url, rel);
        request?.Stage($"status={(int)response.StatusCode}");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var part = dest + ".part-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var source = response.Content.ReadAsStream())
            using (var file = File.Create(part))
                source.CopyTo(file);
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(part, dest);
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    public void Dispose() => _client.Dispose();

    // ---- protocol ----

    private XDocument PropFind(string url, string depth, string body)
    {
        using var message = new HttpRequestMessage(new HttpMethod("PROPFIND"), url);
        message.Headers.TryAddWithoutValidation("Depth", depth);
        message.Content = new StringContent(body, Encoding.UTF8, "text/xml");
        using var response = Send(message, url, string.Empty);
        using var stream = response.Content.ReadAsStream();
        try
        {
            return XDocument.Load(stream);
        }
        catch (XmlException ex)
        {
            throw new SvnTransportException($"'{url}' did not return a WebDAV document", ex);
        }
    }

    /// <summary>Issues a request, mapping transport and existence failures onto backend exceptions.</summary>
    private HttpResponseMessage Send(HttpRequestMessage message, string url, string rel)
    {
        HttpResponseMessage response;
        try
        {
            response = _client.Send(message, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (HttpRequestException ex)
        {
            throw new SvnTransportException($"request to '{url}' failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ex.CancellationToken.IsCancellationRequested)
        {
            throw new SvnTransportException($"request to '{url}' timed out", ex);
        }

        if (response.IsSuccessStatusCode) return response;
        var statusCode = response.StatusCode;
        var status = (int)statusCode;
        response.Dispose();
        if (statusCode == HttpStatusCode.NotFound) throw new SvnNotFoundException($"'{rel}' does not exist at this revision: {url}");
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SvnTransportException(AuthenticationFailure(_hasCredentials, url, _base.Host));
        // Everything else is the server or a proxy refusing the request itself; this transport has
        // no second way to ask.
        throw new SvnTransportException($"'{url}' returned HTTP {status}");
    }

    /// <summary>What to say when the repository refused the request. A caller with no credentials
    /// is told both ways out; one whose credentials were rejected is told that, because sending
    /// them the same advice would be wrong. Internal so the wording is checked by a test.</summary>
    internal static string AuthenticationFailure(bool hasCredentials, string url, string host) => hasCredentials
        ? $"the repository at '{host}' rejected the credentials it was given"
        : $"'{url}' requires authentication and no credentials were available. Run the service as your own Windows account so it can read the cached Subversion credentials, or fill in the username and password on this layer.";

    /// <summary>Turns a Depth:1 multistatus listing into child entries.</summary>
    internal static List<SvnEntry> ParseListing(XDocument document, string requestPath, string selfName)
    {
        var entries = new List<SvnEntry>();
        foreach (var element in document.Descendants(Dav + "response"))
        {
            var href = element.Element(Dav + "href")?.Value;
            if (string.IsNullOrEmpty(href)) continue;
            var path = HrefPath(href);
            var name = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
            if (name.Length == 0) continue;
            if (string.Equals(path, requestPath, StringComparison.Ordinal) ||
                (selfName.Length > 0 && string.Equals(name, selfName, StringComparison.OrdinalIgnoreCase))) continue;

            ReadProps(element, out bool isDir, out long size, out var time, out string contentVersion);
            entries.Add(new SvnEntry { Name = name, IsDir = isDir, Size = size, TimeUtc = time, ContentVersion = contentVersion });
        }
        // svn list reports children in name order; keep that so callers see the same sequence.
        entries.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return entries;
    }

    /// <summary>Turns a Depth:0 multistatus response into the one entry it describes, or null
    /// when the resource is not there. <see cref="ParseListing"/> cannot answer this: it drops
    /// the response that names the request path, which is the only one a Depth:0 document has.</summary>
    internal static SvnEntry? ParseEntry(XDocument document, string requestPath)
    {
        foreach (var element in document.Descendants(Dav + "response"))
        {
            var href = element.Element(Dav + "href")?.Value;
            if (string.IsNullOrEmpty(href)) continue;
            var path = HrefPath(href);
            if (!string.Equals(path, requestPath, StringComparison.Ordinal)) continue;
            ReadProps(element, out bool isDir, out long size, out var time, out string contentVersion);
            return new SvnEntry
            {
                Name = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]),
                IsDir = isDir, Size = size, TimeUtc = time, ContentVersion = contentVersion
            };
        }
        return null;
    }

    /// <summary>Reads the properties of one response, from the propstat whose status is 200.</summary>
    private static void ReadProps(XElement response, out bool isDir, out long size, out DateTime time, out string contentVersion)
    {
        isDir = false;
        size = 0;
        time = DateTime.UnixEpoch;
        contentVersion = string.Empty;
        foreach (var propstat in response.Elements(Dav + "propstat"))
        {
            if (propstat.Element(Dav + "status")?.Value?.Contains(" 200 ", StringComparison.Ordinal) != true) continue;
            var prop = propstat.Element(Dav + "prop");
            if (prop == null) continue;
            isDir = prop.Element(Dav + "resourcetype")?.Element(Dav + "collection") != null;
            var length = prop.Element(Dav + "getcontentlength")?.Value;
            if (!string.IsNullOrEmpty(length)) long.TryParse(length, NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
            var modified = prop.Element(Dav + "getlastmodified")?.Value;
            if (!string.IsNullOrEmpty(modified) && DateTimeOffset.TryParse(modified, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                time = parsed.UtcDateTime;
            // The revision this entry last changed in, which is what a cache compares across
            // global revisions. On a !svn/rvr listing each child's version-name is its own
            // last-changed revision -- the same number libsvn_ra_serf reads for
            // SVN_PROP_ENTRY_COMMITTED_REV. Without it every entry is keyed by the repository's
            // generation and one new revision invalidates the whole tree. A value that is not a
            // number would poison the cache key, so it is only kept when it parses.
            var version = prop.Element(Dav + "version-name")?.Value?.Trim();
            contentVersion = long.TryParse(version, out _) ? version! : string.Empty;
        }
    }

    /// <summary>Resolves the repository root and the mount's path inside it, once per instance.</summary>
    private (string Root, string Prefix) Repository()
    {
        if (_repositoryRoot is { } cached) return (cached, _pathPrefix);
        lock (_gate)
        {
            if (_repositoryRoot is { } current) return (current, _pathPrefix);
            var document = PropFind(_base.AbsoluteUri, "0", ConfigurationBody);
            var href = document.Descendants(Dav + "version-controlled-configuration")
                .Elements(Dav + "href").FirstOrDefault()?.Value;
            if (string.IsNullOrEmpty(href))
                throw new SvnTransportException($"'{_base}' did not advertise a version-controlled-configuration");
            var absolute = new Uri(_base, href).AbsoluteUri;
            var marker = absolute.IndexOf("/!svn/", StringComparison.Ordinal);
            if (marker <= 0) throw new SvnTransportException($"unexpected version-controlled-configuration '{href}'");

            var root = absolute[..marker];
            var rootPath = new Uri(root).AbsolutePath.TrimEnd('/');
            var basePath = _base.AbsolutePath.TrimEnd('/');
            if (!basePath.StartsWith(rootPath, StringComparison.Ordinal))
                throw new SvnTransportException($"'{_base}' is outside repository root '{root}'");
            _pathPrefix = basePath[rootPath.Length..].Trim('/');
            _repositoryRoot = root;
            return (root, _pathPrefix);
        }
    }

    /// <summary>Builds a revision-pinned URL for a repository-relative path.</summary>
    internal static string ResourceUrl(string repositoryRoot, string pathPrefix, long revision, string rel)
    {
        var url = new StringBuilder(repositoryRoot).Append("/!svn/rvr/")
            .Append(revision.ToString(CultureInfo.InvariantCulture));
        if (pathPrefix.Length > 0) url.Append('/').Append(EscapePath(pathPrefix));
        if (rel.Length > 0) url.Append('/').Append(EscapePath(rel));
        return url.ToString();
    }

    internal static string EscapePath(string rel) => string.Join('/',
        rel.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    private static string PathOf(string url) => new Uri(url).AbsolutePath.TrimEnd('/');

    /// <summary>Extracts the escaped path from a multistatus href, which may be a full URL or a server path.</summary>
    private static string HrefPath(string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute) && !absolute.IsFile) return absolute.AbsolutePath.TrimEnd('/');
        return href.Split('?', 2)[0].TrimEnd('/');
    }

    /// <summary>Mirrors <c>svn --trust-server-cert-failures</c>.</summary>
    internal static bool TrustsCertificateFailures(string trustFailures, X509Certificate2? certificate,
        X509Chain? chain, SslPolicyErrors errors)
    {
        var allowed = new HashSet<string>(
            (trustFailures ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        if (allowed.Contains("other")) return true;
        if (errors == SslPolicyErrors.None) return true;
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) return false;
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) && !allowed.Contains("cn-mismatch")) return false;
        if (!errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors)) return true;
        foreach (var status in chain?.ChainStatus ?? [])
            if (!TrustsChainStatus(status.Status, certificate, allowed)) return false;
        return true;
    }

    private static bool TrustsChainStatus(X509ChainStatusFlags flags, X509Certificate2? certificate, HashSet<string> allowed)
    {
        if (flags == X509ChainStatusFlags.NoError) return true;
        var trusted = true;
        if (flags.HasFlag(X509ChainStatusFlags.UntrustedRoot) || flags.HasFlag(X509ChainStatusFlags.PartialChain))
            trusted &= allowed.Contains("unknown-ca");
        if (flags.HasFlag(X509ChainStatusFlags.NotTimeValid) || flags.HasFlag(X509ChainStatusFlags.NotTimeNested))
        {
            // X509ChainStatusFlags does not separate the two directions; the certificate
            // dates tell them apart when one of the two is trusted but not the other.
            var now = DateTime.UtcNow;
            var expired = certificate == null || certificate.NotAfter.ToUniversalTime() <= now;
            trusted &= allowed.Contains(expired ? "expired" : "not-yet-valid");
        }
        return trusted;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
