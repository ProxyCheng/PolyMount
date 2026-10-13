using PolyMount.Core;

namespace PolyMount.Layers;

/// <summary>Durable path-keyed object layer. Namespace changes are complete directory objects.</summary>
public sealed class OverlayLayer : ILayer, IObjectManagement, ILayerStorageUsage
{
    private readonly ObjectDatabase _database;
    public OverlayLayer(string root, string statePath, bool compression = false) => _database = new(root, compression);
    public string Name => "overlay";
    public string Generation => "local";
    public bool Writable => true;
    public bool TryReadObject(string path, out VirtualObject value) => _database.TryGet(path, out value);
    public bool TryApply(IReadOnlyList<ObjectChange> changes) { _database.Apply(changes); return true; }
    public bool TryGetEntry(string rel, out VfsEntry entry)
    {
        bool found = TryReadObject(rel, out var value); entry = found ? value.Header : null!; return found;
    }
    public IReadOnlyList<VfsEntry> ListOwn(string rel) => TryReadObject(rel, out var value) ? value.Children ?? Array.Empty<VfsEntry>() : Array.Empty<VfsEntry>();
    public bool TryOpenRead(string rel, out VfsEntry entry, out Stream stream)
    {
        bool found = _database.TryOpen(rel, out var value, out stream); entry = found ? value.Header : null!; return found;
    }
    public void ClearLocalChanges() => _database.Clear();
    public void DropObject(string path) => _database.Apply(new[] { new ObjectChange(path, null) });
    public void ReleaseObject(string path) => DropObject(path);
    public LayerStorageUsage GetStorageUsage()
    {
        var usage = _database.GetStorageUsage();
        return usage with { DurableBytes = usage.ObjectBytes, DurableObjects = usage.FileObjects + usage.DirectoryObjects };
    }
}
