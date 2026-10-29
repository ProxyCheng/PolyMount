using PolyMount.Core.Ipc;

namespace PolyMount.Core;

/// <summary>The advisory providers plugins registered. A front end asks for the notices of an
/// instance; each provider is given that instance and answers for itself.</summary>
public static class PluginNotices
{
    private static readonly object Gate = new();
    private static readonly List<Func<IPluginInstance, IReadOnlyList<PluginNotice>>> Providers = new();

    public static void Register(Func<IPluginInstance, IReadOnlyList<PluginNotice>> provide)
    {
        ArgumentNullException.ThrowIfNull(provide);
        lock (Gate) Providers.Add(provide);
    }

    public static bool Any { get { lock (Gate) return Providers.Count > 0; } }

    /// <summary>Every notice the plugins have for this instance, in registration order. A provider
    /// that throws is skipped: advice is not worth failing a status poll over.</summary>
    public static IReadOnlyList<PluginNotice> Collect(MountInstance instance)
    {
        Func<IPluginInstance, IReadOnlyList<PluginNotice>>[] providers;
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

/// <summary>The management pages the loaded plugins offer, by id.
///
/// The list is a fact about what this process loaded, so the supervisor can answer it for a front
/// end's sidebar; the page itself is filled in by the process that owns an instance, because a
/// plugin's render may read that mount. Nothing here knows what any panel is about.
/// </summary>
public static class PluginPanels
{
    private static readonly object Gate = new();
    private static readonly List<PluginPanel> Registered = new();

    /// <summary>Adds a page. Two plugins naming one id is refused at load time rather than
    /// resolved: one of them would be answering for the other's rows.</summary>
    public static void Register(PluginPanel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        if (string.IsNullOrWhiteSpace(panel.Id))
            throw new ArgumentException("A plugin panel needs an id", nameof(panel));
        lock (Gate)
        {
            if (Registered.Any(existing => string.Equals(existing.Id, panel.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("a panel with id '" + panel.Id + "' is already registered");
            Registered.Add(panel);
        }
    }

    public static IReadOnlyList<PluginPanel> All { get { lock (Gate) return Registered.ToArray(); } }

    public static IReadOnlyList<string> Ids { get { lock (Gate) return Registered.Select(p => p.Id).ToArray(); } }

    /// <summary>What the sidebar is built from: the pages, without anything that can only be
    /// answered for one instance.</summary>
    public static IReadOnlyList<PluginPanelDescriptor> Descriptors()
    {
        lock (Gate)
            return Registered.Select(p => new PluginPanelDescriptor
            {
                Id = p.Id,
                Title = p.Title,
                Icon = p.Icon,
                OwnPage = p.OwnPage
            }).ToArray();
    }

    /// <summary>Fills one page in for an instance. A render that throws becomes the panel's error
    /// rather than a failed command: a plugin's own page is not worth taking the window down for,
    /// and the reader is owed the sentence the plugin would have shown.</summary>
    public static PluginPanelView Render(string id, IPluginInstance instance)
    {
        PluginPanel? panel;
        lock (Gate) panel = Registered.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (panel == null) throw new InvalidOperationException("no plugin offers a panel called '" + id + "'");
        try { return panel.Render(instance); }
        catch (Exception ex) { return new PluginPanelView { Error = new LocalizedText(ex.Message, ex.Message) }; }
    }

    internal static void Clear() { lock (Gate) Registered.Clear(); }
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
        if (descriptor != null) CacheLayerActions.Add(descriptor);
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
    public void RegisterCredentialKind(CredentialKindDescriptor descriptor) =>
        CredentialKinds.Register(descriptor);
    public void RegisterCredentialConsumer(string type, Func<Credential, bool> accepts) =>
        CredentialConsumers.Register(type, accepts);
    public void RegisterControlOperation(ControlOperation operation) => ControlOperations.Register(operation);
    public void RegisterNotices(Func<IPluginInstance, IReadOnlyList<PluginNotice>> provide) => PluginNotices.Register(provide);
    public void RegisterPanel(PluginPanel panel) => PluginPanels.Register(panel);
}
