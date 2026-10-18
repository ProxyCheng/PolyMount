namespace PolyMount.Core;

/// <summary>A source refused to serve because the account was missing or not accepted. Thrown in
/// the plugin, understood by the host, and turned into the prompt the reader sees.</summary>
public sealed class AuthRequiredException : Exception
{
    public AuthRequiredException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>What a source type has to supply before the product can ask a user to sign in: the
/// origin the credential belongs to, and a way to check that the account works before it is
/// stored. The storage, the order in which credentials are looked for, and the prompt all live in
/// Core, so a Git or SSH plugin later only writes these two methods.</summary>
public interface ICredentialHandler
{
    /// <summary>The origin a sign-in is for — also the key the credential is stored under.</summary>
    string OriginOf(SourceConfig source);

    /// <summary>Checks the account against the server and stores it. Throws with a message worth
    /// showing when the server refuses it; nothing is stored in that case.</summary>
    void SignIn(SourceConfig source, string username, string password);
}

/// <summary>
/// How a source that needs credentials finds them, and what happens after a user signs in.
///
/// The order is the same for every protocol and is decided here once: what the configuration says
/// (only reachable by editing the file now), then what the user entered in this product, then
/// whatever the protocol's own client stored, then nothing — anonymous, which is enough for a
/// source that serves without asking.
///
/// A plugin implements two things: where its protocol keeps cached credentials, if anywhere, and
/// how to check an account against the server.
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

    /// <summary>The origin the credential is stored under, and what a prompt names.</summary>
    public string Origin { get; }

    /// <summary>The credential to present, or null to try anonymously.</summary>
    public Credential? Resolve()
    {
        // What the reader entered here wins over a hand-edited configuration pair: a sign-in is the
        // newer statement, and a stale pair in the file would otherwise make signing in appear to
        // do nothing at all.
        if (CredentialStore.Find(Origin) is { } entered) return entered;
        if (!string.IsNullOrWhiteSpace(Source.Username)) return new Credential(Source.Username, Source.Password ?? string.Empty);
        return ResolveProtocolCredential();
    }

    /// <summary>Where this protocol's own client keeps credentials, if it does. Null by default:
    /// a source whose credentials only ever come from the product has nothing to inherit.</summary>
    protected virtual Credential? ResolveProtocolCredential() => null;

    /// <summary>Asks the server whether the account works. Throwing is how it says no.</summary>
    protected abstract void Verify(Credential credential);

    /// <summary>Checks the account and stores it. Whatever the implementation does with the
    /// credential on the way is its business; what the product keeps is the origin and the pair.</summary>
    public void SignIn(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("a user name is required");
        var credential = new Credential(username, password);
        Verify(credential);
        if (!CredentialStore.Save(Origin, credential))
            throw new InvalidOperationException("the server accepted the account, but it could not be saved for this service account");
    }
}
