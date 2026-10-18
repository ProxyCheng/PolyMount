using PolyMount.Layers;

namespace PolyMount.Core;

public static class SourceRegistry
{
    private static readonly Dictionary<string, Func<SourceConfig, string, ILayer>> Factories =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Action<SourceConfig>> Validators = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, LayerDescriptor> Descriptions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ICredentialHandler> Credentials = new(StringComparer.OrdinalIgnoreCase);

    static SourceRegistry()
    {
        Register("dir", (cfg, _) =>
        {
            if (string.IsNullOrWhiteSpace(cfg.Path))
                throw new ArgumentException("source type 'dir' requires 'Path'");
            return new DirectoryLayer(cfg.Path, cfg.Writable);
        }, cfg => { if (string.IsNullOrWhiteSpace(cfg.Path)) throw new ArgumentException("directory provider requires a path"); }, BuiltInLayerDescriptors.Directory());
    }

    public static void Register(string type, Func<SourceConfig, string, ILayer> factory, Action<SourceConfig>? validate = null)
        => Register(type, factory, validate, null);
    public static void Register(string type, Func<SourceConfig, string, ILayer> factory, Action<SourceConfig>? validate, LayerDescriptor? descriptor,
        ICredentialHandler? credentials = null)
    {
        Factories[type] = factory;
        if (validate != null) Validators[type] = validate;
        else Validators.Remove(type);
        if (descriptor != null) { descriptor.Type = type; Descriptions[type] = descriptor; }
        else Descriptions.Remove(type);
        if (credentials != null) Credentials[type] = credentials;
        else Credentials.Remove(type);
    }

    /// <summary>The handler that can sign this source type in, or null when it never needs to.</summary>
    public static ICredentialHandler? CredentialHandler(string type) => Credentials.GetValueOrDefault(type);
    public static void Validate(string type, SourceConfig config)
    {
        if (Validators.TryGetValue(type, out var validate)) validate(config);
    }

    public static bool Contains(string type) => Factories.ContainsKey(type);

    public static IEnumerable<string> Types => Factories.Keys;
    internal static void Unregister(string type) { Factories.Remove(type); Validators.Remove(type); Descriptions.Remove(type); Credentials.Remove(type); }
    public static LayerDescriptor? Describe(string type) => Descriptions.GetValueOrDefault(type);

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
