namespace PolyMount.Plugins;

/// <summary>
/// What a credential can actually prove, asked of the record as a whole.
///
/// A record may be one thing or several: a client certificate is presented with an account, a
/// sign-in token, or whatever the Subversion client saved, and those are records of their own
/// hanging under it (<see cref="ClientCertificateCredential.Attached"/>). So a source that wants
/// an account asks <see cref="Password"/> rather than <c>credential is IPasswordAuthentication</c>
/// — the object that answers that question for a certificate is the certificate itself, and it
/// answers "no account" whatever is hanging under it.
///
/// This is why the answer is a part and not a wrapper: the parts are the credential objects the
/// store built, of whatever type their kind produces — a plugin's own class included — and no
/// wrapper can be an instance of a class the host never compiled against. A consumer that ignores
/// parts keeps seeing the record itself, which is what a plugin built before this existed does;
/// it simply sees none of what was attached.
/// </summary>
public static class CredentialParts
{
    /// <summary>Everything the record offers, the record itself first.</summary>
    public static IReadOnlyList<Credential> Of(Credential? credential) =>
        credential?.Parts ?? Array.Empty<Credential>();

    /// <summary>The account to present, or null when nothing in the record holds one.
    ///
    /// A part that names no password is not an account: a certificate answers
    /// <see cref="IPasswordAuthentication"/> for a record written before attachments existed, and
    /// an empty pair is what "there is no account here" looks like.</summary>
    public static IPasswordAuthentication? Password(Credential? credential) =>
        Of(credential).OfType<IPasswordAuthentication>()
            .FirstOrDefault(account => account.Password.Length > 0);

    /// <summary>The client certificate to present, or null when the record holds none.</summary>
    public static ICertificateAuthentication? Certificate(Credential? credential) =>
        Of(credential).OfType<ICertificateAuthentication>()
            .FirstOrDefault(certificate => certificate.Certificate.Length > 0);

    /// <summary>The installed certificate to present, or null when the record names none. A part
    /// that names no thumbprint is not one: the same rule as a certificate with no file.</summary>
    public static IInstalledCertificate? Installed(Credential? credential) =>
        Of(credential).OfType<IInstalledCertificate>()
            .FirstOrDefault(certificate => certificate.Thumbprint.Length > 0);

    /// <summary>The bearer token to present, or null when the record holds none.</summary>
    public static IBearerAuthentication? Bearer(Credential? credential) =>
        Of(credential).OfType<IBearerAuthentication>()
            .FirstOrDefault(token => token.Token.Length > 0);

    /// <summary>A part of a given type, for a capability that is a class rather than one of the
    /// three interfaces — the Subversion client's cache is one, and it is what the SVN source asks
    /// for by name.</summary>
    public static T? Find<T>(Credential? credential) where T : class =>
        Of(credential).OfType<T>().FirstOrDefault();
}
