using PolyMount.Layers;

namespace PolyMount.Core;

/// <summary>Provider construction and parameter validation, independent of chain layout.</summary>
public static class LayerRegistry
{
    private sealed record Registration(Func<LayerConfig, LayerBuildContext, ILayer> Create, Action<LayerConfig>? Validate, LayerDescriptor? Descriptor);
    private static readonly Dictionary<string, Registration> Registrations = new(StringComparer.OrdinalIgnoreCase);
    static LayerRegistry()
    {
        Register("overlay", (c, ctx) => new OverlayLayer(ctx.Storage, ctx.State, c.Compression), null, BuiltInLayerDescriptors.Storage("overlay", false));
        Register("prediction-cache", (c, ctx) => new PredictionCacheLayer(MemoryCapacity(c), ctx.LowerGeneration,
            new ReadPredictionOptions { Enabled = true }, Path.Combine(ctx.State, "prediction.json")),
            c => { if (MemoryCapacity(c) < 1 || MemoryCapacity(c) > 256L * 1048576) throw new ArgumentException("Prediction capacity exceeds 256 MiB"); },
            new LayerDescriptor { Type = "prediction-cache", Title = new("Predictive preload", "预测预载"),
                Description = new("Learns object access order and warms a read cache below this layer; does not serve cache hits itself.",
                    "学习文件与目录的访问顺序，提前加载到下方读取缓存；本层不提供读取命中。"),
                Fields = new() { new() { Path = "Parameters.CapacityBytes", Kind = LayerFieldKind.Integer, Scale = 1048576,
                    Minimum = 1, Maximum = 256, DefaultValue = "16", Title = new("Preload file budget (MiB)", "预载文件预算（MiB）") } } });
        Register("cache", (c, ctx) => new LruCacheLayer(ctx.Storage, ctx.LowerGeneration, c.CacheCapacityBytes, c.Compression),
            c => { if (c.CacheCapacityBytes <= 0) throw new ArgumentException($"cache '{c.Id}' requires positive capacity"); }, BuiltInLayerDescriptors.Storage("cache", true));
        Register("memory-cache", (c, ctx) => new MemoryCacheLayer(MemoryCapacity(c), MemoryBlockSize(c), ctx.LowerGeneration),
            c =>
            {
                long capacity = MemoryCapacity(c); int block = MemoryBlockSize(c);
                if (block < 4096 || block > 1048576 || (block & (block - 1)) != 0 || capacity < block)
                    throw new ArgumentException("memory-cache requires capacity >= block size and a power-of-two block size (4 KiB–1 MiB)");
            }, BuiltInLayerDescriptors.Memory());
    }
    private static long MemoryCapacity(LayerConfig config) => config.Parameters.TryGetValue("CapacityBytes", out var value) ? value.GetInt64() : 16 * 1048576;
    private static int MemoryBlockSize(LayerConfig config) => config.Parameters.TryGetValue("BlockSize", out var value)
        ? value.ValueKind == System.Text.Json.JsonValueKind.String ? int.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : value.GetInt32() : 65536;
    public static void Register(string type, Func<LayerConfig, LayerBuildContext, ILayer> create, Action<LayerConfig>? validate = null)
        => Register(type, create, validate, null);
    public static void Register(string type, Func<LayerConfig, LayerBuildContext, ILayer> create, Action<LayerConfig>? validate, LayerDescriptor? descriptor)
    {
        if (descriptor != null) descriptor.Type = type;
        Registrations[type] = new(create, validate, descriptor);
    }
    public static LayerDescriptor Describe(string type) =>
        (Registrations.TryGetValue(type, out var registration) ? registration.Descriptor : SourceRegistry.Describe(type))
        ?? new LayerDescriptor { Type = type, Title = new(type, type) };
    public static IEnumerable<LayerDescriptor> Descriptors => Types.Select(Describe);
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
