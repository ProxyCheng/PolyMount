namespace PolyMount.Core;

public sealed class VfsEntry
{
    public string Name { get; init; } = string.Empty;
    public bool IsDir { get; init; }
    public long Size { get; init; }
    public DateTime TimeUtc { get; init; }
}

public sealed class VfsNotFoundException : Exception
{
    public VfsNotFoundException(string rel) : base("not found: " + rel) => Rel = rel;
    public string Rel { get; }
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
