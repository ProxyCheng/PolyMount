namespace PolyMount.Core;

/// <summary>Stable instance identity for multiple providers of the same type.</summary>
internal class NamedLayer(string name, ILayer inner) : ILayer
{
    internal ILayer Inner => inner;
    public string Name => name;
    public string Generation => inner.Generation;
    public bool Writable => inner.Writable;
    public bool IsDeleted(string rel) => inner.IsDeleted(rel);
    public bool TryGetEntry(string rel, out VfsEntry entry) => inner.TryGetEntry(rel, out entry);
    public IReadOnlyList<VfsEntry> ListOwn(string rel) => inner.ListOwn(rel);
    public bool TryOpenRead(string rel, out VfsEntry entry, out Stream stream) => inner.TryOpenRead(rel, out entry, out stream);
    public Stream Create(string rel, out VfsEntry entry) => inner.Create(rel, out entry);
    public Stream OpenExistingWrite(string rel, out VfsEntry entry) => inner.OpenExistingWrite(rel, out entry);
    public void CreateDirectory(string rel) => inner.CreateDirectory(rel);
    public void Delete(string rel, bool shadowedBelow) => inner.Delete(rel, shadowedBelow);
    public void Rename(string oldRel, string newRel, bool replace, bool shadowedBelow) => inner.Rename(oldRel, newRel, replace, shadowedBelow);
}

internal sealed class NamedCacheLayer(string name, ICacheLayer inner) : NamedLayer(name, inner), ICacheLayer
{
    public void Store(string rel, string version, VfsEntry entry, Stream content) => inner.Store(rel, version, entry, content);
    public void Invalidate() => inner.Invalidate();
}
