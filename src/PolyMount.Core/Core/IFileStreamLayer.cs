namespace PolyMount.Core;

/// <summary>Transparent byte-range acceleration above the selected content provider.</summary>
public interface IFileStreamLayer
{
    // Takes ownership of the stream. Writable staging streams use private cache identities.
    Stream WrapStream(string path, string version, Stream stream, bool writable);
}
