using System.Runtime.InteropServices;
using System.Text;

namespace PolyMount.Plugins.Svn;

/// <summary>A username and password to present to a repository.</summary>
internal sealed record SvnCredential(string Username, string Password);

/// <summary>
/// Where the credentials for a repository come from, now that the command-line client is gone.
///
/// The command line brought one thing the HTTP transport could not: it read the credentials the
/// Subversion client had cached for this user. That cache is still there and is readable — a small
/// key/value file per realm under <c>%APPDATA%\Subversion\auth\svn.simple</c>, with the password
/// stored as base64 of a DPAPI blob. This class reads it directly, so a mount keeps working the
/// way it did without a process in between.
///
/// What the layer is configured with wins over the cache: an operator who typed a username means
/// it. The cache is then tried, and finally anonymous, which is enough for a repository that
/// serves its content without asking.
/// </summary>
internal static class SvnCredentials
{
    /// <summary>The credential for <paramref name="url"/>, or null to try anonymously.</summary>
    public static SvnCredential? Resolve(string url, string? username, string? password)
    {
        if (!string.IsNullOrWhiteSpace(username)) return new SvnCredential(username, password ?? string.Empty);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var repository)) return null;
        return FromCache(repository);
    }

    /// <summary>
    /// The cached credential for a repository, matched by host and port. Several realms can share
    /// a host; a realm that names the repository's own path is preferred, and otherwise the most
    /// recently written file wins, which is the one a person would have just authenticated with.
    /// </summary>
    internal static SvnCredential? FromCache(Uri repository) => FromCache(repository, CacheDirectory(), Unprotect);

    /// <summary>The same lookup over a given directory and decryptor, so the whole path — the file
    /// format, the base64 blob and the realm match — can be exercised without a real credential
    /// store.</summary>
    internal static SvnCredential? FromCache(Uri repository, string directory, Func<byte[], string?> unprotect)
    {
        if (!Directory.Exists(directory)) return null;

        SvnCredential? best = null;
        int bestScore = 0;
        DateTime bestWritten = DateTime.MinValue;

        string[] files;
        try { files = Directory.GetFiles(directory); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        foreach (string file in files)
        {
            Dictionary<string, byte[]>? values;
            try { values = Read(file); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if (values == null) continue;
            if (!values.TryGetValue("svn:realmstring", out var realmBytes)) continue;
            if (!values.TryGetValue("username", out var userBytes)) continue;
            if (!values.TryGetValue("password", out var passwordBytes)) continue;
            // Only the Windows store holds a password this process can open. A realm cached by a
            // build with a different store is passed over rather than reported: another entry for
            // the same host may still work.
            if (!values.TryGetValue("passtype", out var typeBytes)) continue;
            if (!string.Equals(Encoding.ASCII.GetString(typeBytes), "wincrypt", StringComparison.OrdinalIgnoreCase)) continue;

            string realm = Encoding.UTF8.GetString(realmBytes);
            if (!RealmMatches(realm, repository)) continue;

            byte[] blob;
            try { blob = Convert.FromBase64String(Encoding.ASCII.GetString(passwordBytes)); }
            catch (FormatException) { continue; }
            string? password = unprotect(blob);
            if (password == null) continue;

            int score = realm.Contains(repository.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            DateTime written;
            try { written = File.GetLastWriteTimeUtc(file); }
            catch (IOException) { written = DateTime.MinValue; }

            if (score > bestScore || (score == bestScore && written > bestWritten))
            {
                best = new SvnCredential(Encoding.UTF8.GetString(userBytes), password);
                bestScore = score;
                bestWritten = written;
            }
        }
        return best;
    }

    /// <summary>Whether a realm belongs to a repository. The cache key is the whole
    /// <c>&lt;scheme://host:port&gt;</c> prefix the client builds, followed by the server's realm
    /// text: the prefix is parsed and compared field by field, never searched for as a substring,
    /// because a host name that merely appears inside another one — or the same host over a
    /// different scheme — would otherwise be handed credentials meant for someone else.</summary>
    internal static bool RealmMatches(string realm, Uri repository)
    {
        if (!realm.StartsWith('<')) return false;
        int close = realm.IndexOf('>');
        if (close < 4) return false;
        if (!Uri.TryCreate(realm[1..close], UriKind.Absolute, out var origin)) return false;
        return string.Equals(origin.Scheme, repository.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(origin.Host, repository.Host, StringComparison.OrdinalIgnoreCase)
            && origin.Port == repository.Port;
    }

    /// <summary>The Subversion credential store for the current user.</summary>
    internal static string CacheDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Subversion", "auth", "svn.simple");

    /// <summary>
    /// Parses one cache file: repeated <c>K &lt;length&gt;\n&lt;key&gt;\nV &lt;length&gt;\n&lt;value&gt;\n</c>
    /// pairs, lengths in bytes. Returns null for anything that is not that shape, because a file
    /// half-written by another program is not a credential.
    /// </summary>
    internal static Dictionary<string, byte[]>? Read(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        string? key = null;
        bool terminated = false;
        int i = 0;
        while (i < data.Length)
        {
            int lineEnd = Array.IndexOf(data, (byte)'\n', i);
            if (lineEnd < 0) break;
            string line = Encoding.ASCII.GetString(data, i, lineEnd - i);
            i = lineEnd + 1;
            if (line.Length == 0) continue;
            // Subversion terminates the document with END, and its absence is how a file that was
            // cut short looks. Requiring it is what turns "the last pair happened to end at the
            // end of the file" into a rejected file.
            if (line == "END") { terminated = true; break; }
            int space = line.IndexOf(' ');
            if (space != 1 || !int.TryParse(line.AsSpan(2), out int length) || length < 0) return null;
            if (i + length > data.Length) return null;
            var value = data.AsSpan(i, length).ToArray();
            i += length + 1;   // the value is followed by a newline
            if (line[0] == 'K') key = Encoding.UTF8.GetString(value);
            else if (line[0] == 'V' && key != null) values[key] = value;
        }
        return terminated ? values : null;
    }

    /// <summary>Opens a DPAPI blob protected by this user. Null when it belongs to someone else or
    /// the machine, which is what a service running under a different account will see.</summary>
    internal static string? Unprotect(byte[] blob)
    {
        var input = new DataBlob { Size = blob.Length, Data = Marshal.AllocHGlobal(blob.Length) };
        var output = new DataBlob();
        try
        {
            Marshal.Copy(blob, 0, input.Data, blob.Length);
            // UI forbidden: this runs in a service, where a prompt would hang forever.
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output))
                return null;
            var bytes = new byte[output.Size];
            Marshal.Copy(output.Data, bytes, 0, output.Size);
            return Encoding.UTF8.GetString(bytes);
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
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
