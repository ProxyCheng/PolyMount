using PolyMount.Core.Ipc;

namespace PolyMount.Core;

/// <summary>Common explicit cache management, independent of read-through capabilities.</summary>
public abstract class CacheLayer : ILayer
{
    public abstract string Name { get; }
    public abstract string Generation { get; }
    public abstract bool Writable { get; }
    public abstract bool TryGetEntry(string path, out VfsEntry entry);
    public abstract IReadOnlyList<VfsEntry> ListOwn(string path);
    public abstract bool TryOpenRead(string path, out VfsEntry entry, out Stream stream);
    public virtual bool TryReadObject(string path, out VirtualObject value)
    {
        value = null!;
        if (!TryGetEntry(path, out var header)) return false;
        value = new(header, header.IsDir ? ListOwn(path) : null);
        return true;
    }
    public virtual bool TryApply(IReadOnlyList<ObjectChange> changes) => false;
    /// <summary>Clear this layer's recoverable state while preserving user writes.</summary>
    public abstract void Clear();
    internal static void AddManagementActions(LayerDescriptor descriptor)
    {
        if (descriptor.Actions.Any(a => a.Command == ControlCommands.ClearCache)) return;
        descriptor.Actions.Add(new()
        {
            Command = ControlCommands.ClearCache,
            Title = LayerTexts.Get("cache.clear"),
            Confirmation = LayerTexts.Get("cache.clear.confirm")
        });
    }
}
