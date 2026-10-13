namespace PolyMount.Core;

/// <summary>Immutable logical payload; content access and lifetime stay at the content boundary.</summary>
public abstract class ObjectContent
{
    public abstract long LogicalBytes { get; }
    public abstract long EstimatedMemoryBytes { get; }
    public abstract ObjectContent Snapshot();
    public virtual IReadOnlyList<VfsEntry>? Entries => null;
    public virtual Stream OpenRead() => throw new NotSupportedException("This content is not a byte stream");
    internal virtual PolyMount.Layers.CacheContent? RetainBytes(Stream? stream) => null;
    internal virtual long PersistenceEstimate => EstimatedMemoryBytes;
    protected static VfsEntry Copy(VfsEntry entry) => new()
        { Name = entry.Name, IsDir = entry.IsDir, Size = entry.Size, TimeUtc = entry.TimeUtc };
}

public sealed class FileObjectContent : ObjectContent
{
    private readonly Func<Stream>? _open;
    public FileObjectContent(long length, Func<Stream>? open = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        LogicalBytes = length; _open = open;
    }
    public override long LogicalBytes { get; }
    public override long EstimatedMemoryBytes => 128;
    internal override long PersistenceEstimate => checked(LogicalBytes + 128);
    public override ObjectContent Snapshot() => this;
    // The factory must return an independent, caller-owned, seekable read stream.
    public override Stream OpenRead() => _open?.Invoke() ?? throw new NotSupportedException("Content has no read factory");
    internal override PolyMount.Layers.CacheContent? RetainBytes(Stream? stream) =>
        stream == null ? null : PolyMount.Layers.CacheContent.Capture(stream);
}

public sealed class DirectoryObjectContent : ObjectContent
{
    private readonly IReadOnlyList<VfsEntry> _entries;
    private readonly Dictionary<string, VfsEntry> _members;
    public DirectoryObjectContent(IEnumerable<VfsEntry> entries)
    {
        var snapshot = entries.Select(Copy).ToArray();
        _entries = Array.AsReadOnly(snapshot);
        _members = new(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in snapshot)
            if (!_members.TryAdd(entry.Name, entry)) throw new ArgumentException("Duplicate directory member: " + entry.Name);
        EstimatedMemoryBytes = 256L + snapshot.Sum(c => 256L + c.Name.Length * 6L);
    }
    public override IReadOnlyList<VfsEntry> Entries => _entries;
    public override long LogicalBytes => EstimatedMemoryBytes;
    public override long EstimatedMemoryBytes { get; }
    public bool Contains(string name) => _members.ContainsKey(name);
    public bool TryGetEntry(string name, out VfsEntry entry) => _members.TryGetValue(name, out entry!);
    public override ObjectContent Snapshot() => new DirectoryObjectContent(_entries);
}
