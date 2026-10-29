namespace PolyMount.Core;

/// <summary>
/// The engine's chain, reached from the contract's view of it.
///
/// The contract declares what any layer may do with the chain; a few layers built into the engine
/// need more than that — the warm-up builds a second, narrower chain over the same layers, and the
/// prediction layer preloads through <c>LoadObject</c>, which is an engine operation rather than a
/// file-system one. Those layers are always attached by the mount, which passes its own
/// <see cref="VirtualFileSystem"/>, so the cast is an invariant rather than a guess; stating it
/// here keeps the reason in one place instead of at every use.
/// </summary>
internal static class LowerChain
{
    public static VirtualFileSystem FileSystem(ILowerChain chain) =>
        chain as VirtualFileSystem ??
        throw new ArgumentException($"this layer needs the engine's chain, not {chain.GetType().Name}", nameof(chain));
}
