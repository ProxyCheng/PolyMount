using System.Text.Json;

namespace PolyMount.Core;

/// <summary>
/// The strategies a prediction layer can host. Core ships successor, stride, spatial and
/// co-read; a plugin adds names of its own, and they are selected through the same
/// <c>Strategies</c> parameter. A strategy added here shares the layer's held cache, per-stream
/// quota, quality feedback, write invalidation and statistics instead of bringing a second copy
/// of any of them — which is the reason a format-aware strategy is a name in that list rather
/// than a layer of its own.
/// </summary>
public static class PredictionStrategyRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Func<PredictionStrategyContext, IPredictionModel>> Factories =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds or replaces one strategy. A name Core already ships is not overridden:
    /// the layer's own strategies are selected first.</summary>
    public static void Register(string name, Func<PredictionStrategyContext, IPredictionModel> factory)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A prediction strategy needs a name", nameof(name));
        ArgumentNullException.ThrowIfNull(factory);
        lock (Gate) Factories[name] = factory;
    }

    public static IReadOnlyList<string> Names { get { lock (Gate) return Factories.Keys.ToArray(); } }

    internal static bool TryCreate(string name, PredictionStrategyContext context, out IPredictionModel? model)
    {
        Func<PredictionStrategyContext, IPredictionModel>? factory;
        lock (Gate) Factories.TryGetValue(name, out factory);
        model = factory?.Invoke(context);
        return model != null;
    }

    internal static void Unregister(string name) { lock (Gate) Factories.Remove(name); }
}
