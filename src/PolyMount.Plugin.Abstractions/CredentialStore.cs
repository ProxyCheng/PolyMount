using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PolyMount.Plugins;

/// <summary>The kinds of credential the product ships, and the type each one builds. Which of them
/// a layer offers is the layer's own answer (see <see cref="CredentialConsumers"/>).</summary>
public static class CredentialTypes
{
    public const string Password = "password";
    public const string Certificate = "certificate";

    /// <summary>The kinds the product ships.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Password, Certificate };

    /// <summary>Whether a record of this kind can be read: one of the two above, or one a loaded
    /// plugin describes (see <see cref="CredentialKinds"/>).</summary>
    public static bool IsKnown(string kind) => CredentialKinds.IsKnown(kind);
}

/// <summary>
/// What every credential is: something the store can name, choose and show.
///
/// It deliberately says nothing about *how* the credential proves who it is. That used to live
/// here — an account and a password on the base class — which made every other way of
/// authenticating look like a password with extra fields, and made every consumer ask "which
/// concrete type is this?" before it could use one. A credential now declares what it can do by
/// implementing one or more of the interfaces below, and a consumer asks for the capability it
/// needs (<c>credential as IBearerAuthentication</c>) instead of for a class.
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
}

/// <summary>Authenticates by an account and the secret that proves it (HTTP Basic, an SMB logon,
/// an SVN repository that asks for a user).</summary>
public interface IPasswordAuthentication
{
    /// <summary>The account the server asks for. May be empty when the protocol supplies it.</summary>
    string UserName { get; }
    string Password { get; }
}

/// <summary>Authenticates by presenting a client certificate (mutual TLS).</summary>
public interface ICertificateAuthentication
{
    /// <summary>The PKCS#12 file and the password that opens it.</summary>
    byte[] Certificate { get; }
    string CertificatePassword { get; }
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

/// <summary>A PKCS#12 file for mutual TLS, the password that opens it, and the account the
/// server may ask for as well. The account is optional: some servers authenticate on the
/// certificate alone, and the ones that ask for both get the pair through
/// <see cref="IPasswordAuthentication"/> — that is why this class implements both.</summary>
public sealed class ClientCertificateCredential : Credential, ICertificateAuthentication, IPasswordAuthentication
{
    public override string Kind => CredentialTypes.Certificate;
    byte[] ICertificateAuthentication.Certificate => Certificate;
    string ICertificateAuthentication.CertificatePassword => CertificatePassword;
    public byte[] Certificate { get; init; } = Array.Empty<byte>();
    public string CertificatePassword { get; init; } = "";
    string IPasswordAuthentication.UserName => Username;
    /// <summary>The account's password, when the certificate names an account as well. Empty for
    /// a server that authenticates on the certificate alone.</summary>
    public string Password { get; init; } = "";
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
}

/// <summary>What the interface asks the store to save. A null password, certificate or
/// certificate password keeps what the record already holds, so a form that shows only the
/// fields a person changed does not have to read a secret back.</summary>
/// <param name="Values">The fields of a plugin kind (<see cref="CredentialKindDescriptor.Fields"/>),
/// keyed by their paths. An empty value on an edit keeps the stored one, for the same reason a
/// null password does. Unused by the two kinds the product ships.</param>
public sealed record CredentialInput(
    string? Id, string Name, string Kind, string Username,
    string? Password, byte[]? Certificate, string? CertificatePassword,
    IReadOnlyList<string>? Origins = null,
    IReadOnlyDictionary<string, string>? Values = null);

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
        lock (Gate) return Load().Items
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new CredentialRecord(item.Id, item.Name, item.Username, KindOf(item), IsUsable(item),
                item.Origins.ToArray())
            {
                UseFor = Consumers(item)
            })
            .ToList();
    }

    /// <summary>Which layer types would present a record, for the list. The secret is opened once
    /// more here than <see cref="IsUsable"/> opens it — a list is a handful of records, and a
    /// single pass that answered both questions would have to carry the reason for each one.
    /// A record that cannot be opened, or whose kind has no loaded code, has no answer: empty,
    /// rather than an exception thrown into a window that is drawing a list.</summary>
    private static IReadOnlyList<string> Consumers(StoredCredential item)
    {
        try
        {
            return Materialize(item) is { } credential
                ? CredentialConsumers.Of(credential)
                : Array.Empty<string>();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Whether this account can actually use a record: the secret is a blob it can
    /// open, and a certificate credential still names a certificate. The layer editor shows the
    /// answer where the credential is chosen, before a mount has to fail to say so.</summary>
    private static bool IsUsable(StoredCredential item)
    {
        string? plain = UnprotectSecret(item);
        if (plain == null) return false;
        string kind = KindOf(item);
        if (string.Equals(kind, CredentialTypes.Password, StringComparison.OrdinalIgnoreCase))
            return plain.Length > 0;
        if (string.Equals(kind, CredentialTypes.Certificate, StringComparison.OrdinalIgnoreCase))
            return ParseCertificate(plain) is { Certificate.Length: > 0 };
        // A plugin kind: the code that builds the credential decides. A record whose plugin is not
        // loaded now has nothing to build it, and the manager shows it as unusable rather than as
        // missing — the record is intact, what is gone is the description of it.
        var descriptor = CredentialKinds.Find(kind);
        if (descriptor == null) return false;
        try { return descriptor.Create(ParseValues(plain)) != null; }
        catch (Exception) { return false; }
    }

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
            var descriptor = CredentialKinds.Find(kind);
            var current = keeps && certificate ? CertificateOf(item!) : null;
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
                    Account = input.Password ?? current?.Account ?? string.Empty,
                    Certificate = certificateBytes,
                    Password = certificatePassword
                }, Options);
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
                : descriptor?.Kind ?? CredentialTypes.Certificate;
            item.Secret = Convert.ToBase64String(protected_);
            if (added) document.Items.Add(item);
            if (!Save(document)) throw new InvalidOperationException("the credential could not be written");
            return new CredentialRecord(item.Id, item.Name, item.Username, kind);
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
            var item = Load().Items.FirstOrDefault(entry => entry.Id == id);
            return item == null ? null : Materialize(item);
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
    public static Credential? Use(string id, string origin)
    {
        lock (Gate)
        {
            var document = Load();
            var item = document.Items.FirstOrDefault(entry => entry.Id == id);
            if (item == null) return null;
            if (BindsOrigins(KindOf(item)))
            {
                if (item.Origins.Count == 0)
                {
                    item.Origins.Add(origin);
                    Save(document);
                }
                else if (!item.Origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
                {
                    throw new CredentialOriginException(item.Id, item.Name, item.Origins.ToArray(), origin);
                }
            }
            return Materialize(item);
        }
    }

    /// <summary>The secret behind a named credential, checked against the origin it is about to be
    /// presented at — the same rule <see cref="Use"/> applies — without recording the origin.
    ///
    /// It exists for a caller that is asking a question rather than mounting: <see cref="Use"/>
    /// writes the first origin down, which is right when a layer is built and wrong when somebody
    /// is trying an account against an address they may still be editing. A form that asked "does
    /// this work?" would otherwise leave a credential bound to a host nobody meant.
    /// </summary>
    public static Credential? Check(string id, string origin)
    {
        lock (Gate)
        {
            var item = Load().Items.FirstOrDefault(entry => entry.Id == id);
            if (item == null) return null;
            if (BindsOrigins(KindOf(item)) && item.Origins.Count > 0 &&
                !item.Origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
                throw new CredentialOriginException(item.Id, item.Name, item.Origins.ToArray(), origin);
            return Materialize(item);
        }
    }

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
        var descriptor = CredentialKinds.Find(kind);
        // The two kinds the product ships have no descriptor here — a password record is the shape
        // every file written before kinds existed has — and an unknown kind is treated as one that
        // might hold a secret.
        if (descriptor == null) return true;
        foreach (var field in descriptor.Fields)
            if (field.Kind == CredentialFieldKind.Secret) return true;
        return false;
    }

    /// <summary>The credential a stored record stands for, with its secret opened. A record whose
    /// secret cannot be opened — written by another Windows account, or damaged — is null, which
    /// the caller reports as an account that no longer works.</summary>
    private static Credential? Materialize(StoredCredential item)
    {
        string? plain = UnprotectSecret(item);
        if (plain == null) return null;
        string kind = KindOf(item);
        if (string.Equals(kind, CredentialTypes.Password, StringComparison.OrdinalIgnoreCase))
            return new PasswordCredential { Username = item.Username, Password = plain };

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
                Password = payload.Account,
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

    /// <summary>What a certificate credential holds, protected as one blob: the account password
    /// (which may be empty), the PKCS#12 bytes in base64 and the password that opens them.</summary>
    private sealed class CertificateSecret
    {
        public string Account { get; set; } = "";
        public string Certificate { get; set; } = "";
        public string Password { get; set; } = "";
    }

    private static CertificateSecret? CertificateOf(StoredCredential item) =>
        UnprotectSecret(item) is { } json ? ParseCertificate(json) : null;

    private static CertificateSecret? ParseCertificate(string json)
    {
        try { return JsonSerializer.Deserialize<CertificateSecret>(json, Options); }
        catch (JsonException) { return null; }
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
