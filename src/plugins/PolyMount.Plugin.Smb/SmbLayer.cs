using System.Globalization;
using System.IO.Enumeration;
using PolyMount.Plugins;

namespace PolyMount.Plugins.Smb;

/// <summary>
/// A read-only data source backed by a folder on another machine.
///
/// It reads through the Windows redirector — the same client a mapped drive uses — so the far
/// side needs no agent: a share and an account that may read it are the whole of the setup. What
/// this layer adds over pointing the <c>dir</c> source at the same UNC path is everything that
/// makes a network folder different from a local one: an account the service can present, a
/// generation that costs one round trip per interval instead of one per lookup, and a content
/// version that lets a cache keep answering after the generation has moved.
///
/// Writes are not supported. They land in the overlay above, which is where every read-only
/// source in this product puts them; a pass-through writer would have no way to coordinate with
/// the other clients of the same share.
/// </summary>
public sealed class SmbLayer : ILayer, IDisposable
{
    public const string TypeName = "smb";

    private readonly SmbPath _path;
    private readonly SmbConnection _connection;
    private readonly long _generationTtlMs;
    private readonly object _generationGate = new();
    private string _generation = string.Empty;
    private long _generationReadAt;

    public SmbLayer(SmbPath path, SmbOptions options, string username = "", string password = "")
    {
        _path = path;
        _generationTtlMs = Math.Max(1, options.GenerationTtlSeconds) * 1000;
        _connection = new SmbConnection(path.ShareRoot, username, password);
        try
        {
            _connection.Ensure();
            // Fail when the instance is built, not on the first request: a mount that silently
            // maps an unreachable share looks like an empty folder, and the first tool to read it
            // concludes everything was deleted. DirectoryLayer does the same for a local path.
            File.GetAttributes(path.Root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _connection.Dispose();
            throw new IOException("the remote folder '" + path.Root + "' cannot be read: " + ex.Message, ex);
        }
        ReadGeneration();
    }

    /// <summary>Builds the layer a configured source describes, resolving its credential once —
    /// the session holds the account for the life of the process, so no request reads the
    /// credential store, and a credential that was deleted is reported rather than replaced by
    /// whatever the process happens to be running as.</summary>
    public static SmbLayer Create(SourceConfig config)
    {
        var options = SmbOptions.From(config.Parameters);
        (string username, string password) = Account(config);
        return new SmbLayer(SmbPath.Parse(config.Path), options, username, password);
    }

    private static (string Username, string Password) Account(SourceConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.CredentialId))
        {
            var credential = CredentialStore.FindById(config.CredentialId)
                ?? throw new InvalidOperationException("the credential this layer names no longer exists; choose another in the layer's settings");
            return AccountOf(credential);
        }
        if (!string.IsNullOrWhiteSpace(config.Username))
            return (config.Username, config.Password ?? string.Empty);
        return (string.Empty, string.Empty);
    }

    /// <summary>A share is opened with an account and a password, and nothing else. Asking the
    /// credential for that capability — instead of reading two fields off it — is what turns "a
    /// bearer token was chosen here" into this sentence rather than into a connection attempt
    /// with empty strings.</summary>
    internal static (string Username, string Password) AccountOf(Credential credential) =>
        credential is IPasswordAuthentication account
            ? (account.UserName, account.Password)
            : throw new InvalidOperationException("the credential '" + credential.Username
                + "' cannot open a share: a remote folder needs an account and a password, and this one is a '"
                + credential.Kind + "' credential; choose one of the account credentials in the layer's settings");

    public string Name => TypeName;
    public bool Writable => false;
    public SmbPath Path => _path;

    /// <summary>The account this layer presents, for a status view. A name only.</summary>
    public string AccountName => _connection.Account;

    public bool Connected => _connection.Account.Length == 0 || _connection.Established;

    /// <summary>
    /// What caches key on. A remote folder has no revision, so this is the mapped directory's
    /// own last-write time — cheap to compute and stable while nothing at the top of the tree
    /// changes — read at most once per <see cref="SmbOptions.GenerationTtlSeconds"/>.
    ///
    /// It is consulted for every cache lookup the layers above make (see
    /// <c>DurableCacheLayer.ResidentKey</c>), so it must not touch the network each time. The
    /// value is also deliberately *not* rolled when the share cannot be read: a generation that
    /// moved because the source is unreachable would throw away every cached entry at the moment
    /// the source is least able to answer.
    ///
    /// The limits are worth stating: a change deep inside the tree does not move this, so content
    /// changed by another client is seen when the top of the tree changes, when the generation
    /// is cleared (the layer's clear-cache action), or when a file's own content version moves
    /// and something asks the source again.
    /// </summary>
    public string Generation
    {
        get
        {
            lock (_generationGate)
            {
                if (_generation.Length > 0 && Environment.TickCount64 - _generationReadAt < _generationTtlMs)
                    return _generation;
                ReadGeneration();
                return _generation;
            }
        }
    }

    /// <summary>Seconds since the generation was last read from the share, for a status view.</summary>
    public long GenerationAgeSeconds =>
        Math.Max(0, (Environment.TickCount64 - Volatile.Read(ref _generationReadAt)) / 1000);

    /// <summary>How many sessions this layer established, including reconnections.</summary>
    public long Sessions => _connection.Sessions;

    private void ReadGeneration()
    {
        _generationReadAt = Environment.TickCount64;
        try
        {
            _generation = Directory.GetLastWriteTimeUtc(_path.Root).Ticks.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep what the last successful read said; only a layer that has never read one
            // starts at a value.
            if (_generation.Length == 0) _generation = "0";
        }
    }

    public bool TryGetEntry(string rel, out VfsEntry entry)
    {
        string path = LocalPath(rel);
        VfsEntry? found = null;
        bool hit = Retry(() =>
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            // Everything else — the share went away, the session dropped, the account lost
            // access — is thrown rather than answered "no such file": a caller that hears
            // "missing" records a deletion that never happened.
            var info = new FileInfo(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                found = new VfsEntry { Name = VfsPath.Name(rel), IsDir = true, TimeUtc = info.LastWriteTimeUtc };
                return true;
            }
            long size = info.Length;
            DateTime modified = info.LastWriteTimeUtc;
            found = new VfsEntry
            {
                Name = VfsPath.Name(rel),
                IsDir = false,
                Size = size,
                TimeUtc = modified,
                ContentVersion = VersionOf(size, modified)
            };
            return true;
        });
        entry = found!;
        return hit;
    }

    public IReadOnlyList<VfsEntry> ListOwn(string rel)
    {
        string path = LocalPath(rel);
        return Retry<IReadOnlyList<VfsEntry>>(() =>
        {
            try
            {
                // One QUERY_DIRECTORY per folder over SMB, carrying every child's attributes, so
                // a listing is one round trip and no child is stat-ed separately.
                return new FileSystemEnumerable<VfsEntry>(path, (ref FileSystemEntry child) => new VfsEntry
                {
                    Name = child.FileName.ToString(),
                    IsDir = child.IsDirectory,
                    Size = child.IsDirectory ? 0 : child.Length,
                    TimeUtc = child.LastWriteTimeUtc.UtcDateTime,
                    ContentVersion = child.IsDirectory ? null : VersionOf(child.Length, child.LastWriteTimeUtc.UtcDateTime)
                }, new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    AttributesToSkip = 0,
                    IgnoreInaccessible = false,
                    ReturnSpecialDirectories = false
                }).ToArray();
            }
            catch (DirectoryNotFoundException) { return Array.Empty<VfsEntry>(); }
        });
    }

    public bool TryOpenRead(string rel, out VfsEntry entry, out Stream stream)
    {
        string path = LocalPath(rel);
        VfsEntry? found = null;
        Stream? opened = null;
        bool hit = Retry(() =>
        {
            if (!TryGetEntry(rel, out var header) || header.IsDir) return false;
            found = header;
            opened = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            return true;
        });
        entry = found!;
        stream = opened!;
        return hit;
    }

    /// <summary>
    /// A file's identity on a share. Windows has no etag, so this is the pair the redirector
    /// already returns with the attributes: a file whose bytes changed by any client has a new
    /// write time, and one that was replaced by a same-sized file within the same tick is the
    /// case this cannot see. It is what lets the caches answer after the generation has moved —
    /// a new generation re-keys the namespace without re-fetching content that did not change.
    /// </summary>
    public static string VersionOf(long size, DateTime lastWriteUtc) =>
        size.ToString(CultureInfo.InvariantCulture) + ":" + lastWriteUtc.Ticks.ToString(CultureInfo.InvariantCulture);

    /// <summary>The local path for a path inside the mount, refused if it would leave the folder
    /// this layer maps: Windows resolves <c>..</c> in the client before the server sees it, so a
    /// name carrying one would reach a folder above the configured root.</summary>
    public string LocalPath(string rel)
    {
        if (rel.Length == 0) return _path.Root;
        if (rel.Contains("..", StringComparison.Ordinal) && rel.Split('/').Any(segment => segment is "." or ".."))
            throw new UnauthorizedAccessException("'" + rel + "' is not a path in this folder");
        return _path.Root + "\\" + rel.Replace('/', '\\');
    }

    /// <summary>Runs an operation, and retries it once after re-establishing the session when it
    /// failed the way a dropped session fails. The retry is safe: every operation here either
    /// has not touched the far side yet or reads.</summary>
    private T Retry<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (IOException ex) when (SmbConnection.IsConnectionFailure(ex.HResult & 0xFFFF))
        {
            _connection.Reconnect();
            return operation();
        }
    }

    public object Describe(string id) => new
    {
        layer = id,
        path = _path.Root,
        share = _path.ShareRoot,
        account = _connection.Account.Length == 0 ? null : _connection.Account,
        connected = Connected,
        generation = Generation,
        generationAgeSeconds = GenerationAgeSeconds,
        generationTtlSeconds = _generationTtlMs / 1000,
        sessions = Sessions
    };

    public void Dispose() => _connection.Dispose();
}
