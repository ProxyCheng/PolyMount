using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PolyMount.Core;

/// <summary>The kinds of credential the product ships, and the type each one builds. A place
/// that uses credentials names the kinds it accepts (see <see
/// cref="LayerFieldDescriptor.CredentialKinds"/>), and the editor offers those.</summary>
public static class CredentialTypes
{
    public const string Password = "password";
    public const string Certificate = "certificate";
    public static IReadOnlyList<string> All { get; } = new[] { Password, Certificate };
}

/// <summary>
/// What every credential is. The store keeps the concrete subclasses and a consumer names the
/// type it accepts, so any subclass that fits can be chosen for it: the SVN source takes a
/// password credential, a certificate credential, or anything added later that still answers the
/// account question.
/// </summary>
public abstract class Credential
{
    /// <summary>The stable discriminator stored beside the secret and shown to the interface.</summary>
    public abstract string Kind { get; }

    /// <summary>The account a server asks for, when this credential names one.</summary>
    public string Username { get; init; } = "";

    /// <summary>The account's password, when this credential names an account.</summary>
    public string Password { get; init; } = "";
}

/// <summary>An account and its password — the ordinary credential.</summary>
public sealed class PasswordCredential : Credential
{
    public override string Kind => CredentialTypes.Password;
}

/// <summary>A PKCS#12 file for mutual TLS, the password that opens it, and the account the
/// server may ask for as well. The account is optional: some servers authenticate on the
/// certificate alone.</summary>
public sealed class ClientCertificateCredential : Credential
{
    public override string Kind => CredentialTypes.Certificate;
    public byte[] Certificate { get; init; } = Array.Empty<byte>();
    public string CertificatePassword { get; init; } = "";
}

/// <summary>What a credential looks like to the interface: everything except the secret.
/// <see cref="Usable"/> is false when this account cannot open the stored secret — a record
/// written by another Windows account, or one whose payload no longer parses — which is what
/// the layer editor warns about where the credential is chosen.</summary>
public sealed record CredentialRecord(string Id, string Name, string Username,
    string Kind = CredentialTypes.Password, bool Usable = true);

/// <summary>What the interface asks the store to save. A null password, certificate or
/// certificate password keeps what the record already holds, so a form that shows only the
/// fields a person changed does not have to read a secret back.</summary>
public sealed record CredentialInput(
    string? Id, string Name, string Kind, string Username,
    string? Password, byte[]? Certificate, string? CertificatePassword);

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
    /// application data folder the installer has already given the service account.</summary>
    public static string Path { get; } = System.IO.Path.Combine(
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
            .Select(item => new CredentialRecord(item.Id, item.Name, item.Username, KindOf(item), IsUsable(item)))
            .ToList();
    }

    /// <summary>Whether this account can actually use a record: the secret is a blob it can
    /// open, and a certificate credential still names a certificate. The layer editor shows the
    /// answer where the credential is chosen, before a mount has to fail to say so.</summary>
    private static bool IsUsable(StoredCredential item)
    {
        string? plain = UnprotectSecret(item);
        if (plain == null) return false;
        if (KindOf(item) == CredentialTypes.Password) return plain.Length > 0;
        return ParseCertificate(plain) is { Certificate.Length: > 0 };
    }

    /// <summary>Adds or changes a named credential and returns the record the interface can show.
    /// Names are what the picker shows, so they are unique: two rows reading the same thing would
    /// be a list with no way to tell which one a layer means.</summary>
    public static CredentialRecord Save(CredentialInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) throw new ArgumentException("a credential needs a name");
        if (!CredentialTypes.All.Contains(input.Kind))
            throw new ArgumentException("unknown credential kind '" + input.Kind + "'");
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
            bool keeps = item != null && KindOf(item) == input.Kind;
            var current = keeps && input.Kind == CredentialTypes.Certificate ? CertificateOf(item!) : null;
            string secret;
            if (input.Kind == CredentialTypes.Certificate)
            {
                string certificate = input.Certificate is { Length: > 0 } bytes
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
                    Certificate = certificate,
                    Password = certificatePassword
                }, Options);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(input.Username))
                    throw new ArgumentException("a credential needs a user name");
                secret = input.Password
                    ?? (keeps ? UnprotectSecret(item!) : null)
                    ?? throw new ArgumentException("a credential needs a password");
            }

            if (Protect(secret) is not { } protected_)
                throw new InvalidOperationException("the secret could not be protected for this service account");

            bool added = item == null;
            item ??= new StoredCredential { Id = Guid.NewGuid().ToString("N") };
            item.Name = name;
            item.Username = input.Username.Trim();
            item.Kind = input.Kind == CredentialTypes.Certificate ? CredentialTypes.Certificate : "";
            item.Secret = Convert.ToBase64String(protected_);
            if (added) document.Items.Add(item);
            if (!Save(document)) throw new InvalidOperationException("the credential could not be written");
            return new CredentialRecord(item.Id, item.Name, item.Username, input.Kind);
        }
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

    /// <summary>The secret behind a named credential, for a layer that chose it. The result is
    /// the concrete subclass the record was saved as, which is what lets a consumer name the
    /// type it accepts and be handed whichever subclass fits.</summary>
    public static Credential? FindById(string id)
    {
        lock (Gate)
        {
            var item = Load().Items.FirstOrDefault(entry => entry.Id == id);
            if (item == null) return null;
            string? plain = UnprotectSecret(item);
            if (plain == null) return null;
            if (KindOf(item) == CredentialTypes.Password)
                return new PasswordCredential { Username = item.Username, Password = plain };

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
    }

    /// <summary>The kind a stored record is. Files written before kinds existed are passwords,
    /// which is also what an unrecognised value means: a secret with no certificate is one.</summary>
    private static string KindOf(StoredCredential item) =>
        string.Equals(item.Kind, CredentialTypes.Certificate, StringComparison.OrdinalIgnoreCase)
            ? CredentialTypes.Certificate : CredentialTypes.Password;

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
    }

    /// <summary>What an older entry held once opened: the pair, protected together.</summary>
    private sealed record Stored(string Username, string Secret);

    private static Credential? Decode(byte[] blob)
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
