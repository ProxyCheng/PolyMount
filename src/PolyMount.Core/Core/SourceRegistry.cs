using PolyMount.Layers;
using PolyMount.Plugins.Svn;

namespace PolyMount.Core;

public static class SourceRegistry
{
    private static readonly Dictionary<string, Func<SourceConfig, string, ILayer>> Factories =
        new(StringComparer.OrdinalIgnoreCase);

    static SourceRegistry()
    {
        Register("svn", (cfg, stateDir) =>
            SvnLayer.Create(cfg.Url, cfg.TrustServerCertFailures, Path.Combine(stateDir, "pin.txt"),
                cfg.Username, cfg.Password));

        Register("dir", (cfg, _) =>
        {
            if (string.IsNullOrWhiteSpace(cfg.Path))
                throw new ArgumentException("source type 'dir' requires 'Path'");
            return new DirectoryLayer(cfg.Path, cfg.Writable);
        });
    }

    public static void Register(string type, Func<SourceConfig, string, ILayer> factory)
        => Factories[type] = factory;

    public static bool Contains(string type) => Factories.ContainsKey(type);

    public static IEnumerable<string> Types => Factories.Keys;

    public static ILayer Create(SourceConfig config, string stateDir)
    {
        if (!Factories.TryGetValue(config.Type, out var factory))
            throw new NotSupportedException("unknown source type: " + config.Type);

        var layer = factory(config, stateDir);

        if (layer is IRevisionSource rev)
        {
            long revision = config.PinnedRevision > 0 ? config.PinnedRevision : rev.HeadRevision();
            rev.SetRevision(revision);
        }
        return layer;
    }
}
