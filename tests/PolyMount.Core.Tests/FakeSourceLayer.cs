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

    public void BumpGeneration(string gen) => Generation = gen;

    public void AddDir(string rel) => _nodes[rel] = new Node { IsDir = true };

    public void AddFile(string rel, string content)
    {
        // auto-create parent dirs
        var parent = VfsPath.Parent(rel);
        while (parent.Length > 0 && !_nodes.ContainsKey(parent))
        {
            _nodes[parent] = new Node { IsDir = true };
            parent = VfsPath.Parent(parent);
        }
        _nodes[rel] = new Node { IsDir = false, Data = System.Text.Encoding.UTF8.GetBytes(content) };
    }

    public bool IsDeleted(string rel) => false;

    public bool TryGetEntry(string rel, out VfsEntry entry)
    {
        entry = null!;
        if (rel.Length == 0) { entry = new VfsEntry { Name = "", IsDir = true }; return true; }
        if (!_nodes.TryGetValue(rel, out var n)) return false;
        entry = new VfsEntry { Name = VfsPath.Name(rel), IsDir = n.IsDir, Size = n.Data.Length, TimeUtc = n.TimeUtc };
        return true;
    }

    public IReadOnlyList<VfsEntry> ListOwn(string rel)
    {
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
