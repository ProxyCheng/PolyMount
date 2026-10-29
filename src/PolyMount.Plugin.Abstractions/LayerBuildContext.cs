namespace PolyMount.Plugins;

/// <summary>What a layer factory is told about the instance it is being built for: where it may
/// keep its own data, where it may keep state that describes the current mount, and how to ask
/// what the chain beneath it considers its generation.
///
/// Storage and state are directories the host has already created for this layer id, so a layer
/// never has to guess where it lives or share a directory with another one.</summary>
public sealed record LayerBuildContext(string Storage, string State, Func<string> LowerGeneration);
