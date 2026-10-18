using System.Text;
using PolyMount.Core;

namespace PolyMount.Plugins.Svn;

/// <summary>
/// How the SVN source finds credentials and what it accepts as one.
///
/// The order is the one Core fixes for every source: what the configuration says, then what the
/// user entered in this product, then whatever the Subversion client cached for this Windows user,
/// then anonymous. Only the third step is SVN's own; the rest comes from
/// <see cref="CredentialedSource"/>.
/// </summary>
internal sealed class SvnCredentialedSource(SourceConfig source) : CredentialedSource(source.Url, source)
{
    /// <inheritdoc/>
    protected override Credential? ResolveProtocolCredential() =>
        FromCache(new Uri(Source.Url, UriKind.Absolute)) is { } cached
            ? new Credential(cached.Username, cached.Password)
            : null;

    /// <summary>One request proves the account: the youngest revision, which is what a mount asks
    /// for first anyway.</summary>
    protected override void Verify(Credential credential)
    {
        using var client = new SvnHttpClient(Source.Url, Source.TrustServerCertFailures, credential.Username, credential.Secret);
        try { client.GetHeadRevision(); }
        catch (AuthRequiredException) { throw; }
        catch (SvnTransportException ex)
        {
            throw new InvalidOperationException("could not reach " + Origin + ": " + ex.Message, ex);
        }
    }

    /// <summary>
    /// The cached credential for a repository, matched by host and port. Several realms can share
    /// a host; a realm that names the repository's own path is preferred, and otherwise the most
    /// recently written file wins, which is the one a person would have just authenticated with.
    /// </summary>
    internal static CachedCredential? FromCache(Uri repository) => FromCache(repository, CacheDirectory(), CredentialStore.Unprotect);

    /// <summary>The same lookup over a given directory and decryptor, so the whole path — the file
    /// format, the base64 blob and the realm match — can be exercised without a real credential
    /// store.</summary>
    internal static CachedCredential? FromCache(Uri repository, string directory, Func<byte[], string?> unprotect)
    {
        if (!Directory.Exists(directory)) return null;

        CachedCredential? best = null;
        int bestScore = 0;
        DateTime bestWritten = DateTime.MinValue;

        string[] files;
        try { files = Directory.GetFiles(directory); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        foreach (string file in files)
        {
            Dictionary<string, byte[]>? values;
            try { values = Read(file); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if (values == null) continue;
            if (!values.TryGetValue("svn:realmstring", out var realmBytes)) continue;
            if (!values.TryGetValue("username", out var userBytes)) continue;
            if (!values.TryGetValue("password", out var passwordBytes)) continue;
            // Only the Windows store holds a password this process can open. A realm cached by a
            // build with a different store is passed over rather than reported: another entry for
            // the same host may still work.
            if (!values.TryGetValue("passtype", out var typeBytes)) continue;
            if (!string.Equals(Encoding.ASCII.GetString(typeBytes), "wincrypt", StringComparison.OrdinalIgnoreCase)) continue;

            string realm = Encoding.UTF8.GetString(realmBytes);
            if (!RealmMatches(realm, repository)) continue;

            byte[] blob;
            try { blob = Convert.FromBase64String(Encoding.ASCII.GetString(passwordBytes)); }
            catch (FormatException) { continue; }
            string? password = unprotect(blob);
            if (password == null) continue;

            int score = realm.Contains(repository.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            DateTime written;
            try { written = File.GetLastWriteTimeUtc(file); }
            catch (IOException) { written = DateTime.MinValue; }

            if (score > bestScore || (score == bestScore && written > bestWritten))
            {
                best = new CachedCredential(Encoding.UTF8.GetString(userBytes), password);
                bestScore = score;
                bestWritten = written;
            }
        }
        return best;
    }

    /// <summary>Whether a realm belongs to a repository. The cache key is the whole
    /// <c>&lt;scheme://host:port&gt;</c> prefix the client builds, followed by the server's realm
    /// text: the prefix is parsed and compared field by field, never searched for as a substring,
    /// because a host name that merely appears inside another one — or the same host over a
    /// different scheme — would otherwise be handed credentials meant for someone else.</summary>
    internal static bool RealmMatches(string realm, Uri repository)
    {
        if (!realm.StartsWith('<')) return false;
        int close = realm.IndexOf('>');
        if (close < 4) return false;
        if (!Uri.TryCreate(realm[1..close], UriKind.Absolute, out var origin)) return false;
        return string.Equals(origin.Scheme, repository.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(origin.Host, repository.Host, StringComparison.OrdinalIgnoreCase)
            && origin.Port == repository.Port;
    }

    /// <summary>The Subversion credential store for the current user.</summary>
    internal static string CacheDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Subversion", "auth", "svn.simple");

    /// <summary>
    /// Parses one cache file: repeated <c>K &lt;length&gt;\n&lt;key&gt;\nV &lt;length&gt;\n&lt;value&gt;\n</c>
    /// pairs, lengths in bytes, terminated by a line reading <c>END</c>. Returns null for anything
    /// that is not that shape — including a file without the terminator, which is what a file cut
    /// short looks like.
    /// </summary>
    internal static Dictionary<string, byte[]>? Read(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        string? key = null;
        bool terminated = false;
        int i = 0;
        while (i < data.Length)
        {
            int lineEnd = Array.IndexOf(data, (byte)'\n', i);
            if (lineEnd < 0) break;
            string line = Encoding.ASCII.GetString(data, i, lineEnd - i);
            i = lineEnd + 1;
            if (line.Length == 0) continue;
            if (line == "END") { terminated = true; break; }
            int space = line.IndexOf(' ');
            if (space != 1 || !int.TryParse(line.AsSpan(2), out int length) || length < 0) return null;
            if (i + length > data.Length) return null;
            var value = data.AsSpan(i, length).ToArray();
            i += length + 1;   // the value is followed by a newline
            if (line[0] == 'K') key = Encoding.UTF8.GetString(value);
            else if (line[0] == 'V' && key != null) values[key] = value;
        }
        return terminated ? values : null;
    }
}

/// <summary>A credential read from the Subversion client's own store.</summary>
internal sealed record CachedCredential(string Username, string Password);

/// <summary>What the product calls to sign an SVN repository in: check the account, then keep it.</summary>
internal sealed class SvnCredentialHandler : ICredentialHandler
{
    public string OriginOf(SourceConfig source) =>
        Uri.TryCreate(source.Url, UriKind.Absolute, out var uri)
            ? CredentialStore.OriginOf(uri)
            : source.Url;

    public void SignIn(SourceConfig source, string username, string password) =>
        new SvnCredentialedSource(source).SignIn(username, password);
}
