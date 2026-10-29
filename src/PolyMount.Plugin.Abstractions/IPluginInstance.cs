using PolyMount.Plugins.Ipc;

namespace PolyMount.Plugins;

/// <summary>The mount a command or a notice is about, as the plugin sees it.
///
/// A plugin command arrives addressed to one instance, and a notice is advice about one; both
/// need to say which it is and to look at what it is made of. That is all this view offers: the
/// instance's identity, its configured layers, the layers actually built, and the status every
/// front end already reads. The engine's own instance type holds far more — the chains it built,
/// the metrics it collects, the mount it owns — and none of that is a plugin's business, which is
/// what lets the engine change without the contract moving.
/// </summary>
public interface IPluginInstance
{
    /// <summary>The instance's stable identifier: the name its store, log and pipe are derived
    /// from. It does not change when the instance is renamed.</summary>
    string Id { get; }

    /// <summary>The instance's display name, as the configuration gives it.</summary>
    string Name { get; }

    /// <summary>The layers as configured, in chain order, including ones that are disabled and
    /// so were never built.</summary>
    IReadOnlyList<LayerConfig> LayerConfigs { get; }

    /// <summary>The layers actually built, keyed by the configuration id each came from. A
    /// plugin finds its own layers here — that is how a source answers questions about itself
    /// without the host keeping a second table.</summary>
    IReadOnlyDictionary<string, ILayer> Layers { get; }

    /// <summary>The instance's status, the same answer a front end gets: what is mounted where,
    /// which layers are in the chain, what each of them is doing.</summary>
    MountStatus GetStatus();
}
