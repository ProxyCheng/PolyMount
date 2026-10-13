using System.Diagnostics;
using PolyMount;
using PolyMount.Core;

internal static class WarmPathProbe
{
    public static void Run(string configPath, string mountName, string relativePath)
    {
        var config = AppConfig.LoadOrCreate(configPath);
        var errors = PluginLoader.LoadConfigured(config.Plugins, Path.GetDirectoryName(configPath)!);
        if (errors.Count != 0) throw new InvalidOperationException(string.Join("; ", errors));
        var profile = config.Mounts.Single(m => m.Name == mountName);
        if (profile.GetLayers().Any(l => l.Type == "svn" && l.Source.PinnedRevision == 0))
            throw new InvalidOperationException("Probe requires a pinned source revision");
        using var instance = new MountInstance(profile);
        var vfs = instance.Vfs;
        using (vfs.OpenRead(relativePath, out _)) { }
        foreach (bool diagnostics in new[] { true, false })
        {
            var trace = vfs.Diagnostics;
            if (!diagnostics) vfs.Diagnostics = null;
            try
            {
                Measure("Lookup", 1000, () => { if (!vfs.TryLookup(relativePath, out _)) throw new FileNotFoundException(); });
                Measure("OpenClose", 1000, () => { using var stream = vfs.OpenRead(relativePath, out _); });
                Measure("RepositoryList", 20, () => { _ = vfs.List("Repository"); });
            }
            finally { vfs.Diagnostics = trace; }
            Console.WriteLine($"Diagnostics={diagnostics}");
        }
    }
    private static void Measure(string name, int count, Action action)
    {
        action();
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < count; i++) action();
        Console.WriteLine($"{name}: {watch.Elapsed.TotalMilliseconds / count:0.000} ms/op, {(GC.GetAllocatedBytesForCurrentThread() - bytes) / count} allocated bytes/op");
    }
}
