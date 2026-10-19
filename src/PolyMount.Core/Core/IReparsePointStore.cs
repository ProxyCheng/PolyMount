namespace PolyMount.Core;

/// <summary>A layer that keeps reparse points: the data a symbolic link, a junction or another
/// name-surrogate object is made of. Only a layer that accepts writes is asked to keep one; a
/// read-through cache has nothing of its own to say about it. The file system hands the bytes
/// back to Windows, which resolves the target itself — the layer only has to remember them.</summary>
public interface IReparsePointStore
{
    bool TryGetReparsePoint(string path, out byte[]? data);
    bool SetReparsePoint(string path, byte[] data);
    bool DeleteReparsePoint(string path);
    /// <summary>Whether a name carries a reparse point, without loading its bytes: every entry
    /// the resolver hands out is marked with it, so the answer has to be cheap.</summary>
    bool TryGetReparseTag(string path, out uint tag);
}
