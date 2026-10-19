using PolyMount.Core;

namespace PolyMount.Layers;

/// <summary>Durable path-keyed object layer. Namespace changes are complete directory objects.</summary>
public sealed class OverlayLayer : ILayer, IObjectManagement, ILayerStorageUsage, IReparsePointStore, IDisposable
{
    private readonly ObjectDatabase _database;
    public OverlayLayer(string root, bool compression = false) => _database = new(root, compression);
    public string Name => "overlay";
    public string Generation => "local";
    public bool Writable => true;
    /// <summary>Makes every accepted write durable now. Writes are coalesced into short
    /// transactions (see <see cref="ObjectDatabase"/>), so a caller that needs a point on
    /// disk - a test reopening the store, an orderly unmount - says so here.</summary>
    public void Flush() => _database.Commit();
    public void Dispose() => _database.Dispose();
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
    public bool TryGetReparsePoint(string path, out byte[]? data) =>
        _database.TryGetReparse(VfsPath.Normalize(path), out data);
    public bool SetReparsePoint(string path, byte[] data)
    {
        _database.SetReparse(VfsPath.Normalize(path), data);
        return true;
    }
    public bool DeleteReparsePoint(string path) => _database.DeleteReparse(VfsPath.Normalize(path));
    public bool TryGetReparseTag(string path, out uint tag) =>
        _database.TryGetReparseTag(VfsPath.Normalize(path), out tag);
    /// <summary>Forgets this layer's record of a path and of everything below it: a directory
    /// whose own record went but whose children's stayed would keep serving those children
    /// over whatever the layers below now show there. The parent's listing is the chain's to
    /// reconcile (see <see cref="VirtualFileSystem.ReconcileAfterRelease"/>), because only the
    /// chain knows what is underneath.</summary>
    public void DropObject(string path)
    {
        var keys = _database.KeysAtOrBelow(VfsPath.Normalize(path));
        if (keys.Count == 0) return;
        _database.Apply(keys.Select(key => new ObjectChange(key, null)).ToArray());
        // An explicit management action, not a write in a burst: when it returns, the store
        // must not bring the dropped objects back after a restart.
        _database.Commit();
    }
    public void ReleaseObject(string path) => DropObject(path);
    public LayerStorageUsage GetStorageUsage()
    {
        var usage = _database.GetStorageUsage();
        return usage with { DurableBytes = usage.ObjectBytes, DurableObjects = usage.FileObjects + usage.DirectoryObjects };
    }
}
