using PolyMount.Layers;
using PolyMount.Plugins.Svn;

namespace PolyMount.Core;

public static class SourceRegistry
{
    private static readonly Dictionary<string, Func<SourceConfig, string, ILayer>> Factories =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Action<SourceConfig>> Validators = new(StringComparer.OrdinalIgnoreCase);

    static SourceRegistry()
    {
        Register("svn", (cfg, stateDir) =>
            SvnLayer.Create(cfg.Url, cfg.TrustServerCertFailures, Path.Combine(stateDir, "pin.txt"),
                cfg.Username, cfg.Password), cfg =>
            {
                if (!Uri.TryCreate(cfg.Url, UriKind.Absolute, out _) || cfg.PinnedRevision < 0)
                    throw new ArgumentException("SVN requires an absolute repository URL and nonnegative revision");
            });

        Register("dir", (cfg, _) =>
        {
            if (string.IsNullOrWhiteSpace(cfg.Path))
                throw new ArgumentException("source type 'dir' requires 'Path'");
            return new DirectoryLayer(cfg.Path, cfg.Writable);
        }, cfg => { if (string.IsNullOrWhiteSpace(cfg.Path)) throw new ArgumentException("directory provider requires a path"); });
    }

    public static void Register(string type, Func<SourceConfig, string, ILayer> factory, Action<SourceConfig>? validate = null)
    {
        Factories[type] = factory;
        if (validate != null) Validators[type] = validate;
        else Validators.Remove(type);
    }
    public static void Validate(string type, SourceConfig config)
    {
        if (Validators.TryGetValue(type, out var validate)) validate(config);
    }

    public static bool Contains(string type) => Factories.ContainsKey(type);

    public static IEnumerable<string> Types => Factories.Keys;

    public static ILayer Create(SourceConfig config, string stateDir, string? type = null)
    {
        if (!Factories.TryGetValue(type ?? config.Type, out var factory))
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
