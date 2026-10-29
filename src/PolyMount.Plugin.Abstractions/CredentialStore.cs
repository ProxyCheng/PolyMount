using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace PolyMount.Plugins;

/// <summary>The kinds of credential the product ships, and the type each one builds. Which of them
/// a layer offers is the layer's own answer (see <see cref="CredentialConsumers"/>).</summary>
public static class CredentialTypes
{
    public const string Password = "password";
    public const string Certificate = "certificate";
    /// <summary>A certificate installed in a Windows store, named by store and thumbprint: the
    /// private key stays where Windows keeps it, and the record holds no secret at all.</summary>
    public const string InstalledCertificate = "installed-certificate";
    /// <summary>The kind whose record is a list of other records: a certificate and the account it
    /// is presented with, a certificate and a sign-in token. It proves nothing itself — what it
    /// is, is a way to choose several credentials as one thing a layer names.</summary>
    public const string Composite = "composite";

    /// <summary>The kinds the product ships.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Password, Certificate, InstalledCertificate, Composite };

    /// <summary>Whether a record of this kind can be read: one of the three above, or one a loaded
    /// plugin describes (see <see cref="CredentialKinds"/>).</summary>
    public static bool IsKnown(string kind) => CredentialKinds.IsKnown(kind);

    /// <summary>Whether records of this kind are made of other records — the composite is, and a
    /// plugin kind says so on its descriptor (<see cref="CredentialKindDescriptor.Attaches"/>).
    ///
    /// The store accepts children on those records and the form draws the row that picks them. One
    /// level by rule: a child may not itself be made of others, which is what makes a chain
    /// impossible and spares everyone a depth limit.</summary>
    public static bool Attaches(string kind) =>
        string.Equals(kind, Composite, StringComparison.OrdinalIgnoreCase) ||
        CredentialKinds.Find(kind)?.Attaches == true;
}

/// <summary>
/// What every credential is: something the store can name, choose and show.
///
/// It deliberately says nothing about *how* the credential proves who it is. That used to live
/// here — an account and a password on the base class — which made every other way of
/// authenticating look like a password with extra fields, and made every consumer ask "which
/// concrete type is this?" before it could use one. A credential now declares what it can do by
/// implementing one or more of the interfaces below, and a consumer asks for the capability it
/// needs (<see cref="CredentialParts.Bearer"/>) instead of for a class.
///
/// <see cref="Username"/> stays because it is not a secret and it is not a mechanism: it is who
/// the record is for, which is what a list shows and what a server usually names.
/// </summary>
public abstract class Credential
{
    /// <summary>The stable discriminator stored beside the secret and shown to the interface.</summary>
    public abstract string Kind { get; }

    /// <summary>The account this record is for, when it names one. Display and routing only: a
    /// credential that authenticates without an account leaves it empty.</summary>
    public string Username { get; init; } = "";

    /// <summary>What this record is made of — itself, unless it was built from other records as
    /// well.
    ///
    /// This is the question a consumer has to ask instead of <c>credential is X</c>: a record that
    /// carries a certificate *and* the account to go with it is one record and two ways of
    /// authenticating, and no single object can answer "am I a password?" for both halves — the
    /// half that answers is a different object, often of a type the host has never heard of (a
    /// plugin's own credential class). So the record hands out its parts and the consumer looks
    /// for the capability it presents among them (<see cref="CredentialParts"/>).
    ///
    /// The object itself is always the first part: a consumer that ignores parts — a plugin built
    /// before this existed — still sees everything the record can do on its own.</summary>
    public virtual IReadOnlyList<Credential> Parts => new[] { this };
}

/// <summary>Authenticates by an account and the secret that proves it (HTTP Basic, an SMB logon,
/// an SVN repository that asks for a user).</summary>
public interface IPasswordAuthentication
{
    /// <summary>The account the server asks for. May be empty when the protocol supplies it.</summary>
    string UserName { get; }
    string Password { get; }
}

/// <summary>Authenticates by presenting a client certificate (mutual TLS), from a file the
/// record holds.</summary>
public interface ICertificateAuthentication
{
    /// <summary>The PKCS#12 file and the password that opens it.</summary>
    byte[] Certificate { get; }
    string CertificatePassword { get; }
}

/// <summary>Authenticates by presenting a client certificate that is already installed in a
/// Windows certificate store.
///
/// The difference from <see cref="ICertificateAuthentication"/> is where the private key lives: in
/// the store, under the protection Windows gives it, rather than in a file whose password this
/// product would have to keep. A key that cannot be exported works this way, which is the point —
/// and a record that names one holds no secret at all, only a thumbprint.
///
/// The store is the one the process can see: <c>LocalMachine</c> is the machine's, and every
/// account can read it, while <c>CurrentUser</c> is the account the service runs as. A certificate
/// in somebody else's user store is not reachable from here and is not meant to be.</summary>
public interface IInstalledCertificate
{
    /// <summary><c>LocalMachine</c> or <c>CurrentUser</c>.</summary>
    string Store { get; }

    /// <summary>The certificate's thumbprint, upper-case hexadecimal without separators — what a
    /// store is searched by, and the whole of what the record holds.</summary>
    string Thumbprint { get; }
}

/// <summary>Authenticates by presenting a bearer token — an access token from an identity
/// provider, a single sign-on session, a personal API token. The token is already the proof;
/// there is no account the server asks for, which is exactly why this cannot be spelled as a
/// password.</summary>
public interface IBearerAuthentication
{
    string Token { get; }
}

/// <summary>An account and its password — the ordinary credential.</summary>
public sealed class PasswordCredential : Credential, IPasswordAuthentication
{
    public override string Kind => CredentialTypes.Password;
    string IPasswordAuthentication.UserName => Username;
    public string Password { get; init; } = "";
}

/// <summary>A credential that is made of others: a list of records a layer names as one thing, and
/// a server is shown each of them — the client certificate during the TLS handshake, the account in
/// an Authorization header on every request after it.
///
/// It exists because a pairing is a decision somebody makes and may want to see, name and reuse: the
/// certificate and the account are records of their own (one certificate can be paired with two
/// accounts, one account with none), and this is the record that says "these together, here". The
/// alternative — a certificate that holds an account — made one kind answer for two proofs, made
/// every other pairing (a token, a client cache) inexpressible, and could not be asked about except
/// by casting the object to a type.
///
/// One level by rule: a child is never itself made of others, so a chain cannot form and there is
/// nothing to detect or limit.</summary>
public class CompositeCredential : Credential
{
    public CompositeCredential() { }

    /// <summary>A composite already holding records — for a caller that has them in hand: a test
    /// asking a layer what it would present, or a plugin that builds one itself. The store fills
    /// <see cref="Children"/> for the records it opens; this is the same object, built the other
    /// way round.</summary>
    public CompositeCredential(params Credential[] children) => Children = children;

    public override string Kind => CredentialTypes.Composite;

    /// <summary>The records this one is made of, in the order they were put here, as the store
    /// opened them. Filled by the store, which is the only thing that can look a record up.</summary>
    public IReadOnlyList<Credential> Children { get; internal set; } = Array.Empty<Credential>();

    /// <summary>Itself and its children, itself first: a consumer asking for an account finds the
    /// one someone put here, and one that knows nothing of composites still gets a credential it
    /// can ask about — an empty one, which is what "this record is not an account" looks like.</summary>
    public override IReadOnlyList<Credential> Parts => Children.Count == 0
        ? new Credential[] { this }
        : new[] { (Credential)this }.Concat(Children).ToArray();
}

/// <summary>A PKCS#12 file for mutual TLS, the password that opens it.
///
/// The account a server may ask for beside the certificate used to live in this record, which made
/// the class answer <c>IPasswordAuthentication</c> whatever it held. It answers for the certificate
/// and nothing else now: the pairing is a <see cref="CompositeCredential"/> those records are put
/// into, which is visible in the manager and reusable.</summary>
public sealed class ClientCertificateCredential : Credential, ICertificateAuthentication
{
    public override string Kind => CredentialTypes.Certificate;
    byte[] ICertificateAuthentication.Certificate => Certificate;
    string ICertificateAuthentication.CertificatePassword => CertificatePassword;
    public byte[] Certificate { get; init; } = Array.Empty<byte>();
    public string CertificatePassword { get; init; } = "";
}

/// <summary>A certificate the machine already has, named by where it lives and its thumbprint.
///
/// Its own kind rather than a second shape of the one above: a record that holds a file and a
/// record that points into a store hold different things, are filled in by different forms, and go
/// wrong in different ways — "the password does not open this file" against "that thumbprint is not
/// in this account's store". One class that is both would have to answer "which am I?" before it
/// could be used, which is the question the capability interfaces exist to replace.</summary>
public sealed class InstalledCertificateCredential : Credential, IInstalledCertificate
{
    public override string Kind => CredentialTypes.InstalledCertificate;
    public string Store { get; init; } = "";
    public string Thumbprint { get; init; } = "";
}

/// <summary>One certificate this account could name, as the form that picks one shows it: where it
/// lives, which one it is, and the three facts a reader recognises a certificate by.</summary>
public sealed class InstalledCertificateInfo
{
    /// <summary><c>LocalMachine</c> or <c>CurrentUser</c>.</summary>
    public string Store { get; set; } = "";
    public string Thumbprint { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Issuer { get; set; } = "";
    public DateTime NotAfter { get; set; }
}

/// <summary>What a credential looks like to the interface: everything except the secret.
/// <see cref="Usable"/> is false when this account cannot open the stored secret — a record
/// written by another Windows account, or one whose payload no longer parses — which is what
/// the layer editor warns about where the credential is chosen.</summary>
public sealed record CredentialRecord(string Id, string Name, string Username,
    string Kind = CredentialTypes.Password, bool Usable = true, IReadOnlyList<string>? Origins = null)
{
    /// <summary>The layer types that would present this record, as their own filters answered when
    /// the list was read (<see cref="CredentialConsumers"/>). A picker offers a record to the layer
    /// being edited only when its type is on this list; empty means no loaded layer can present it,
    /// which is what a record whose kind's plugin is gone looks like.</summary>
    public IReadOnlyList<string> UseFor { get; init; } = Array.Empty<string>();

    /// <summary>The record is made of others when it says so: the composite is, and so is any
    /// plugin kind that set <see cref="CredentialKindDescriptor.Attaches"/>.</summary>
    public IReadOnlyList<string> Children { get; init; } = Array.Empty<string>();

    /// <summary>Where a certificate credential's certificate comes from. Empty for the PKCS#12 the
    /// record holds; otherwise the store an installed certificate was named in, with its
    /// thumbprint. Neither is a secret — a thumbprint is public — and the form needs them to open
    /// showing the choice the record was saved with.</summary>
    public string CertificateStore { get; init; } = "";
    public string CertificateThumbprint { get; init; } = "";
}

/// <summary>What the interface asks the store to save. A null password, certificate or
/// certificate password keeps what the record already holds, so a form that shows only the
/// fields a person changed does not have to read a secret back.</summary>
/// <param name="Values">The fields of a plugin kind (<see cref="CredentialKindDescriptor.Fields"/>),
/// keyed by their paths. An empty value on an edit keeps the stored one, for the same reason a
/// null password does. Unused by the two kinds the product ships.</param>
/// <param name="Children">The records a composite is made of, in the order they are presented,
/// replacing what it holds. Null keeps them — a client that does not offer them sends nothing, and
/// an empty list is an answer: the person took them all out.</param>
public sealed record CredentialInput(
    string? Id, string Name, string Kind, string Username,
    string? Password, byte[]? Certificate, string? CertificatePassword,
    IReadOnlyList<string>? Origins = null,
    IReadOnlyDictionary<string, string>? Values = null,
    IReadOnlyList<string>? Children = null,
    string? CertificateStore = null, string? CertificateThumbprint = null);

/// <summary>
/// The accounts this product keeps: one list of credentials, each with a name, an id, a user name
/// and a secret. A layer names the one it uses.
///
/// A credential belongs to a person, not to a server — the same account usually opens several
/// hosts — so nothing is matched by address. A file written by an earlier version that stored
/// accounts by address is read once and turned into named credentials.
///
/// The file holds no secret in the clear: each one is a DPAPI blob protected for the current
/// Windows user. The writer is the service, the one account both the supervisor and its workers
/// share, so an installation works whatever account it runs as; the tray cannot read or write it
/// and asks over the control pipe instead.
/// </summary>
public static class CredentialStore
{
    private static readonly object Gate = new();
    // Both directions use one options object. Reading is case-insensitive because the same file
    // may have been written by a build that named its properties differently; a guard that asks
    // for "items" must find "Items" rather than decide the file is empty.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Where the entries live: with the configuration and the stores, under the common
    /// application data folder the installer has already given the service account. The setter is
    /// internal so a test can point it at a temporary directory: everything below treats this path
    /// as the only file it reads or writes, and a test that used the machine's own store would be
    /// writing to the account it runs as.</summary>
    public static string Path { get; internal set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PolyMount", "credentials.json");

    /// <summary>The key an origin credential is stored under: scheme and host in lower case, and a
    /// port only when it is not the scheme's default, so <c>https://host</c> and
    /// <c>https://host:443</c> are one key and <c>http://host</c> is another.</summary>
    public static string OriginOf(Uri uri)
    {
        string host = uri.IdnHost.ToLowerInvariant();
        if (host.Contains(':') && !host.StartsWith('[')) host = "[" + host + "]";
        return uri.Scheme.ToLowerInvariant() + "://" + host + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port);
    }

    // ---- named credentials, the ones the manager owns ----

    public static List<CredentialRecord> List()
    {
        lock (Gate)
        {
            var document = Load();
            return document.Items
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => new CredentialRecord(item.Id, item.Name, item.Username, KindOf(item),
                    IsUsable(document, item), item.Origins.ToArray())
                {
                    UseFor = Consumers(document, item),
                    Children = item.Children.ToArray()
                })
                .ToList();
        }
    }

    /// <summary>Which layer types would present a record, for the list. The secret is opened once
    /// more here than <see cref="IsUsable"/> opens it — a list is a handful of records, and a
    /// single pass that answered both questions would have to carry the reason for each one.
    /// A record that cannot be opened, or whose kind has no loaded code, has no answer: empty,
    /// rather than an exception thrown into a window that is drawing a list.</summary>
    private static IReadOnlyList<string> Consumers(Document document, StoredCredential item)
    {
        try
        {
            return Materialize(document, item) is { } credential
                ? CredentialConsumers.Of(credential)
                : Array.Empty<string>();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Whether this account can actually use a record: the secret is a blob it can
    /// open, a certificate credential still names a certificate, and everything attached to it is
    /// still there to be presented. The layer editor shows the answer where the credential is
    /// chosen, before a mount has to fail to say so.
    ///
    /// An attachment that has been deleted or that cannot be built makes the whole record
    /// unusable rather than quietly dropping that half: a certificate that was paired with an
    /// account, presented alone, is a 401 nobody can explain.</summary>
    private static bool IsUsable(Document document, StoredCredential item)
    {
        try
        {
            return Materialize(document, item) is { } credential && PartIsUsable(credential);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool PartIsUsable(Credential credential) =>
        credential.Parts.All(part => part switch
        {
            // A certificate without its file, and an account without a password, are records that
            // say nothing a server can ask for.
            ClientCertificateCredential certificate => certificate.Certificate.Length > 0,
            InstalledCertificateCredential installed => installed.Thumbprint.Length > 0,
            PasswordCredential account => account.Password.Length > 0,
            // A plugin kind that could not build its credential threw while materializing, above.
            _ => true
        });

    /// <summary>Adds or changes a named credential and returns the record the interface can show.
    /// Names are what the picker shows, so they are unique: two rows reading the same thing would
    /// be a list with no way to tell which one a layer means.</summary>
    public static CredentialRecord Save(CredentialInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) throw new ArgumentException("a credential needs a name");
        // Empty is what a client that predates kinds sends, and it means a password credential.
        string kind = string.IsNullOrWhiteSpace(input.Kind) ? CredentialTypes.Password : input.Kind;
        if (!CredentialTypes.IsKnown(kind))
            throw new ArgumentException("unknown credential kind '" + kind + "'");
        string name = input.Name.Trim();
        lock (Gate)
        {
            var document = Load();
            var item = string.IsNullOrEmpty(input.Id)
                ? null
                : document.Items.FirstOrDefault(entry => entry.Id == input.Id)
                  ?? throw new ArgumentException("no such credential: " + input.Id);
            if (document.Items.Any(entry => !ReferenceEquals(entry, item) &&
                    string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("a credential named '" + name + "' already exists");

            // A record being edited keeps what the form did not resend, but only while it stays
            // the same kind: switching from a password to a certificate has nothing to keep.
            bool keeps = item != null && string.Equals(KindOf(item), kind, StringComparison.OrdinalIgnoreCase);
            bool certificate = string.Equals(kind, CredentialTypes.Certificate, StringComparison.OrdinalIgnoreCase);
            bool composite = string.Equals(kind, CredentialTypes.Composite, StringComparison.OrdinalIgnoreCase);
            bool installed = string.Equals(kind, CredentialTypes.InstalledCertificate, StringComparison.OrdinalIgnoreCase);
            var descriptor = CredentialKinds.Find(kind);
            var current = keeps && certificate ? CertificateOf(item!) : null;
            var currentInstalled = keeps && installed ? CertificateOf(item!) : null;
            string secret;
            string account = input.Username.Trim();
            if (certificate)
            {
                string certificateBytes = input.Certificate is { Length: > 0 } bytes
                    ? Convert.ToBase64String(bytes)
                    : current?.Certificate
                      ?? throw new ArgumentException("a certificate credential needs a PKCS#12 file");
                string certificatePassword = input.CertificatePassword
                    ?? current?.Password
                    ?? throw new ArgumentException("a certificate credential needs the password of its file");
                // A file that does not open is refused now rather than when a layer tries to
                // build: the form can say which field is wrong, the mount could only say it
                // failed.
                if (input.Certificate is { Length: > 0 }) ValidateCertificate(input.Certificate, certificatePassword);
                secret = JsonSerializer.Serialize(new CertificateSecret
                {
                    Certificate = certificateBytes,
                    Password = certificatePassword
                }, Options);
            }
            else if (installed)
            {
                // A reference, not a copy: the record holds where the certificate lives and which
                // one it is, and the private key never leaves the store. Checked here, in the
                // account that will present it — a thumbprint this account cannot open is refused
                // while the form is still open to say which one.
                string store = (input.CertificateStore ?? (keeps ? currentInstalled?.Store : null) ?? "").Trim();
                string thumbprint = NormalizeThumbprint(
                    input.CertificateThumbprint ?? (keeps ? currentInstalled?.Thumbprint : null) ?? "");
                ValidateInstalled(store, thumbprint);
                secret = JsonSerializer.Serialize(new CertificateSecret
                {
                    Store = store == "CurrentUser" ? "CurrentUser" : "LocalMachine",
                    Thumbprint = thumbprint
                }, Options);
            }
            else if (composite)
            {
                // A composite holds no secret of its own — what it proves is in the records it
                // names. The blob is still written, empty: a record is one shape, and "can this
                // account open it" stays one question for every kind.
                secret = "{}";
                account = "";
            }
            else if (descriptor == null)
            {
                if (string.IsNullOrWhiteSpace(input.Username))
                    throw new ArgumentException("a credential needs a user name");
                secret = input.Password
                    ?? (keeps ? UnprotectSecret(item!) : null)
                    ?? throw new ArgumentException("a credential needs a password");
            }
            else
            {
                var values = ValuesFor(item, keeps, input, descriptor);
                // Built once here so a record that cannot produce a credential is refused by the
                // form that made it, in the kind's own words, instead of by a mount later.
                descriptor.Create(values);
                secret = JsonSerializer.Serialize(values, Options);
                if (descriptor.AccountField.Length > 0)
                    account = values.GetValueOrDefault(descriptor.AccountField, "").Trim();
            }

            if (Protect(secret) is not { } protected_)
                throw new InvalidOperationException("the secret could not be protected for this service account");

            bool added = item == null;
            item ??= new StoredCredential { Id = Guid.NewGuid().ToString("N") };
            item.Name = name;
            item.Username = account;
            // The origins are the credential's own record of where it may be presented, and they
            // are written here rather than by using it: a person adding a second server to an
            // account says so once, in the place that holds the account. Null keeps whatever the
            // record already has, which is what a form that never showed them sends.
            if (input.Origins is not null) item.Origins = NormalizeOrigins(input.Origins);
            // A password record keeps its kind empty, which is how every file written before
            // kinds existed reads; the other kinds are written as the name they answer to.
            item.Kind = string.Equals(kind, CredentialTypes.Password, StringComparison.OrdinalIgnoreCase)
                ? ""
                : descriptor?.Kind ?? kind;
            // What this record is presented with. Only a certificate is presented with anything: it
            // is the one kind that says nothing about the account beside it, and a certificate is
            // never among these, which is what keeps the chain one level deep.
            var children = input.Children is { } asked
                ? asked
                : keeps ? item.Children.ToArray() : Array.Empty<string>();
            children = children.Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal).ToArray();
            // A kind that is not made of records keeps none, whatever a client sent: a record
            // switched from a composite to an account would otherwise hold a list nothing reads.
            if (!CredentialTypes.Attaches(kind)) children = Array.Empty<string>();
            if (composite && children.Count == 0)
                throw new ArgumentException("a composite credential needs at least one credential in it");
            ValidateChildren(document, item, children);
            item.Children = children.ToList();
            item.Secret = Convert.ToBase64String(protected_);
            if (added) document.Items.Add(item);
            if (!Save(document)) throw new InvalidOperationException("the credential could not be written");
            var stored = installed ? ParseCertificate(secret) : null;
            return new CredentialRecord(item.Id, item.Name, item.Username, kind)
            {
                Children = item.Children.ToArray(),
                CertificateStore = stored?.Store ?? "",
                CertificateThumbprint = stored?.Thumbprint ?? ""
            };
        }
    }

    /// <summary>Checks what a composite is about to be made of, while a form is still open to be
    /// told which record is wrong.
    ///
    /// Three rules, each of which would otherwise be discovered by a mount failing: nothing that
    /// no longer exists, nothing unusable (the account that was paired with the certificate is
    /// exactly the half whose absence reads as a 401), and nothing that answers the same question
    /// as another — two accounts, or two sign-in tokens, would leave the order of the list deciding
    /// which one a server sees and the other silently never presented. One of each is what a
    /// server can be shown.</summary>
    private static void ValidateChildren(Document document, StoredCredential item, IReadOnlyList<string> children)
    {
        if (children.Count == 0) return;
        var capabilities = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string id in children)
        {
            if (string.Equals(id, item.Id, StringComparison.Ordinal))
                throw new ArgumentException("a credential cannot contain itself");
            var child = document.Items.FirstOrDefault(entry => entry.Id == id)
                ?? throw new ArgumentException("no credential with id '" + id
                    + "' to put in this one; choose another in the credential manager");
            if (CredentialTypes.Attaches(KindOf(child)))
                throw new ArgumentException("'" + child.Name
                    + "' is made of other credentials; it cannot be put inside another one");
            if (!IsUsable(document, child))
                throw new ArgumentException("'" + child.Name
                    + "' cannot be used: this account cannot open it, or it is not complete");
            foreach (string capability in Capabilities(document, child))
                if (capabilities.TryGetValue(capability, out string? holder))
                    throw new ArgumentException("'" + child.Name + "' and '" + holder + "' both provide "
                        + capability + "; put one of each in a composite credential");
                else capabilities[capability] = child.Name;
        }
    }

    /// <summary>What one record can prove, in words a refusal can use: the capabilities its parts
    /// implement, and the type each part is — which is the only name a plugin's own kind has.</summary>
    private static IEnumerable<string> Capabilities(Document document, StoredCredential item)
    {
        var credential = Materialize(document, item)
            ?? throw new ArgumentException("'" + item.Name + "' cannot be opened by this account");
        foreach (var part in credential.Parts)
        {
            if (part is IPasswordAuthentication) yield return "an account";
            if (part is ICertificateAuthentication) yield return "a client certificate";
            if (part is IBearerAuthentication) yield return "a sign-in token";
            // A kind that implements none of the three is recognised by its own type, which is
            // what a consumer names when it asks for that kind (<see cref="CredentialParts.Find"/>).
            if (part is not IPasswordAuthentication and not ICertificateAuthentication and not IBearerAuthentication)
                yield return part.GetType().FullName ?? part.GetType().Name;
        }
    }

    /// <summary>The fields of a plugin kind as this save should store them: what the record holds,
    /// overlaid with what the form sent, where an empty value means "keep" (the form never shows a
    /// stored secret back, so it cannot resend one). A required field that ends up empty is
    /// refused here — the form says the same thing first, in the field's own words.</summary>
    private static Dictionary<string, string> ValuesFor(StoredCredential? item, bool keeps,
        CredentialInput input, CredentialKindDescriptor descriptor)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (keeps && item != null && UnprotectSecret(item) is { } stored)
            foreach (var pair in ParseValues(stored)) values[pair.Key] = pair.Value;
        if (input.Values != null)
            foreach (var pair in input.Values)
            {
                if (keeps && string.IsNullOrEmpty(pair.Value) && values.ContainsKey(pair.Key)) continue;
                values[pair.Key] = pair.Value ?? "";
            }
        foreach (var field in descriptor.Fields)
        {
            if (!values.ContainsKey(field.Path) && field.DefaultValue is { } fallback)
                // A default is filled the way a form fills it: environment names are the account
                // doing the filling, which for the one caller that reaches this line without the
                // form is the service's own.
                values[field.Path] = Environment.ExpandEnvironmentVariables(fallback);
            if (field.Required && string.IsNullOrWhiteSpace(values.GetValueOrDefault(field.Path)))
                throw new ArgumentException("the credential needs '" + field.Path + "'");
        }
        return values;
    }

    private static Dictionary<string, string> ParseValues(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, Options)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException) { return new Dictionary<string, string>(StringComparer.Ordinal); }
    }

    public static void RemoveCredential(string id)
    {
        lock (Gate)
        {
            var document = Load();
            if (document.Items.RemoveAll(entry => entry.Id == id) == 0) return;
            Save(document);
        }
    }

    /// <summary>The secret behind a named credential, for a consumer with no origin to bind it to
    /// — a share reached by its UNC path, which is an address rather than a URL. A consumer that
    /// does have an origin uses <see cref="Use"/>, which is where the binding is enforced.</summary>
    public static Credential? FindById(string id)
    {
        lock (Gate)
        {
            var document = Load();
            var item = document.Items.FirstOrDefault(entry => entry.Id == id);
            return item == null ? null : Materialize(document, item);
        }
    }

    /// <summary>The secret behind a named credential, checked against the origin it is about to be
    /// sent to.
    ///
    /// A credential belongs to a person rather than to a server — one account usually opens
    /// several repositories — so it is not stored per address. What it is, is bounded: the first
    /// origin it is used for is written down with it, and a later layer that names the same
    /// credential for a different origin is refused. Without that, whoever can change the
    /// configuration can point a layer at a host of their choosing, name the credential that holds
    /// somebody's repository password, and read it out of the first 401 the server sends back.
    ///
    /// The first use is recorded rather than confirmed: it is the same act as choosing the
    /// credential for that layer, and a prompt in the middle of a mount is a prompt nobody could
    /// answer.</summary>
    public static Credential? Use(string id, string origin) => Resolve(id, origin, write: true);

    /// <summary>The secret behind a named credential, checked against the origin it is about to be
    /// presented at — the same rule <see cref="Use"/> applies — without recording the origin.
    ///
    /// It exists for a caller that is asking a question rather than mounting: <see cref="Use"/>
    /// writes the first origin down, which is right when a layer is built and wrong when somebody
    /// is trying an account against an address they may still be editing. A form that asked "does
    /// this work?" would otherwise leave a credential bound to a host nobody meant.
    /// </summary>
    public static Credential? Check(string id, string origin) => Resolve(id, origin, write: false);

    /// <summary>Whether the origin binding applies to a record of this kind at all.
    ///
    /// The binding protects a secret: it keeps a record holding somebody's password from being
    /// pointed at a server its holder never named. A plugin kind whose values hold no secret has
    /// nothing to protect — the Subversion client-cache kind is a reference to "whatever this
    /// Windows account saved", and one such record is meant for every repository that account can
    /// reach — so binding it would refuse the second server and make the reader edit a list that
    /// describes nothing. Such a record writes no origin down either: the next reader to look at
    /// it should not see a list that was never a decision.</summary>
    private static bool BindsOrigins(string kind)
    {
        // A composite holds no secret of its own, so there is nothing of it to bind: everything it
        // is made of is bound by the walk below, one child at a time, and each child keeps its own
        // list of the hosts it may be shown to.
        if (string.Equals(kind, CredentialTypes.Composite, StringComparison.OrdinalIgnoreCase)) return false;
        var descriptor = CredentialKinds.Find(kind);
        // The two kinds the product ships have no descriptor here — a password record is the shape
        // every file written before kinds existed has — and an unknown kind is treated as one that
        // might hold a secret.
        if (descriptor == null) return true;
        foreach (var field in descriptor.Fields)
            if (field.Kind == CredentialFieldKind.Secret) return true;
        return false;
    }

    /// <summary>Checks an origin against a record and everything presented beside it, writing the
    /// first use down when <paramref name="write"/> allows it.
    ///
    /// The walk is the point: a password hanging under a certificate is still a secret somebody
    /// bound to a host, so without this the pairing would be a way to carry that password to the
    /// certificate's host instead. A record that holds no secret is not bound, the same rule as
    /// for a record used on its own.</summary>
    private static bool Bind(Document document, StoredCredential item, string origin, bool write,
        HashSet<string> open)
    {
        if (!open.Add(item.Id)) return false;
        bool changed = false;
        if (BindsOrigins(KindOf(item)))
        {
            if (item.Origins.Count == 0)
            {
                if (write)
                {
                    item.Origins.Add(origin);
                    changed = true;
                }
            }
            else if (!item.Origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                throw new CredentialOriginException(item.Id, item.Name, item.Origins.ToArray(), origin);
            }
        }
        // A child that is gone is not checked here: materializing says so, in a sentence that
        // names the record the reader can go and fix.
        foreach (string id in item.Children)
            if (document.Items.FirstOrDefault(entry => entry.Id == id) is { } child)
                changed |= Bind(document, child, origin, write, open);
        return changed;
    }

    private static Credential? Resolve(string id, string origin, bool write)
    {
        lock (Gate)
        {
            var document = Load();
            var item = document.Items.FirstOrDefault(entry => entry.Id == id);
            if (item == null) return null;
            bool changed = Bind(document, item, origin, write, new HashSet<string>(StringComparer.Ordinal));
            if (changed) Save(document);
            return Materialize(document, item);
        }
    }

    /// <summary>The credential a stored record stands for: its own secret opened, and — for a
    /// record made of others — each child opened as well.
    ///
    /// The two steps are separate because only one of them is per record. <see cref="Own"/> is the
    /// record on its own, what a file holds; <see cref="Composed"/> fills in what it is made of, so
    /// that a consumer asking "do you have an account?" is answered by the account someone put
    /// there rather than by a record that has none. A record whose secret cannot be opened, or a
    /// child that cannot be built, throws with a sentence naming which one: the caller reports it
    /// as an account that no longer works.
    /// </summary>
    private static Credential? Materialize(Document document, StoredCredential item) =>
        Composed(document, item, new HashSet<string>(StringComparer.Ordinal));

    private static Credential? Composed(Document document, StoredCredential item, HashSet<string> open)
    {
        var own = Own(item);
        if (own is not CompositeCredential composite || item.Children.Count == 0) return own;
        // One level is all the model allows (see CompositeCredential); a file edited by hand could
        // still name a chain that comes back on itself, and following it would be a hang rather
        // than an answer.
        if (!open.Add(item.Id))
            throw new InvalidOperationException("the credential '" + item.Name
                + "' contains itself; remove that in the credential manager");
        try
        {
            var children = new List<Credential>(item.Children.Count);
            foreach (string id in item.Children)
            {
                var child = document.Items.FirstOrDefault(entry => entry.Id == id)
                    ?? throw new InvalidOperationException("the credential '" + item.Name
                        + "' contains a credential that no longer exists; choose another in the credential manager");
                if (Composed(document, child, open) is { } part) children.Add(part);
            }
            composite.Children = children;
            return composite;
        }
        finally { open.Remove(item.Id); }
    }

    /// <summary>One record's own secret, without what it is made of.</summary>
    private static Credential? Own(StoredCredential item)
    {
        string? plain = UnprotectSecret(item);
        if (plain == null) return null;
        string kind = KindOf(item);
        if (string.Equals(kind, CredentialTypes.Password, StringComparison.OrdinalIgnoreCase))
            return new PasswordCredential { Username = item.Username, Password = plain };
        // The composite proves nothing by itself: it is the list that <see cref="Composed"/> fills
        // in. The empty object is still a credential a consumer can ask about.
        if (string.Equals(kind, CredentialTypes.Composite, StringComparison.OrdinalIgnoreCase))
            return new CompositeCredential { Username = item.Username };
        // A certificate the machine already has: the record is a pointer, so opening it is reading
        // two strings rather than a key.
        if (string.Equals(kind, CredentialTypes.InstalledCertificate, StringComparison.OrdinalIgnoreCase))
        {
            var reference = ParseCertificate(plain);
            if (reference == null || reference.Thumbprint.Length == 0) return null;
            return new InstalledCertificateCredential
            {
                Username = item.Username,
                Store = reference.Store.Length > 0 ? reference.Store : "LocalMachine",
                Thumbprint = reference.Thumbprint
            };
        }

        if (!string.Equals(kind, CredentialTypes.Certificate, StringComparison.OrdinalIgnoreCase))
        {
            // A plugin kind. A record whose plugin is not loaded cannot be opened at all, and
            // saying that is better than letting the caller report it as a credential that no
            // longer exists — the record is intact, what is missing is the code that reads it.
            var descriptor = CredentialKinds.Find(kind)
                ?? throw new InvalidOperationException("the credential '" + item.Name + "' is of kind '"
                    + kind + "', and no loaded plugin describes that kind");
            try { return descriptor.Create(ParseValues(plain)); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "the credential '" + item.Name + "' cannot be used: " + ex.Message, ex);
            }
        }

        var payload = ParseCertificate(plain);
        if (payload == null) return null;
        try
        {
            return new ClientCertificateCredential
            {
                Username = item.Username,
                Certificate = Convert.FromBase64String(payload.Certificate),
                CertificatePassword = payload.Password
            };
        }
        catch (FormatException) { return null; }
    }

    /// <summary>The kind a stored record is. Files written before kinds existed keep an empty
    /// kind, which is a password credential; anything else is the name it was written under,
    /// whether the product ships that kind or a plugin does.</summary>
    private static string KindOf(StoredCredential item) =>
        string.IsNullOrEmpty(item.Kind) ? CredentialTypes.Password : item.Kind;

    /// <summary>What a certificate credential holds, protected as one blob: the PKCS#12 bytes in
    /// base64 and the password that opens them.
    ///
    /// A record written before certificates stopped holding an account also carries <c>Account</c>
    /// here. It is not read any more — a pairing is a composite credential now — and not written:
    /// the next save of such a record drops it.</summary>
    private sealed class CertificateSecret
    {
        /// <summary>The file, for a record that holds one.</summary>
        public string Certificate { get; set; } = "";
        public string Password { get; set; } = "";
        /// <summary>The store and thumbprint, for a record that names an installed certificate.
        /// Empty in the other shape.</summary>
        public string Store { get; set; } = "";
        public string Thumbprint { get; set; } = "";
    }

    private static CertificateSecret? CertificateOf(StoredCredential item) =>
        UnprotectSecret(item) is { } json ? ParseCertificate(json) : null;

    private static CertificateSecret? ParseCertificate(string json)
    {
        try { return JsonSerializer.Deserialize<CertificateSecret>(json, Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>A thumbprint as a store is searched by: hexadecimal, upper case, no separators —
    /// the form of <c>X509Certificate2.Thumbprint</c>, so a value copied out of certmgr or typed
    /// with spaces and lower case still names the same certificate.</summary>
    private static string NormalizeThumbprint(string thumbprint) =>
        new string(thumbprint.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    /// <summary>Finds the certificate a record names, in the store it names, as this account.
    ///
    /// Null when this account cannot see it. A certificate is only a client credential if its
    /// private key opens for the process presenting it, and a store can hold one whose key belongs
    /// to somebody else.</summary>
    private static X509Certificate2? FindInstalled(string store, string thumbprint)
    {
        var location = string.Equals(store, "CurrentUser", StringComparison.OrdinalIgnoreCase)
            ? StoreLocation.CurrentUser
            : StoreLocation.LocalMachine;
        try
        {
            using var opened = new X509Store(StoreName.My, location);
            opened.Open(OpenFlags.ReadOnly);
            foreach (var candidate in opened.Certificates)
                if (string.Equals(candidate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
                    return candidate;
        }
        catch (CryptographicException) { }
        return null;
    }

    /// <summary>Refuses an installed certificate this account cannot present, in the words of the
    /// thing that is missing.</summary>
    private static void ValidateInstalled(string store, string thumbprint)
    {
        if (thumbprint.Length == 0)
            throw new ArgumentException("an installed certificate credential needs the certificate's thumbprint");
        bool known = string.Equals(store, "LocalMachine", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(store, "CurrentUser", StringComparison.OrdinalIgnoreCase);
        if (!known)
            throw new ArgumentException("'" + store + "' is not a certificate store; use LocalMachine or CurrentUser");
        var found = FindInstalled(store, thumbprint)
            ?? throw new ArgumentException("no certificate with thumbprint " + thumbprint
                + " in " + store + @"\My, as the account this service runs as");
        if (!found.HasPrivateKey)
            throw new ArgumentException("the certificate " + thumbprint
                + " is in " + store + @"\My but has no private key this account can use");
    }

    /// <summary>The certificates this account could name, for the form that picks one: the two
    /// stores a service can see — the machine's, and its own — and only what has a private key,
    /// since a certificate without one cannot present anything.</summary>
    public static List<InstalledCertificateInfo> Certificates()
    {
        var found = new List<InstalledCertificateInfo>();
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            try
            {
                using var opened = new X509Store(StoreName.My, location);
                opened.Open(OpenFlags.ReadOnly);
                foreach (var certificate in opened.Certificates)
                    if (certificate.HasPrivateKey)
                        found.Add(new InstalledCertificateInfo
                        {
                            Store = location.ToString(),
                            Thumbprint = certificate.Thumbprint,
                            Subject = certificate.Subject,
                            Issuer = certificate.Issuer,
                            NotAfter = certificate.NotAfter
                        });
            }
            catch (CryptographicException) { }
        }
        return found;
    }

    /// <summary>Opens the file with the password it will be used with. The private key stays in
    /// memory; nothing is written to a key container on disk.</summary>
    private static void ValidateCertificate(byte[] pfx, string password)
    {
        try
        {
            using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
                pfx, password, System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.EphemeralKeySet);
            if (!certificate.HasPrivateKey)
                throw new ArgumentException("the certificate file holds no certificate with a private key");
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            throw new ArgumentException("the certificate could not be opened with that password: " + ex.Message, ex);
        }
    }

    /// <summary>The origins a credential may be presented at, checked and spelled the one way.
    ///
    /// A string that is not an absolute http(s) origin is refused: this list is what stands
    /// between a stored password and a host nobody named, so an entry that cannot be compared is
    /// an entry that would silently never match — or, worse, match everything.</summary>
    private static List<string> NormalizeOrigins(IReadOnlyList<string> origins)
    {
        var normalized = new List<string>(origins.Count);
        foreach (string origin in origins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query))
                throw new ArgumentException("'" + origin + "' is not an origin (http(s)://host[:port])");
            string key = OriginOf(uri);
            if (!normalized.Contains(key, StringComparer.OrdinalIgnoreCase)) normalized.Add(key);
        }
        return normalized;
    }

    private static string? UnprotectSecret(StoredCredential item)
    {
        try { return Unprotect(Convert.FromBase64String(item.Secret)); }
        catch (FormatException) { return null; }
    }

    // ---- the document ----

    private sealed class Document
    {
        public List<StoredCredential> Items { get; set; } = new();
    }

    private sealed class StoredCredential
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Username { get; set; } = "";
        /// <summary>"certificate", or empty for a password credential — which is also how every
        /// file written before credentials had kinds is read.</summary>
        public string Kind { get; set; } = "";
        public string Secret { get; set; } = "";
        /// <summary>The origins this credential has been used for, as <see cref="OriginOf"/>
        /// spells them. Empty means it has not been used yet, which is also what every record
        /// written before this existed looks like: the first use records it.</summary>
        public List<string> Origins { get; set; } = new();
        /// <summary>The records this one is presented with, by id. Only a certificate names any;
        /// what it names is never another certificate, so the chain is one level deep.</summary>
        public List<string> Children { get; set; } = new();
    }

    /// <summary>What an older entry held once opened: the pair, protected together.</summary>
    private sealed record Stored(string Username, string Secret);

    private static PasswordCredential? Decode(byte[] blob)
    {
        if (Unprotect(blob) is not { } json) return null;
        try
        {
            return JsonSerializer.Deserialize<Stored>(json) is { } stored
                ? new PasswordCredential { Username = stored.Username, Password = stored.Secret }
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Reads the document. Shapes written before credentials were one named list —
    /// accounts remembered by address, and one address-less default — are folded into that list
    /// and written back, so every account the product holds is one thing and nothing is hidden
    /// behind an address.</summary>
    private static Document Load()
    {
        try
        {
            if (!File.Exists(Path)) return new Document();
            string text = File.ReadAllText(Path);
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new Document();

            // A file written while both models existed carries the named list and the address
            // entries side by side; the latter are folded in rather than left behind.
            bool named = TryGet(root, "items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array;
            var document = named
                ? JsonSerializer.Deserialize<Document>(text) ?? new Document()
                : new Document();

            bool migrated = false;
            if (TryGet(root, "origins", out var origins) && origins.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in origins.EnumerateObject())
                    if (entry.Value.ValueKind == JsonValueKind.String)
                        migrated |= Adopt(document, NameFor(entry.Name), entry.Value.GetString() ?? "");
            }
            else if (!named)
            {
                // The first shape was one flat map of address → protected pair.
                foreach (var entry in root.EnumerateObject())
                    if (entry.Value.ValueKind == JsonValueKind.String && !IsDocumentKey(entry.Name))
                        migrated |= Adopt(document, NameFor(entry.Name), entry.Value.GetString() ?? "");
            }
            // The second shape also kept one account with no address; it becomes a named entry too.
            if (TryGet(root, "default", out var fallback) && fallback.ValueKind == JsonValueKind.String)
                migrated |= Adopt(document, "Default", fallback.GetString() ?? "");

            if (migrated) Save(document);
            return document;
        }
        catch (IOException) { return new Document(); }
        catch (UnauthorizedAccessException) { return new Document(); }
        catch (JsonException) { return new Document(); }
    }

    /// <summary>Turns one address-keyed entry into a credential in the list, under a name that is
    /// free. An account already present is recognised rather than duplicated: the old store kept
    /// one entry per address, so the same pair saved for two repositories became two rows. False
    /// for an entry this account cannot open; it is not turned into a row without a secret.</summary>
    private static bool Adopt(Document document, string name, string blob)
    {
        try
        {
            if (Decode(Convert.FromBase64String(blob)) is not { } credential) return false;
            foreach (var existing in document.Items)
            {
                if (!string.Equals(existing.Username, credential.Username, StringComparison.Ordinal)) continue;
                if (UnprotectSecret(existing) == credential.Password) return true;
            }
            if (Protect(credential.Password) is not { } secret) return false;
            document.Items.Add(new StoredCredential
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = FreeName(document, name),
                Username = credential.Username,
                Secret = Convert.ToBase64String(secret)
            });
            return true;
        }
        catch (FormatException) { return false; }
    }

    /// <summary>Finds a property without caring how an earlier writer capitalised it.</summary>
    private static bool TryGet(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>The names a shaped document uses. A flat map's keys are addresses, so these can
    /// only be document fields and are not mistaken for one.</summary>
    private static bool IsDocumentKey(string name) =>
        string.Equals(name, "items", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "origins", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "default", StringComparison.OrdinalIgnoreCase);

    /// <summary>A readable name for an account that only had an address: its host, without the
    /// scheme and port, so it can be recognised and renamed.</summary>
    private static string NameFor(string origin)
    {
        try
        {
            var uri = new Uri(origin, UriKind.Absolute);
            return uri.Host.Length > 0 ? uri.Host : origin;
        }
        catch (UriFormatException) { return origin; }
    }

    /// <summary>The name itself, or the first numbered variant that nothing else holds.</summary>
    private static string FreeName(Document document, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "Credential";
        string candidate = name;
        for (int suffix = 2; document.Items.Any(item =>
                 string.Equals(item.Name, candidate, StringComparison.OrdinalIgnoreCase)); suffix++)
            candidate = name + " (" + suffix + ")";
        return candidate;
    }

    /// <summary>Written beside the target and moved over it: a reader that catches this file
    /// half-written would lose every entry it holds.</summary>
    private static bool Save(Document document)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            string temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(document, Options));
            File.Move(temp, Path, overwrite: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    // ---- protection ----

    /// <summary>Protects a value for this Windows user. Null when it cannot be done, which is what
    /// a profile without DPAPI keys (or a different account) looks like.</summary>
    public static byte[]? Protect(string value)
    {
        byte[] data = Encoding.UTF8.GetBytes(value);
        var input = new DataBlob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        var output = new DataBlob();
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            if (!CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output))
                return null;
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    /// <summary>Opens a blob protected by this user. Null when it belongs to another account or
    /// the machine.</summary>
    public static string? Unprotect(byte[] blob)
    {
        var input = new DataBlob { Size = blob.Length, Data = Marshal.AllocHGlobal(blob.Length) };
        var output = new DataBlob();
        try
        {
            Marshal.Copy(blob, 0, input.Data, blob.Length);
            // UI forbidden: this runs behind a service, where a prompt would hang forever.
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output))
                return null;
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return Encoding.UTF8.GetString(result);
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    private const int UiForbidden = 0x1;

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
