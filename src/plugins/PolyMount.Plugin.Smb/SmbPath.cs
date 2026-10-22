namespace PolyMount.Plugins.Smb;

/// <summary>
/// A folder on another machine, named the way Windows names it: <c>\\server\share[\folder...]</c>.
///
/// The share is kept apart from the folder inside it because only the share is something an SMB
/// session can be established against — <c>WNetAddConnection2</c> connects to a share, not to a
/// directory — while the rest is a path this layer composes and confines itself to.
/// </summary>
public sealed class SmbPath
{
    private SmbPath(string server, string share, string folder)
    {
        Server = server;
        Share = share;
        Folder = folder;
    }

    public string Server { get; }
    public string Share { get; }

    /// <summary>The folder inside the share: backslash-separated, no leading or trailing one,
    /// empty when the layer maps the whole share.</summary>
    public string Folder { get; }

    /// <summary>What the SMB session is established against.</summary>
    public string ShareRoot => @"\\" + Server + @"\" + Share;

    /// <summary>The directory the layer maps.</summary>
    public string Root => Folder.Length == 0 ? ShareRoot : ShareRoot + @"\" + Folder;

    public override string ToString() => Root;

    /// <summary>
    /// Reads a configured path. Anything that is not a share path is refused here rather than at
    /// the first read: a local path belongs to the <c>dir</c> source, and a half-understood path
    /// in this layer would answer "not found" for a folder that exists.
    /// </summary>
    public static SmbPath Parse(string? path)
    {
        string text = (path ?? string.Empty).Trim();
        if (text.Length == 0)
            throw new ArgumentException(@"a remote folder needs a path such as \\server\share\folder");
        if (text.StartsWith(@"\\?\", StringComparison.Ordinal) || text.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException(@"'" + text + @"' is a device path, not a share; name the folder as \\server\share\folder");

        text = text.Replace('/', '\\');
        if (!text.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("'" + text + @"' is not a folder on another machine; a remote folder is named \\server\share\folder (a local folder uses the 'dir' source)");

        string[] parts = text[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new ArgumentException("'" + text + @"' names no share; a remote folder is named \\server\share\folder");

        for (int i = 0; i < Math.Min(2, parts.Length); i++) Validate(parts[i], text);
        for (int i = 2; i < parts.Length; i++) Validate(parts[i], text);

        return new SmbPath(parts[0], parts[1], string.Join('\\', parts[2..]));
    }

    /// <summary>Refuses what Windows would read as something other than a name. <c>..</c> is
    /// refused in the folder part for the same reason the layer refuses it per request: the
    /// redirector resolves it before the server sees it, so it would name a folder above the one
    /// this layer was configured to map.</summary>
    private static void Validate(string segment, string path)
    {
        if (segment is "." or "..")
            throw new ArgumentException("a remote folder cannot walk upwards: '" + path + "'");
        foreach (char c in segment)
            if (c < 0x20 || c is '<' or '>' or ':' or '"' or '|' or '?' or '*')
                throw new ArgumentException("'" + path + "' holds a character Windows does not allow in a folder name: '" + c + "'");
    }
}
