using PolyMount.Core.Ipc;

namespace PolyMount.Core;

/// <summary>One command a plugin answers, with the instance it was addressed to. A plugin
/// command is about one mount, like the commands Core already routes that way (pin, probe,
/// recording), so it arrives with the instance rather than with a process.</summary>
public sealed record PluginCommand(MountInstance Instance, string Rel, ControlRequest Request, ControlResponse Response);

/// <summary>
/// What a plugin may add to the product. Every extension point is reached through this one
/// object, so a plugin is not limited to being a data source: it can register layers of its own,
/// sources for a protocol, presets that appear in the editor, a prediction strategy that runs
/// inside the shared prediction layer, and commands the control pipe answers.
/// <para>
/// A layer that declares actions in its descriptor can also implement
/// <see cref="ILayerActionHandler"/>; the mount asks it before falling back to the actions it
/// knows by type, so a plugin's own actions become executable without Core learning about them.
/// </para>
/// </summary>
public interface IPluginContext
{
    /// <summary>A layer type. The descriptor is what the configuration editor shows.</summary>
    void RegisterLayer(string type, Func<LayerConfig, LayerBuildContext, ILayer> create,
        Action<LayerConfig>? validate = null, LayerDescriptor? descriptor = null);

    /// <summary>A layer type whose instances are caches: the descriptor gains the standard
    /// clear-cache action in addition to whatever the plugin declares.</summary>
    void RegisterCacheLayer<T>(string type, Func<LayerConfig, LayerBuildContext, T> create,
        Action<LayerConfig>? validate = null, LayerDescriptor? descriptor = null) where T : CacheLayer;

    /// <summary>A data-source type: what a mount's bottom layer speaks.</summary>
    void RegisterSource(string type, Func<SourceConfig, string, ILayer> create,
        Action<SourceConfig>? validate = null, LayerDescriptor? descriptor = null);

    /// <summary>A chain the configuration editor offers as a preset.</summary>
    void RegisterPreset(LayerPreset preset);

    /// <summary>A strategy the shared prediction layer can host, selected by name through that
    /// layer's <c>Strategies</c> parameter.</summary>
    void RegisterPredictionStrategy(string name, Func<PredictionStrategyContext, IPredictionModel> factory);

    /// <summary>A command the control pipe routes to the instance that owns the request.</summary>
    void RegisterCommand(string command, Action<PluginCommand> handler);

    /// <summary>One operation of the control plane: what a front-end built on
    /// <see cref="ControlOperations"/> offers as a tool. It runs in the front-end's process and
    /// reaches the instance through the control link, so the handler behind it is usually a
    /// command registered here as well.</summary>
    void RegisterControlOperation(ControlOperation operation);

    /// <summary>Something this plugin wants a person told about the instance, checked whenever a
    /// front end asks (the tray does, on a timer). What a notice means is the plugin's business:
    /// Core carries it, a front end shows it, and the person can dismiss it for good — so a plugin
    /// can advise about a condition only it can recognise without Core learning what that is.
    /// </summary>
    void RegisterNotices(Func<MountInstance, IReadOnlyList<PluginNotice>> provide);
}

/// <summary>
/// One thing a plugin wants said about a mount, with an optional action the plugin itself will
/// carry out.
/// <para>
/// <see cref="Id"/> is what a front end remembers when the reader says "don't tell me again", so it
/// has to name the subject — one program, one condition — and not the occasion. </para>
/// <para>
/// <see cref="Action"/> is a command the front end sends back to the same instance when the reader
/// accepts: the plugin decides what to ask for, and neither Core nor the window has to know what
/// the plugin is about. A notice without one is information.
/// </para>
/// </summary>
public sealed record PluginNotice(
    string Id,
    LocalizedText Title,
    LocalizedText Body,
    LocalizedText? ActionLabel = null,
    ControlRequest? Action = null);

/// <summary>The advisory providers plugins registered. A front end asks for the notices of an
/// instance; each provider is given that instance and answers for itself.</summary>
public static class PluginNotices
{
    private static readonly object Gate = new();
    private static readonly List<Func<MountInstance, IReadOnlyList<PluginNotice>>> Providers = new();

    public static void Register(Func<MountInstance, IReadOnlyList<PluginNotice>> provide)
    {
        ArgumentNullException.ThrowIfNull(provide);
        lock (Gate) Providers.Add(provide);
    }

    public static bool Any { get { lock (Gate) return Providers.Count > 0; } }

    /// <summary>Every notice the plugins have for this instance, in registration order. A provider
    /// that throws is skipped: advice is not worth failing a status poll over.</summary>
    public static IReadOnlyList<PluginNotice> Collect(MountInstance instance)
    {
        Func<MountInstance, IReadOnlyList<PluginNotice>>[] providers;
        lock (Gate) providers = Providers.ToArray();
        var notices = new List<PluginNotice>();
        foreach (var provide in providers)
        {
            try { notices.AddRange(provide(instance)); }
            catch (Exception) { /* a provider with nothing to say, or nothing it can say */ }
        }
        return notices;
    }

    /// <summary>Drops every provider. They all belong to plugins, so unloading is all or nothing —
    /// the same rule as the commands, which are tracked by name because a front end names them.</summary>
    internal static void Clear() { lock (Gate) Providers.Clear(); }
}

/// <summary>A layer whose descriptor declares actions of its own: the mount asks the layer
/// itself before it falls back to the actions it knows by type.</summary>
public interface ILayerActionHandler
{
    /// <summary>Applies one action of this layer's descriptor. False means the layer does not
    /// know the action, which is what lets the mount fall back to its own dispatch.</summary>
    bool TryApplyAction(string rel, string action);
}

/// <summary>The commands plugins answer, keyed by name. Core registers none; every name here
/// came from a plugin and is routed to the instance the request names.</summary>
public static class PluginCommands
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Action<PluginCommand>> Handlers = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(string command, Action<PluginCommand> handler)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("A plugin command needs a name", nameof(command));
        ArgumentNullException.ThrowIfNull(handler);
        lock (Gate) Handlers[command] = handler;
    }

    public static IReadOnlyList<string> Names { get { lock (Gate) return Handlers.Keys.ToArray(); } }

    public static bool Handles(string command) { lock (Gate) return Handlers.ContainsKey(command); }

    /// <summary>Applies the plugin command, and reports whether one was registered: a caller
    /// that reaches here with a name nobody registered has a programming error, not a plugin's.</summary>
    public static bool TryApply(PluginCommand command)
    {
        Action<PluginCommand>? handler;
        lock (Gate) Handlers.TryGetValue(command.Request.Cmd, out handler);
        if (handler == null) return false;
        handler(command);
        return true;
    }

    internal static void Unregister(string command) { lock (Gate) Handlers.Remove(command); }
}

/// <summary>The context handed to one plugin while it registers. It delegates to the same
/// registries Core uses for its own layers, so there is one table per extension point and not
/// a plugin copy beside a built-in one.</summary>
internal sealed class PluginContext : IPluginContext
{
    public void RegisterLayer(string type, Func<LayerConfig, LayerBuildContext, ILayer> create,
        Action<LayerConfig>? validate = null, LayerDescriptor? descriptor = null) =>
        LayerRegistry.Register(type, create, validate, descriptor);

    public void RegisterCacheLayer<T>(string type, Func<LayerConfig, LayerBuildContext, T> create,
        Action<LayerConfig>? validate = null, LayerDescriptor? descriptor = null) where T : CacheLayer
    {
        if (descriptor != null) CacheLayer.AddManagementActions(descriptor);
        LayerRegistry.Register(type, (config, context) => create(config, context), validate, descriptor);
    }

    public void RegisterSource(string type, Func<SourceConfig, string, ILayer> create,
        Action<SourceConfig>? validate = null, LayerDescriptor? descriptor = null) =>
        SourceRegistry.Register(type, create, validate, descriptor);

    public void RegisterPreset(LayerPreset preset) => LayerPresetRegistry.Register(preset);
    public void RegisterPredictionStrategy(string name, Func<PredictionStrategyContext, IPredictionModel> factory) =>
        PredictionStrategyRegistry.Register(name, factory);
    public void RegisterCommand(string command, Action<PluginCommand> handler) =>
        PluginCommands.Register(command, handler);
    public void RegisterControlOperation(ControlOperation operation) => ControlOperations.Register(operation);
    public void RegisterNotices(Func<MountInstance, IReadOnlyList<PluginNotice>> provide) => PluginNotices.Register(provide);
}
