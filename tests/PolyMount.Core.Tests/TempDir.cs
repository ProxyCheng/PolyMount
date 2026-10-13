namespace PolyMount.Core.Tests;

/// <summary>Creates a unique temp directory and recursively deletes it on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PolyMountTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Sub(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
        catch { /* best effort */ }
    }
}
