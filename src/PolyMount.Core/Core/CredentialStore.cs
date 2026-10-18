using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PolyMount.Core;

/// <summary>A username and a secret to present to one origin.</summary>
public sealed record Credential(string Username, string Secret);

/// <summary>What a credential looks like to the interface: everything except the secret.</summary>
public sealed record CredentialRecord(string Id, string Name, string Username);

/// <summary>An account the product remembered for one origin, shown so it can be deleted.</summary>
public sealed record OriginRecord(string Origin, string Username);

/// <summary>
/// The accounts this product keeps: named ones the reader manages and picks per layer, and the
/// ones it remembers by origin on its own.
///
/// A named credential belongs to a person, not to a server — the same account usually opens several
/// hosts — so a layer names the credential it uses instead of the credentials being matched by
/// address. The origin-keyed entries remain for the automatic path, and for what earlier versions
/// wrote.
///
/// The file holds no secret in the clear: each one is a DPAPI blob protected for the current
/// Windows user. The writer is the service, the one account both the supervisor and its workers
/// share, so an installation works whatever account it runs as; the tray cannot read or write it
/// and asks over the control pipe instead.
/// </summary>
public static class CredentialStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private const string DefaultKey = "default";

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
        lock (Gate) return Load().Items.Select(item => new CredentialRecord(item.Id, item.Name, item.Username)).ToList();
    }

    /// <summary>Adds a named credential and returns the record the interface can show.</summary>
    public static CredentialRecord Add(string name, string username, string secret)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("a credential needs a name");
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("a credential needs a user name");
        if (Protect(secret) is not { } protected_) throw new InvalidOperationException("the secret could not be protected for this service account");
        lock (Gate)
        {
            var document = Load();
            var item = new StoredCredential
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name.Trim(),
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
        lock (Gate)
        {
            var document = Load();
            var item = document.Items.FirstOrDefault(entry => entry.Id == id)
                ?? throw new ArgumentException("no such credential: " + id);
            item.Name = name.Trim();
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

    // ---- automatic entries, by origin ----

    /// <summary>The accounts remembered by origin, for the manager's read-only group.</summary>
    public static List<OriginRecord> Origins()
    {
        lock (Gate)
        {
            var result = new List<OriginRecord>();
            foreach (var (origin, blob) in Load().Origins)
            {
                try
                {
                    if (Unprotect(Convert.FromBase64String(blob)) is { } json &&
                        JsonSerializer.Deserialize<Stored>(json) is { } stored)
                        result.Add(new OriginRecord(origin, stored.Username));
                }
                catch (FormatException) { }
                catch (JsonException) { }
            }
            result.Sort((a, b) => string.Compare(a.Origin, b.Origin, StringComparison.OrdinalIgnoreCase));
            return result;
        }
    }

    public static Credential? FindByOrigin(string origin)
    {
        lock (Gate)
        {
            if (!Load().Origins.TryGetValue(origin, out var blob)) return null;
            try { return Decode(Convert.FromBase64String(blob)); }
            catch (FormatException) { return null; }
        }
    }

    /// <summary>Stores one automatic entry, and says whether it was stored.</summary>
    public static bool Save(string origin, Credential credential)
    {
        lock (Gate)
        {
            var document = Load();
            if (Encode(credential) is not { } blob) return false;
            document.Origins[origin] = blob;
            return Save(document);
        }
    }

    public static void Remove(string origin)
    {
        lock (Gate)
        {
            var document = Load();
            if (!document.Origins.Remove(origin)) return;
            Save(document);
        }
    }

    public static bool SaveDefault(Credential credential)
    {
        lock (Gate)
        {
            var document = Load();
            if (Encode(credential) is not { } blob) return false;
            document.Default = blob;
            return Save(document);
        }
    }

    public static Credential? FindDefault()
    {
        lock (Gate)
        {
            if (Load().Default is not { } blob) return null;
            try { return Decode(Convert.FromBase64String(blob)); }
            catch (FormatException) { return null; }
        }
    }

    // ---- the document ----

    private sealed class Document
    {
        public List<StoredCredential> Items { get; set; } = new();
        public Dictionary<string, string> Origins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Default { get; set; }
    }

    private sealed class StoredCredential
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Username { get; set; } = "";
        public string Secret { get; set; } = "";
    }

    /// <summary>What an automatic entry holds once opened: the pair, protected together.</summary>
    private sealed record Stored(string Username, string Secret);

    private static string? Encode(Credential credential)
    {
        if (Protect(JsonSerializer.Serialize(new Stored(credential.Username, credential.Secret))) is not { } protected_) return null;
        return Convert.ToBase64String(protected_);
    }

    private static Credential? Decode(byte[] blob)
    {
        if (Unprotect(blob) is not { } json) return null;
        try
        {
            return JsonSerializer.Deserialize<Stored>(json) is { } stored ? new Credential(stored.Username, stored.Secret) : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Reads the document, migrating the flat <c>origin → blob</c> map earlier versions
    /// wrote into its own section so no account is lost.</summary>
    private static Document Load()
    {
        try
        {
            if (!File.Exists(Path)) return new Document();
            string text = File.ReadAllText(Path);
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new Document();
            bool shaped = root.TryGetProperty("items", out _) || root.TryGetProperty("origins", out _) || root.TryGetProperty("default", out _);
            if (shaped) return JsonSerializer.Deserialize<Document>(text) ?? new Document();
            var legacy = JsonSerializer.Deserialize<Dictionary<string, string>>(text) ?? new();
            return new Document { Origins = new Dictionary<string, string>(legacy, StringComparer.OrdinalIgnoreCase) };
        }
        catch (IOException) { return new Document(); }
        catch (UnauthorizedAccessException) { return new Document(); }
        catch (JsonException) { return new Document(); }
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
