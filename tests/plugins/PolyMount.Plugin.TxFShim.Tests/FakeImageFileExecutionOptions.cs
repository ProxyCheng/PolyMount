using PolyMount.Plugins.TxFShim;

namespace PolyMount.Plugins.TxFShim;

/// <summary>The registry, in memory. The real one needs an administrator and outlives a test run;
/// what is worth checking is the policy on top of it — refuse a foreign entry, remove only what we
/// wrote, report drift — and that policy never sees the registry directly.</summary>
internal sealed class FakeImageFileExecutionOptions : IImageFileExecutionOptions
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    internal int Writes { get; private set; }
    internal int Deletes { get; private set; }

    /// <summary>Makes the next write throw, the way an unprivileged worker would see it.</summary>
    internal bool DenyWrites { get; set; }

    internal void Seed(string imageName, string launcher) => _values[imageName] = launcher;

    internal void Forget(string imageName) => _values.Remove(imageName);

    public string? Launcher(string imageName) => _values.TryGetValue(imageName, out var value) ? value : null;

    public void SetLauncher(string imageName, string launcher)
    {
        if (DenyWrites) throw new UnauthorizedAccessException("access to the registry key is denied");
        Writes++;
        _values[imageName] = launcher;
    }

    public void RemoveLauncher(string imageName)
    {
        Deletes++;
        _values.Remove(imageName);
    }

    public IEnumerable<string> Instrumented() => _values.Keys.ToArray();
}
