using PolyMount.Layers;

namespace PolyMount.Core;

/// <summary>Provider construction and parameter validation, independent of chain layout.</summary>
public static class LayerRegistry
{
    private sealed record Registration(Func<LayerConfig, LayerBuildContext, ILayer> Create, Action<LayerConfig>? Validate, LayerDescriptor? Descriptor);
    private static readonly Dictionary<string, Registration> Registrations = new(StringComparer.OrdinalIgnoreCase);
    static LayerRegistry()
    {
        Register("overlay", (c, ctx) => new OverlayLayer(ctx.Storage, c.Compression), null, BuiltInLayerDescriptors.Storage("overlay", false));
        RegisterCache("prediction-cache", (c, ctx) => new PredictionCacheLayer(PredictionCapacity(c), ctx.LowerGeneration,
            ReadPredictionProfiles.Resolve(Text(c, "Profile"), () => CustomPrediction(c)),
            CoReadOptionsFor(c), StrategiesFor(c), Path.Combine(ctx.State, "prediction.json"), c),
            c => { if (PredictionCapacity(c) < 1 || PredictionCapacity(c) > 256L * 1048576) throw new ArgumentException("Prediction capacity exceeds 256 MiB"); },
            new LayerDescriptor { Type = "prediction-cache", Title = LayerTexts.Get("prediction.title"), Icon = "predict-path",
                Description = LayerTexts.Get("prediction.description"),
                Fields = new()
                {
                    new() { Path = "Parameters.CapacityBytes", Kind = LayerFieldKind.Integer, Scale = 1048576,
                        Minimum = 1, Maximum = 256, DefaultValue = "256", Title = LayerTexts.Get("prediction.capacity") },
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
                        DefaultValue = "60", Title = LayerTexts.Get("prediction.retention"), Description = LayerTexts.Get("prediction.retention.description") },
                    new() { Path = "Parameters.MaxSuccessors", Kind = LayerFieldKind.Integer, Minimum = 1, Maximum = 16,
                        DefaultValue = "4", Title = LayerTexts.Get("prediction.successors"), Description = LayerTexts.Get("prediction.successors.description") },
                    new() { Path = "Parameters.MinConfidencePercent", Kind = LayerFieldKind.Integer, Minimum = 0, Maximum = 100,
                        DefaultValue = "60", Title = LayerTexts.Get("prediction.confidence"), Description = LayerTexts.Get("prediction.confidence.description") },
                    new() { Path = "Parameters.ContextDepth", Kind = LayerFieldKind.Integer, Minimum = 1, Maximum = 4,
                        DefaultValue = "2", Title = LayerTexts.Get("prediction.context"), Description = LayerTexts.Get("prediction.context.description") },
                    new() { Path = "Parameters.PrefetchDepth", Kind = LayerFieldKind.Integer, Minimum = 1, Maximum = 8,
                        DefaultValue = "2", Title = LayerTexts.Get("prediction.depth"), Description = LayerTexts.Get("prediction.depth.description") }
                    // `Parameters.Strategies` is deliberately not a field: its value is a
                    // comma-separated list of model names (successor, stride, spatial, co-read,
                    // plus whatever a plugin registered), and choosing among those is a preset's
                    // job. Configurations and presets that set it still work; the form leaves the
                    // parameter alone.
                } });
        RegisterCache("cache", (c, ctx) => new LruCacheLayer(ctx.Storage, ctx.LowerGeneration, c.CacheCapacityBytes,
                c.Compression, writable: Bool(c, "Writable", true)),
            c => { if (c.CacheCapacityBytes <= 0) throw new ArgumentException($"cache '{c.Id}' requires positive capacity"); }, BuiltInLayerDescriptors.Storage("cache", true));
        Register(ListingWarmupLayer.Type, (c, ctx) => new ListingWarmupLayer(ctx.LowerGeneration,
                Number(c, "Limit", 2000), Number(c, "Seconds", 60), Number(c, "QuietMs", 250)),
            c =>
            {
                if (Number(c, "Limit", 2000) is < 0 or > 1_000_000) throw new ArgumentException("warmup limit must be 0-1000000");
                if (Number(c, "Seconds", 60) is < 0 or > 86400) throw new ArgumentException("warmup seconds must be 0-86400");
                if (Number(c, "QuietMs", 250) is < 0 or > 60000) throw new ArgumentException("warmup quiet period must be 0-60000 ms");
            }, new LayerDescriptor
            {
                Type = ListingWarmupLayer.Type, Title = LayerTexts.Get("warmup.title"), Icon = "warm-listings",
                Description = LayerTexts.Get("warmup.description"),
                // The two budgets are what the old WarmListings fields meant; the quiet period
                // was a constant until the layer needed a field for it.
                Fields = new()
                {
                    new() { Path = "Parameters.Limit", Kind = LayerFieldKind.Integer, Minimum = 0, Maximum = 1_000_000,
                        DefaultValue = "2000", Title = LayerTexts.Get("warmup.limit"), Description = LayerTexts.Get("warmup.limit.description") },
                    new() { Path = "Parameters.Seconds", Kind = LayerFieldKind.Integer, Minimum = 0, Maximum = 86400,
                        DefaultValue = "60", Title = LayerTexts.Get("warmup.seconds"), Description = LayerTexts.Get("warmup.seconds.description") },
                    new() { Path = "Parameters.QuietMs", Kind = LayerFieldKind.Integer, Minimum = 0, Maximum = 60000,
                        DefaultValue = "250", Title = LayerTexts.Get("warmup.quiet"), Description = LayerTexts.Get("warmup.quiet.description") }
                }
            });
        RegisterCache("memory-cache", (c, ctx) => new MemoryCacheLayer(MemoryCapacity(c), MemoryBlockSize(c), ctx.LowerGeneration),
            c =>
            {
                long capacity = MemoryCapacity(c); int block = MemoryBlockSize(c);
                if (block < 4096 || block > 1048576 || (block & (block - 1)) != 0 || capacity < block)
                    throw new ArgumentException("memory-cache requires capacity >= block size and a power-of-two block size (4 KiB–1 MiB)");
            }, BuiltInLayerDescriptors.Memory());
    }
    /// <summary>The held budget the prediction layer falls back to. 256 MiB is the ceiling its
    /// validator allows, and it is where the measurement pointed: at 16 MiB the layer held 456 KB
    /// against a workload whose reads covered hundreds of megabytes in seconds, so a preloaded
    /// file was evicted before the loader asked for it -- 551 entries evicted unread against 162
    /// read (2026-10-15). Preloading can only move a source read earlier, never remove it, so the
    /// budget buys exactly the window between guessing and being asked.</summary>
    private const long PredictionCapacityDefault = 256 * 1048576;
    private static long MemoryCapacity(LayerConfig config) => config.Parameters.TryGetValue("CapacityBytes", out var value) ? value.GetInt64() : 16 * 1048576;
    /// <summary>The prediction layer's own budget: the same parameter and the same validator, a
    /// different fallback. Sharing <see cref="MemoryCapacity"/>'s 16 MiB with the block cache was
    /// what kept the predictor from holding anything it had already fetched.</summary>
    private static long PredictionCapacity(LayerConfig config) => config.Parameters.TryGetValue("CapacityBytes", out var value) ? value.GetInt64() : PredictionCapacityDefault;

    private static string Text(LayerConfig config, string key) =>
        config.Parameters.TryGetValue(key, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()! : "";

    /// <summary>A boolean parameter, accepting the JSON literal the service writes and the
    /// "True"/"False" string the editor's checkbox writes.</summary>
    private static bool Bool(LayerConfig config, string key, bool fallback)
    {
        if (!config.Parameters.TryGetValue(key, out var value)) return fallback;
        return value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => fallback
        };
    }

    private static int Number(LayerConfig config, string key, int fallback) =>
        config.Parameters.TryGetValue(key, out var value) &&
        value.ValueKind == System.Text.Json.JsonValueKind.Number ? value.GetInt32() : fallback;

    /// <summary>The independent strategies a prediction layer feeds. The default keeps the
    /// shipped successor + stride + spatial behaviour; adding co-read selects the burst model.</summary>
    private static IReadOnlyList<string> StrategiesFor(LayerConfig config)
    {
        string[] defaults = { "successor", "stride", "spatial" };
        if (!config.Parameters.TryGetValue("Strategies", out var value)) return defaults;
        if (value.ValueKind == System.Text.Json.JsonValueKind.Array)
            return value.EnumerateArray().Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0).ToArray();
        if (value.ValueKind == System.Text.Json.JsonValueKind.String)
            return value.GetString()!.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return defaults;
    }

    /// <summary>Co-read budgets. The keys are prefixed so they cannot collide with the
    /// successor profile's parameters.</summary>
    private static CoReadOptions CoReadOptionsFor(LayerConfig config)
    {
        var options = new CoReadOptions();
        options.TickIntervalMs = Number(config, "CoRead.TickIntervalMs", options.TickIntervalMs);
        options.BurstGapMs = Number(config, "CoRead.BurstGapMs", options.BurstGapMs);
        options.BurstWindowMs = Number(config, "CoRead.WindowMs", options.BurstWindowMs);
        options.MaxPathsPerBurst = Number(config, "CoRead.MaxPaths", options.MaxPathsPerBurst);
        options.MaxLinks = Number(config, "CoRead.MaxLinks", options.MaxLinks);
        options.MaxCoReadsPerPath = Number(config, "CoRead.MaxPerPath", options.MaxCoReadsPerPath);
        options.MinCoReadCount = Number(config, "CoRead.MinCount", options.MinCoReadCount);
        options.MinConfidence = Number(config, "CoRead.MinConfidencePercent", (int)Math.Round(options.MinConfidence * 100)) / 100.0;
        options.MinBurstMembers = Number(config, "CoRead.MinBurstMembers", options.MinBurstMembers);
        options.MinSetOverlap = Number(config, "CoRead.MinSetOverlapPercent", (int)Math.Round(options.MinSetOverlap * 100)) / 100.0;
        options.MinConfirmations = Number(config, "CoRead.MinConfirmations", options.MinConfirmations);
        options.MaxBatchFiles = Number(config, "CoRead.MaxBatchFiles", options.MaxBatchFiles);
        options.GroupWasteStreak = Number(config, "CoRead.GroupWasteStreak", options.GroupWasteStreak);
        options.MaxInFlight = Number(config, "CoRead.MaxInFlight", options.MaxInFlight);
        options.FeedbackWindowMs = Number(config, "CoRead.FeedbackWindowMs", options.FeedbackWindowMs);
        options.FeedbackSamples = Number(config, "CoRead.FeedbackSamples", options.FeedbackSamples);
        options.RetentionMs = Number(config, "CoRead.RetentionMinutes", options.RetentionMs / 60000) * 60000;
        if (config.Parameters.TryGetValue("CoRead.MaxFileBytes", out var bytes) &&
            bytes.ValueKind == System.Text.Json.JsonValueKind.Number)
            options.MaxFileBytes = bytes.GetInt64() * 1048576;
        if (config.Parameters.TryGetValue("CoRead.MaxBatchMB", out var batch) &&
            batch.ValueKind == System.Text.Json.JsonValueKind.Number)
            options.MaxBatchBytes = batch.GetInt64() * 1048576;
        return options;
    }

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
        baseline.MaxSuccessorsPerKey = Number(config, "MaxSuccessors", baseline.MaxSuccessorsPerKey);
        baseline.ContextDepth = Number(config, "ContextDepth", baseline.ContextDepth);
        baseline.PrefetchDepth = Number(config, "PrefetchDepth", baseline.PrefetchDepth);
        baseline.FeedbackWindowMs = Number(config, "FeedbackWindowMs", baseline.FeedbackWindowMs);
        baseline.FeedbackSamples = Number(config, "FeedbackSamples", baseline.FeedbackSamples);
        baseline.MinConfidence = Number(config, "MinConfidencePercent", (int)Math.Round(baseline.MinConfidence * 100)) / 100.0;
        baseline.MaxStreams = Number(config, "MaxStreams", baseline.MaxStreams);
        if (config.Parameters.TryGetValue("SpatialEnabled", out var spatial))
            baseline.SpatialEnabled = Bool(config, "SpatialEnabled", baseline.SpatialEnabled);
        baseline.SpatialTriggerFiles = Number(config, "SpatialTriggerFiles", baseline.SpatialTriggerFiles);
        baseline.SpatialMaxFiles = Number(config, "SpatialMaxFiles", baseline.SpatialMaxFiles);
        baseline.SpatialMaxChildren = Number(config, "SpatialMaxChildren", baseline.SpatialMaxChildren);
        baseline.SpatialSmallDirectory = Number(config, "SpatialSmallDirectory", baseline.SpatialSmallDirectory);
        baseline.SpatialMinPrefix = Number(config, "SpatialMinPrefix", baseline.SpatialMinPrefix);
        baseline.SpatialCooldownMs = Number(config, "SpatialCooldownMs", baseline.SpatialCooldownMs);
        if (config.Parameters.TryGetValue("SpatialMaxBytes", out var spatialBytes) &&
            spatialBytes.ValueKind == System.Text.Json.JsonValueKind.Number)
            baseline.SpatialMaxBytes = spatialBytes.GetInt64() * 1048576;
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
