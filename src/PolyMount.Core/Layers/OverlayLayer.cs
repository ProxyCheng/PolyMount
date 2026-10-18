using PolyMount.Core;

namespace PolyMount.Layers;

/// <summary>Durable path-keyed object layer. Namespace changes are complete directory objects.</summary>
public sealed class OverlayLayer : ILayer, IObjectManagement, ILayerStorageUsage
{
    private readonly ObjectDatabase _database;
    public OverlayLayer(string root, bool compression = false) => _database = new(root, compression);
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
    /// <summary>Forgets this layer's record of a path and of everything below it: a directory
    /// whose own record went but whose children's stayed would keep serving those children
    /// over whatever the layers below now show there. The parent's listing is the chain's to
    /// reconcile (see <see cref="VirtualFileSystem.ReconcileAfterRelease"/>), because only the
    /// chain knows what is underneath.</summary>
    public void DropObject(string path)
    {
        var keys = _database.KeysAtOrBelow(VfsPath.Normalize(path));
        if (keys.Count > 0) _database.Apply(keys.Select(key => new ObjectChange(key, null)).ToArray());
    }
    public void ReleaseObject(string path) => DropObject(path);
    public LayerStorageUsage GetStorageUsage()
    {
        var usage = _database.GetStorageUsage();
        return usage with { DurableBytes = usage.ObjectBytes, DurableObjects = usage.FileObjects + usage.DirectoryObjects };
    }
}
