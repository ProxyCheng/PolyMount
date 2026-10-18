using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PolyMount.Core;

/// <summary>A username and a secret to present to one origin.</summary>
public sealed record Credential(string Username, string Secret);

/// <summary>What a credential looks like to the interface: everything except the secret.</summary>
public sealed record CredentialRecord(string Id, string Name, string Username);

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
            .Select(item => new CredentialRecord(item.Id, item.Name, item.Username))
            .ToList();
    }

    /// <summary>Adds a named credential and returns the record the interface can show. Names are
    /// what the picker shows, so they are unique: two rows reading the same thing would be a list
    /// with no way to tell which one a layer means.</summary>
    public static CredentialRecord Add(string name, string username, string secret)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("a credential needs a name");
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("a credential needs a user name");
        if (Protect(secret) is not { } protected_) throw new InvalidOperationException("the secret could not be protected for this service account");
        name = name.Trim();
        lock (Gate)
        {
            var document = Load();
            if (document.Items.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("a credential named '" + name + "' already exists");
            var item = new StoredCredential
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                Username = username.Trim(),
                Secret = Convert.ToBase64String(protected_)
            };
            document.Items.Add(item);
            if (!Save(document)) throw new InvalidOperationException("the credential could not be written");
            return new CredentialRecord(item.Id, item.Name, item.Username);
        }
    }

    /// <summary>Changes a named credential. A null secret keeps the one already stored, so the
    /// interface never has to read a password back to save the rest of the record.</summary>
    public static void Update(string id, string name, string username, string? secret)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("a credential needs a name");
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("a credential needs a user name");
        byte[]? protected_ = null;
        if (secret != null)
            protected_ = Protect(secret) ?? throw new InvalidOperationException("the secret could not be protected for this service account");
        name = name.Trim();
        lock (Gate)
        {
            var document = Load();
            var item = document.Items.FirstOrDefault(entry => entry.Id == id)
                ?? throw new ArgumentException("no such credential: " + id);
            if (document.Items.Any(entry => entry.Id != id &&
                    string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("a credential named '" + name + "' already exists");
            item.Name = name;
            item.Username = username.Trim();
            if (protected_ != null) item.Secret = Convert.ToBase64String(protected_);
            if (!Save(document)) throw new InvalidOperationException("the credential could not be written");
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

    /// <summary>The secret behind a named credential, for a layer that chose it.</summary>
    public static Credential? FindById(string id)
    {
        lock (Gate)
        {
            var item = Load().Items.FirstOrDefault(entry => entry.Id == id);
            if (item == null) return null;
            try { return Unprotect(Convert.FromBase64String(item.Secret)) is { } secret ? new Credential(item.Username, secret) : null; }
            catch (FormatException) { return null; }
        }
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
        public string Secret { get; set; } = "";
    }

    /// <summary>What an older entry held once opened: the pair, protected together.</summary>
    private sealed record Stored(string Username, string Secret);

    private static Credential? Decode(byte[] blob)
    {
        if (Unprotect(blob) is not { } json) return null;
        try
        {
            return JsonSerializer.Deserialize<Stored>(json) is { } stored ? new Credential(stored.Username, stored.Secret) : null;
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
                if (SecretOf(existing) == credential.Secret) return true;
            }
            if (Protect(credential.Secret) is not { } secret) return false;
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

    private static string? SecretOf(StoredCredential item)
    {
        try { return Unprotect(Convert.FromBase64String(item.Secret)); }
        catch (FormatException) { return null; }
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
