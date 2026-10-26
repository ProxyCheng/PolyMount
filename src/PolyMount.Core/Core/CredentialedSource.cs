namespace PolyMount.Core;

/// <summary>A source refused to serve because the account was missing or not accepted. Thrown in
/// the plugin, understood by the host, and shown to the reader as the instance's error.</summary>
public sealed class AuthRequiredException : Exception
{
    public AuthRequiredException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// How a source that needs credentials finds them.
///
/// One rule for every protocol: the credential the layer names, if it names one; otherwise the
/// account written into the configuration; otherwise whatever the protocol's own client saved for
/// this machine; otherwise nothing — anonymous, which is enough for a source that serves without
/// asking.
///
/// The product used to remember accounts by address and guess which one a repository wanted. That
/// guessing is gone: which account a layer uses is now a thing a reader can see and change.
/// </summary>
public abstract class CredentialedSource
{
    protected CredentialedSource(string url, SourceConfig source)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException("a credentialed source needs an absolute url", nameof(url));
        Source = source;
        Origin = CredentialStore.OriginOf(uri);
    }

    protected SourceConfig Source { get; }

    /// <summary>The origin this source talks to, for messages.</summary>
    public string Origin { get; }

    /// <summary>The credential to present, or null to try anonymously. Resolved once, when the
    /// source is built: the result is held by the protocol client, so serving a request never
    /// reads the credential store or the client's cache.</summary>
    public Credential? Resolve()
    {
        // A chosen credential is a decision, not a hint: it is used as it stands, and one that has
        // been deleted is reported rather than quietly replaced by another account.
        if (!string.IsNullOrWhiteSpace(Source.CredentialId))
            return CredentialStore.Use(Source.CredentialId, Origin)
                ?? throw new InvalidOperationException("the credential this layer names no longer exists; choose another in the layer's settings");
        if (!string.IsNullOrWhiteSpace(Source.Username))
            return new PasswordCredential { Username = Source.Username, Password = Source.Password ?? string.Empty };
        return ResolveProtocolCredential();
    }

    /// <summary>Where this protocol's own client keeps credentials, if it does. Null by default:
    /// a source whose credentials only ever come from the product has nothing to inherit.</summary>
    protected virtual Credential? ResolveProtocolCredential() => null;
}
