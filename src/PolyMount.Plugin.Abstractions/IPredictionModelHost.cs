namespace PolyMount.Plugins;

/// <summary>A layer that hosts a prediction strategy.
///
/// The prediction layer is part of the engine — it owns the held cache, the per-stream quota,
/// the eviction and the write invalidation — and a plugin contributes only the rules. A plugin
/// whose rules also answer questions (a command that reports what its family table learned) needs
/// to find its own strategy inside whichever layer was built for it, and this is the whole of
/// what it needs to do that: the strategy, whichever one it is. Comparing it to the type the
/// plugin registered is the plugin's business.
/// </summary>
public interface IPredictionModelHost
{
    /// <summary>The strategy this layer hosts, or null when it has not been built yet.</summary>
    IPredictionModel? Strategy { get; }
}
