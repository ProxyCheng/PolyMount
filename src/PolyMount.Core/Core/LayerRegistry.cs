using PolyMount.Layers;

namespace PolyMount.Core;

/// <summary>Provider construction and parameter validation, independent of chain layout.</summary>
public static class LayerRegistry
{
    private sealed record Registration(Func<LayerConfig, LayerBuildContext, ILayer> Create, Action<LayerConfig>? Validate);
    private static readonly Dictionary<string, Registration> Registrations = new(StringComparer.OrdinalIgnoreCase);
    static LayerRegistry()
    {
        Register("overlay", (c, ctx) => new OverlayLayer(ctx.Storage, ctx.State, c.Compression));
        Register("cache", (c, ctx) => new LruCacheLayer(ctx.Storage, ctx.LowerGeneration, c.CacheCapacityBytes, c.Compression),
            c => { if (c.CacheCapacityBytes <= 0) throw new ArgumentException($"cache '{c.Id}' requires positive capacity"); });
    }
    public static void Register(string type, Func<LayerConfig, LayerBuildContext, ILayer> create, Action<LayerConfig>? validate = null)
        => Registrations[type] = new(create, validate);
    public static bool Contains(string type) => Registrations.ContainsKey(type) || SourceRegistry.Contains(type);
    public static IEnumerable<string> Types => Registrations.Keys.Concat(SourceRegistry.Types).Distinct(StringComparer.OrdinalIgnoreCase);
    internal static void Unregister(string type) => Registrations.Remove(type);
    public static void Validate(LayerConfig config)
    {
        if (Registrations.TryGetValue(config.Type, out var registration)) registration.Validate?.Invoke(config);
        else SourceRegistry.Validate(config.Type, config.Source);
    }
    public static ILayer Create(LayerConfig config, LayerBuildContext context)
    {
        if (Registrations.TryGetValue(config.Type, out var registration)) return registration.Create(config, context);
        return SourceRegistry.Create(config.Source, context.State, config.Type);
    }
}

public sealed record LayerBuildContext(string Storage, string State, Func<string> LowerGeneration);
