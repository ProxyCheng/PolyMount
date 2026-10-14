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
        RegisterCache("prediction-cache", (c, ctx) => new PredictionCacheLayer(MemoryCapacity(c), ctx.LowerGeneration,
            ReadPredictionProfiles.Resolve(Text(c, "Profile"), () => CustomPrediction(c)), Path.Combine(ctx.State, "prediction.json")),
            c => { if (MemoryCapacity(c) < 1 || MemoryCapacity(c) > 256L * 1048576) throw new ArgumentException("Prediction capacity exceeds 256 MiB"); },
            new LayerDescriptor { Type = "prediction-cache", Title = LayerTexts.Get("prediction.title"), Icon = "predict-path",
                Description = LayerTexts.Get("prediction.description"),
                Fields = new()
                {
                    new() { Path = "Parameters.CapacityBytes", Kind = LayerFieldKind.Integer, Scale = 1048576,
                        Minimum = 1, Maximum = 256, DefaultValue = "16", Title = LayerTexts.Get("prediction.capacity") },
                    new() { Path = "Parameters.Profile", Kind = LayerFieldKind.Choice, DefaultValue = ReadPredictionProfiles.Idle,
                        Title = LayerTexts.Get("prediction.profile"), Description = LayerTexts.Get("prediction.profile.description"),
                        Choices = new()
                        {
                            new() { Value = ReadPredictionProfiles.Idle, Title = LayerTexts.Get("prediction.profile.idle") },
                            new() { Value = ReadPredictionProfiles.Steady, Title = LayerTexts.Get("prediction.profile.steady") },
                            new() { Value = ReadPredictionProfiles.Eager, Title = LayerTexts.Get("prediction.profile.eager") },
                            new() { Value = ReadPredictionProfiles.Custom, Title = LayerTexts.Get("prediction.profile.custom") }
                        } },
                    // Applied only under the custom profile; each defaults to the idle tier.
                    new() { Path = "Parameters.MaxActiveDemand", Kind = LayerFieldKind.Integer, Minimum = 0, Maximum = 1024,
                        DefaultValue = "1", Title = LayerTexts.Get("prediction.demand"), Description = LayerTexts.Get("prediction.demand.description") },
                    new() { Path = "Parameters.IdleDelayMs", Kind = LayerFieldKind.Integer, Minimum = 0, Maximum = 60000,
                        DefaultValue = "250", Title = LayerTexts.Get("prediction.idle"), Description = LayerTexts.Get("prediction.idle.description") },
                    new() { Path = "Parameters.TickIntervalMs", Kind = LayerFieldKind.Integer, Minimum = 10, Maximum = 10000,
                        DefaultValue = "100", Title = LayerTexts.Get("prediction.tick"), Description = LayerTexts.Get("prediction.tick.description") },
                    new() { Path = "Parameters.MaxInFlight", Kind = LayerFieldKind.Integer, Minimum = 1, Maximum = 64,
                        DefaultValue = "1", Title = LayerTexts.Get("prediction.inflight"), Description = LayerTexts.Get("prediction.inflight.description") },
                    new() { Path = "Parameters.MaxTransitions", Kind = LayerFieldKind.Integer, Minimum = 1, Maximum = 1048576,
                        DefaultValue = "4096", Title = LayerTexts.Get("prediction.transitions"), Description = LayerTexts.Get("prediction.transitions.description") },
                    new() { Path = "Parameters.MaxFileBytes", Kind = LayerFieldKind.Integer, Scale = 1048576, Minimum = 1, Maximum = 256,
                        DefaultValue = "8", Title = LayerTexts.Get("prediction.filebytes"), Description = LayerTexts.Get("prediction.filebytes.description") },
                    new() { Path = "Parameters.RetentionMinutes", Kind = LayerFieldKind.Integer, Minimum = 0, Maximum = 10080,
                        DefaultValue = "60", Title = LayerTexts.Get("prediction.retention"), Description = LayerTexts.Get("prediction.retention.description") }
                } });
        RegisterCache("cache", (c, ctx) => new LruCacheLayer(ctx.Storage, ctx.LowerGeneration, c.CacheCapacityBytes, c.Compression),
            c => { if (c.CacheCapacityBytes <= 0) throw new ArgumentException($"cache '{c.Id}' requires positive capacity"); }, BuiltInLayerDescriptors.Storage("cache", true));
        RegisterCache("memory-cache", (c, ctx) => new MemoryCacheLayer(MemoryCapacity(c), MemoryBlockSize(c), ctx.LowerGeneration),
            c =>
            {
                long capacity = MemoryCapacity(c); int block = MemoryBlockSize(c);
                if (block < 4096 || block > 1048576 || (block & (block - 1)) != 0 || capacity < block)
                    throw new ArgumentException("memory-cache requires capacity >= block size and a power-of-two block size (4 KiB–1 MiB)");
            }, BuiltInLayerDescriptors.Memory());
    }
    private static long MemoryCapacity(LayerConfig config) => config.Parameters.TryGetValue("CapacityBytes", out var value) ? value.GetInt64() : 16 * 1048576;

    private static string Text(LayerConfig config, string key) =>
        config.Parameters.TryGetValue(key, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()! : "";

    private static int Number(LayerConfig config, string key, int fallback) =>
        config.Parameters.TryGetValue(key, out var value) &&
        value.ValueKind == System.Text.Json.JsonValueKind.Number ? value.GetInt32() : fallback;

    internal static ReadPredictionOptions PredictionOptionsForTest(LayerConfig config) => CustomPrediction(config);

    /// <summary>Options for the <c>custom</c> pacing profile; each field falls back to the
    /// tier it is most likely to be tuned from.</summary>
    private static ReadPredictionOptions CustomPrediction(LayerConfig config)    {
        var baseline = ReadPredictionProfiles.IdleOptions();
        baseline.TickIntervalMs = Number(config, "TickIntervalMs", baseline.TickIntervalMs);
        baseline.IdleDelayMs = Number(config, "IdleDelayMs", baseline.IdleDelayMs);
        baseline.MaxActiveDemandForPreload = Number(config, "MaxActiveDemand", baseline.MaxActiveDemandForPreload);
        baseline.MaxInFlight = Number(config, "MaxInFlight", baseline.MaxInFlight);
        baseline.MaxTransitions = Number(config, "MaxTransitions", baseline.MaxTransitions);
        baseline.RetentionMs = Number(config, "RetentionMinutes", baseline.RetentionMs / 60000) * 60000;
        if (config.Parameters.TryGetValue("MaxFileBytes", out var bytes) && bytes.ValueKind == System.Text.Json.JsonValueKind.Number)
            baseline.MaxFileBytes = bytes.GetInt64() * 1048576;
        return baseline;
    }
    private static int MemoryBlockSize(LayerConfig config) => config.Parameters.TryGetValue("BlockSize", out var value)
        ? value.ValueKind == System.Text.Json.JsonValueKind.String ? int.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : value.GetInt32() : 65536;
    public static void Register(string type, Func<LayerConfig, LayerBuildContext, ILayer> create, Action<LayerConfig>? validate = null)
        => Register(type, create, validate, null);
    public static void RegisterCache<T>(string type, Func<LayerConfig, LayerBuildContext, T> create,
        Action<LayerConfig>? validate, LayerDescriptor descriptor) where T : CacheLayer
    {
        CacheLayer.AddManagementActions(descriptor);
        Register(type, (config, context) => create(config, context), validate, descriptor);
    }
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
