using System.Text.Json;

namespace PolyMount.Plugins;

/// <summary>What a prediction strategy is handed when the layer builds it: the chain below the
/// layer, where its learned state may live, the way to ask the layer for a speculative fetch,
/// and the layer's own configuration — a plugin's tuning lives in the layer's
/// <c>Parameters</c>, under keys the plugin names.</summary>
public sealed record PredictionStrategyContext(
    ILowerChain Lower,
    string ModelPath,
    PreloadRequest Preload,
    PredictionTuning Tuning,
    IReadOnlyDictionary<string, JsonElement> Parameters);
