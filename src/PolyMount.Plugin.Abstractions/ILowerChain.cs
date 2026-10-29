namespace PolyMount.Plugins;

/// <summary>The chain beneath a layer, as that layer sees it.
///
/// A layer that needs to fetch through layers it does not know by name — a prediction strategy
/// reading a directory of the repository, a transformation layer translating a path and letting
/// the chain resolve it — is handed this, and nothing else. The mount builds the chain and keeps
/// its own bookkeeping; what a layer may do with the chain is exactly what is declared here, so a
/// layer can be written and versioned without the engine's own types.
///
/// Paths are relative to the mount root in the canonical spelling <see cref="VfsPath"/>
/// produces. Methods throw the same exceptions the engine's file-system boundary translates:
/// <see cref="VfsNotFoundException"/>, <see cref="UnauthorizedAccessException"/> for a chain with
/// no writable layer, and <see cref="IOException"/> for the rest.
/// </summary>
public interface ILowerChain
{
    /// <summary>The layers of the chain, highest priority first. A layer that needs to reach a
    /// particular kind of layer below it — another layer type it knows by contract — finds it
    /// here.</summary>
    IReadOnlyList<ILayer> Layers { get; }

    /// <summary>Whether any layer in the chain accepts a write.</summary>
    bool Writable { get; }

    /// <summary>The chain's namespace generation: it moves when the source's content does, and
    /// everything keyed by it is then stale.</summary>
    string Generation { get; }

    /// <summary>The request tracing the mount was built with, or null when it has none. A source
    /// that spends real time on the network opens a scope here so its round trips show up in the
    /// request log beside the lookups that asked for them.</summary>
    IRequestTracing? Tracing { get; }

    /// <summary>The header of a path, or false when the chain does not have it.</summary>
    bool TryLookup(string path, out VfsEntry entry, bool recordAccess = false);

    /// <summary>The complete listing of a directory.</summary>
    List<VfsEntry> List(string path, bool recordAccess = true, bool speculative = false, bool fill = true);

    /// <summary>Opens a file for reading, reporting the entry the chain resolved.</summary>
    Stream OpenRead(string path, out VfsEntry entry, bool speculative = false);

    /// <summary>Opens a file for writing through the chain's writable layer, copying it up first
    /// when only a lower layer has it.</summary>
    Stream OpenWrite(string path, bool truncate, out VfsEntry entry);

    /// <summary>Creates a directory, including any missing parents.</summary>
    void CreateDirectory(string path);

    /// <summary>Deletes a file or an empty directory.</summary>
    void Delete(string path);

    /// <summary>Throws what a delete of this path would throw, without deleting it.</summary>
    void CheckDelete(string path);

    /// <summary>Renames or moves a path within the chain.</summary>
    void Rename(string path, string destination, bool replace);

    /// <summary>Drops every cache's copy of everything, keeping user writes.</summary>
    void InvalidateCaches();

    /// <summary>The reparse data of a path, when the writable layer holds one.</summary>
    bool TryGetReparsePoint(string path, out byte[]? data);

    /// <summary>Stores reparse data for a path.</summary>
    bool SetReparsePoint(string path, byte[] data);

    /// <summary>Drops the reparse data of a path.</summary>
    bool DeleteReparsePoint(string path);

    /// <summary>The index of the first path component that is a link, or false when none is.</summary>
    bool TryFindReparsePoint(string path, out int index);
}
