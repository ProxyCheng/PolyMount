using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PolyMount.Core;

/// <summary>A username and a secret to present to one origin.</summary>
public sealed record Credential(string Username, string Secret);

/// <summary>
/// Credentials the user entered in the product, kept for every source that needs them.
///
/// Keyed by origin — <c>scheme://host[:port]</c> — rather than by source type, because what a
/// credential belongs to is a server, not a protocol: an SVN repository, a Git remote and an SSH
/// host are all "somebody signed in here". A plugin only has to hand over a URL.
///
/// The file holds no secret in the clear: each entry is a DPAPI blob protected for the current
/// Windows user, so another account on the same machine cannot read it and a copy taken off the
/// disk is useless. That scope is also the limitation: a service running as LocalService cannot
/// open it, which is the same reason it cannot read the Subversion cache.
/// </summary>
public static class CredentialStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Where the entries live: with the configuration and the stores, under the common
    /// application data folder the installer has already given the service account. Not under the
    /// tray's own settings, because the writer is the service — the one account that both the
    /// supervisor and its workers share — and DPAPI here is scoped to that account, so a service
    /// install works whatever account it runs as.</summary>
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PolyMount", "credentials.json");

    /// <summary>The key a credential is stored under: scheme and host in lower case, and a port
    /// only when it is not the scheme's default, so <c>https://host</c> and <c>https://host:443</c>
    /// are one key and <c>http://host</c> is another.</summary>
    public static string OriginOf(Uri uri)
    {
        string host = uri.IdnHost.ToLowerInvariant();
        if (host.Contains(':') && !host.StartsWith('[')) host = "[" + host + "]";
        return uri.Scheme.ToLowerInvariant() + "://" + host + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port);
    }

    public static Credential? Find(string origin)
    {
        lock (Gate)
        {
            if (Read() is not { } entries) return null;
            if (!entries.TryGetValue(origin, out var blob)) return null;
            try { return Decode(Convert.FromBase64String(blob)); }
            catch (FormatException) { return null; }
        }
    }

    /// <summary>Stores one entry, and says whether it was stored: a sign-in that could not be
    /// kept must not report success, or the reader is told to try again by the same prompt.</summary>
    public static bool Save(string origin, Credential credential)
    {
        lock (Gate)
        {
            var entries = Read() ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // A value that cannot be protected is not written at all: storing it in the clear is
            // the one thing this file must never do.
            if (Protect(JsonSerializer.Serialize(new Stored(credential.Username, credential.Secret))) is not { } protected_)
                return false;
            entries[origin] = Convert.ToBase64String(protected_);
            return Write(entries);
        }
    }

    public static void Remove(string origin)
    {
        lock (Gate)
        {
            if (Read() is not { } entries || !entries.Remove(origin)) return;
            Write(entries);
        }
    }

    private sealed record Stored(string Username, string Secret);

    private static Dictionary<string, string>? Read()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path))
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    /// <summary>Written beside the target and moved over it: a reader that catches this file
    /// half-written would lose every entry it holds.</summary>
    private static bool Write(Dictionary<string, string> entries)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            string temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries, Options));
            File.Move(temp, Path, overwrite: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static Credential? Decode(byte[] blob)
    {
        string? json = Unprotect(blob);
        if (json == null) return null;
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(json);
            return stored == null ? null : new Credential(stored.Username, stored.Secret);
        }
        catch (JsonException) { return null; }
    }

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
    /// the machine — the LocalService case.</summary>
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
