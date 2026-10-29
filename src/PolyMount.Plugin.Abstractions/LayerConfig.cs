using System.Text.Json;

namespace PolyMount.Plugins;

/// <summary>One layer of a mount's chain, as the configuration describes it.
///
/// The plugin-facing view of a layer's configuration: the type decides which factory builds it,
/// the rest is what that factory reads. A layer's own settings live in <see cref="Parameters"/>
/// under keys only the layer's author knows, which is what lets a plugin add a layer type without
/// the host learning anything about it.
/// </summary>
public sealed class LayerConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Type { get; set; } = "overlay";
    public string Path { get; set; } = "";
    public long CacheCapacityBytes { get; set; } = 4L * 1024 * 1024 * 1024;
    public SourceConfig Source { get; set; } = new();
    public string Color { get; set; } = "";
    public bool Compression { get; set; }
    public Dictionary<string, JsonElement> Parameters { get; set; } = new();
}

/// <summary>The data source a chain's bottom layer speaks, as the configuration describes it.</summary>
public sealed class SourceConfig
{
    public string Type { get; set; } = "svn";
    public string Url { get; set; } = "";
    public long PinnedRevision { get; set; } = 0;

    /// <summary>Certificate failures this source accepts, comma separated, mirroring
    /// <c>svn --trust-server-cert-failures</c>: unknown-ca, cn-mismatch, expired, not-yet-valid,
    /// other.
    ///
    /// Empty — the default — is the strict reading: the server's certificate has to verify. It
    /// used to default to the whole list, which accepted any certificate from anybody, and the
    /// account is offered before the first challenge arrives, so an on-path attacker could take
    /// it with a certificate it made itself. A self-signed internal server lists exactly the
    /// failures it means to accept, in its own configuration, where a reader can see what this
    /// source promises about its network.</summary>
    public string TrustServerCertFailures { get; set; } = "";

    /// <summary>Whether a source may talk over a transport that is neither encrypted nor
    /// authenticated — <c>http://</c>. It is refused by default: the content becomes the mounted
    /// files and the account travels with the request, so an on-path attacker with no certificate
    /// at all can read and rewrite both.</summary>
    public bool AllowInsecureTransport { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }

    // Used by the "dir" source: the local directory this layer maps.
    public string Path { get; set; } = "";

    // Used by the "dir" source: allow writing straight through to the backing directory.
    public bool Writable { get; set; } = false;

    /// <summary>The named credential this source uses, or empty for none. A name that no longer
    /// exists is an error rather than a reason to try another account: which account a layer uses
    /// should never be a guess.</summary>
    public string CredentialId { get; set; } = "";

    /// <summary>A source that is only being asked whether an account works, not kept. It never
    /// reaches a configuration file and never goes into one — verifying an account must not be the
    /// act that binds it to an address somebody typed into a form.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool VerifyOnly { get; set; }
    public Dictionary<string, JsonElement> Parameters { get; set; } = new();
}
