namespace PolyMount.Core;

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
    public static string Normalize(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return string.Empty;
        var s = fileName.Replace('\\', '/');
        if (s == "/") return string.Empty;
        return s.Trim('/');
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
