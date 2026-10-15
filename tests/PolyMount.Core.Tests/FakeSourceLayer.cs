using PolyMount.Core;

namespace PolyMount.Core.Tests;

/// <summary>
/// In-memory, read-only <see cref="ILayer"/> used as a controllable data source in tests.
/// Its <see cref="Generation"/> can be bumped to simulate upstream content changes.
/// </summary>
internal sealed class FakeSourceLayer : ILayer
{
    private sealed class Node
    {
        public bool IsDir;
        public byte[] Data = Array.Empty<byte>();
        public DateTime TimeUtc = DateTime.UtcNow;
    }

    // rel -> node. "" is the implicit root directory.
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "fake";
    public bool Writable => false;
    public string Generation { get; private set; } = "g0";

    public int OpenReadCount { get; private set; }
    public List<string> ListedDirectories { get; } = new();
    /// <summary>Paths this source was asked about by name, which is the other way a
    /// resolution reaches it: a listing answers its children, this answers one path.</summary>
    public List<string> EntryLookups { get; } = new();
    /// <summary>The class each request arrived in, in order, so a test can see what the resolver
    /// told the source about the work it was asking for.</summary>
    public List<(string Operation, int Priority)> Lanes { get; } = new();

    private void Record(string operation) { lock (Lanes) Lanes.Add((operation, ReadPriority.Ambient)); }

    public void BumpGeneration(string gen) => Generation = gen;

    public void AddDir(string rel) => _nodes[rel] = new Node { IsDir = true };

    public void AddFile(string rel, string content) => AddFileBytes(rel, System.Text.Encoding.UTF8.GetBytes(content));

    /// <summary>Replaces a file's content. <paramref name="keepTime"/> leaves its timestamp
    /// alone, so a test can change content behind an identity that would otherwise move.</summary>
    public void AddFileBytes(string rel, byte[] content, bool keepTime = false)
    {
        // auto-create parent dirs
        var parent = VfsPath.Parent(rel);
        while (parent.Length > 0 && !_nodes.ContainsKey(parent))
        {
            _nodes[parent] = new Node { IsDir = true };
            parent = VfsPath.Parent(parent);
        }
        _nodes[rel] = new Node
        {
            IsDir = false,
            Data = content,
            TimeUtc = keepTime && _nodes.TryGetValue(rel, out var previous) ? previous.TimeUtc : DateTime.UtcNow
        };
    }

    public bool IsDeleted(string rel) => false;

    public bool TryGetEntry(string rel, out VfsEntry entry)
    {
        entry = null!;
        Record("entry");
        EntryLookups.Add(rel);
        if (rel.Length == 0) { entry = new VfsEntry { Name = "", IsDir = true }; return true; }
        if (!_nodes.TryGetValue(rel, out var n)) return false;
        entry = new VfsEntry { Name = VfsPath.Name(rel), IsDir = n.IsDir, Size = n.Data.Length, TimeUtc = n.TimeUtc };
        return true;
    }

    public IReadOnlyList<VfsEntry> ListOwn(string rel)
    {
        Record("list");
        ListedDirectories.Add(rel);
        var list = new List<VfsEntry>();
        foreach (var kv in _nodes)
        {
            if (kv.Key.Length == 0) continue;
            if (!string.Equals(VfsPath.Parent(kv.Key), rel, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new VfsEntry
            {
                Name = VfsPath.Name(kv.Key),
                IsDir = kv.Value.IsDir,
                Size = kv.Value.Data.Length,
                TimeUtc = kv.Value.TimeUtc
            });
        }
        return list;
    }

    public bool TryOpenRead(string rel, out VfsEntry entry, out Stream stream)
    {
        stream = null!;
        if (!TryGetEntry(rel, out entry) || entry.IsDir) return false;
        Record("read");
        OpenReadCount++;
        stream = new MemoryStream(_nodes[rel].Data, writable: false);
        return true;
    }

    public Stream Create(string rel, out VfsEntry entry) => throw new NotSupportedException();
    public Stream OpenExistingWrite(string rel, out VfsEntry entry) => throw new NotSupportedException();
    public void CreateDirectory(string rel) => throw new NotSupportedException();
    public void Delete(string rel, bool shadowedBelow) => throw new NotSupportedException();
    public void Rename(string rel, string dst, bool replace, bool shadowedBelow) => throw new NotSupportedException();
}
