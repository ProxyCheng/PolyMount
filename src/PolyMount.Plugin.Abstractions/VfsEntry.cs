namespace PolyMount.Plugins;

public sealed class VfsEntry
{
    public string Name { get; init; } = string.Empty;
    public bool IsDir { get; init; }
    public long Size { get; init; }
    public DateTime TimeUtc { get; init; }
    /// <summary>An opaque identity for this path's content, supplied by whichever layer
    /// resolved it, or null when that layer cannot name one. It is the source's own answer to
    /// "is this still the same bytes at this path" — SVN's last-changed revision, another
    /// backend's etag — and caches compare it without interpreting it. A source that cannot
    /// name a version leaves it null and the cache falls back to invalidating by generation,
    /// so nothing here assumes a particular backend exists.</summary>
    public string? ContentVersion { get; init; }
    /// <summary>Whether this name carries a reparse point (a symbolic link, a junction). The
    /// resolver sets it from the writable layer's store; the mount reports it as
    /// FILE_ATTRIBUTE_REPARSE_POINT, which is how Windows knows to resolve the name at all.</summary>
    public bool IsReparsePoint { get; set; }
    /// <summary>The reparse tag of that point (0 when there is none), reported alongside the
    /// attribute so the system can tell a name surrogate from any other kind.</summary>
    public uint ReparseTag { get; set; }
}

public sealed class VfsNotFoundException : Exception
{
    public VfsNotFoundException(string rel) : base("not found: " + rel) => Rel = rel;
    public string Rel { get; }
}

/// <summary>A directory that still lists something was asked to go away.</summary>
public sealed class VfsDirectoryNotEmptyException(string rel) : IOException("directory not empty: " + rel)
{
    public string Rel { get; } = rel;
}

/// <summary>A name that already exists was asked for without permission to replace it.</summary>
public sealed class VfsNameCollisionException(string rel) : IOException("destination exists: " + rel)
{
    public string Rel { get; } = rel;
}

public static class VfsPath
{
    /// <summary>The canonical spelling of a path in this namespace, with the names that are not
    /// names refused.
    ///
    /// What arrives here comes from two places: the interface, which normalises but is not
    /// obliged to be careful, and listings from a source, which are whatever that source says.
    /// Both are turned into the one form every layer indexes by — forward slashes, no leading or
    /// trailing slash, the empty string for the root — and both are refused if a component could
    /// mean something other than a file name:
    ///
    /// <c>.</c> and <c>..</c> walk the path rather than naming something in it; a colon ends the
    /// name and starts an alternate stream; a reserved device name (<c>NUL</c>, <c>COM1</c>,
    /// <c>CON.txt</c>, …) is a file no directory contains, so opening a path that ends in one
    /// reaches the device instead of a file. None of those can be created as ordinary files on
    /// Windows either, so refusing them loses nothing and closes the ways a path stops being a
    /// path.
    ///
    /// It is a scan and not a regular expression because it runs on every resolution: one pass,
    /// and no allocation for a path that is already canonical.</summary>
    public static string Normalize(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return string.Empty;
        var s = fileName.Contains('\\') ? fileName.Replace('\\', '/') : fileName;
        if (s == "/") return string.Empty;
        s = s.Trim('/');
        if (!IsCanonical(s)) throw new ArgumentException("not a name in this namespace: '" + fileName + "'");
        return s;
    }

    /// <summary>Whether every component of an already normalised path is a plain file name.
    /// See <see cref="Normalize"/>.</summary>
    private static bool IsCanonical(string path)
    {
        int start = 0;
        for (int i = 0; i <= path.Length; i++)
        {
            if (i < path.Length && path[i] != '/')
            {
                if (path[i] == ':') return false;
                continue;
            }
            int length = i - start;
            if (length == 0) return false;                                  // an empty component
            if (length == 1 && path[start] == '.') return false;            // "."
            if (length == 2 && path[start] == '.' && path[start + 1] == '.') return false;
            if (IsDeviceName(path, start, length)) return false;
            start = i + 1;
        }
        return true;
    }

    /// <summary>Whether one component is a reserved device name, with or without an extension:
    /// Windows resolves <c>CON</c> and <c>CON.txt</c> to the console device wherever they appear
    /// in a path, so a source listing either of them would otherwise reach a device rather than a
    /// file.</summary>
    private static bool IsDeviceName(string path, int start, int length)
    {
        int dot = path.IndexOf('.', start, length);
        int nameLength = dot < 0 ? length : dot - start;
        if (nameLength is < 3 or > 4) return false;
        ReadOnlySpan<char> name = path.AsSpan(start, nameLength);
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            return true;
        if (nameLength != 4) return false;
        // COM1..COM9 and LPT1..LPT9: a digit is what makes the fourth character one.
        int number = name[3] - '0';
        if (number is < 1 or > 9) return false;
        ReadOnlySpan<char> prefix = name[..3];
        return prefix.Equals("COM", StringComparison.OrdinalIgnoreCase) ||
               prefix.Equals("LPT", StringComparison.OrdinalIgnoreCase);
    }

    public static string Parent(string rel)
    {
        var i = rel.LastIndexOf('/');
        return i < 0 ? string.Empty : rel[..i];
    }

    public static string Name(string rel)
    {
        var i = rel.LastIndexOf('/');
        return i < 0 ? rel : rel[(i + 1)..];
    }

    public static string Child(string rel, string name) => rel.Length == 0 ? name : rel + "/" + name;
}
