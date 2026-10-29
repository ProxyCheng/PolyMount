using PolyMount.Plugins.Ipc;

namespace PolyMount.Plugins;

/// <summary>One command a plugin answers, with the instance it was addressed to. A plugin
/// command is about one mount, like the commands Core already routes that way (pin, probe,
/// recording), so it arrives with the instance rather than with a process.</summary>
public sealed record PluginCommand(IPluginInstance Instance, string Rel, ControlRequest Request, ControlResponse Response);

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

    /// <summary>A kind of credential: what a source of this plugin presents when it authenticates
    /// in some way other than an account and a password. The descriptor gives the store its fields,
    /// the manager its rows, and the code that turns the stored values into a
    /// <see cref="Credential"/>.</summary>
    void RegisterCredentialKind(CredentialKindDescriptor descriptor);

    /// <summary>What a layer of this type can present, as a filter over the credential itself:
    /// an account with a password, a client certificate, a token. It is how a picker knows which
    /// stored records to offer for this layer — the answer travels with each record
    /// (<see cref="CredentialRecord.UseFor"/>), so a kind another plugin adds tomorrow is offered
    /// here without this one having named it, as long as the credential it builds is something
    /// this filter takes. A layer that registers none accepts nothing from a picker: a credential
    /// field on a layer that cannot say what it presents has no answer to offer.</summary>
    void RegisterCredentialConsumer(string type, Func<Credential, bool> accepts);

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
    void RegisterNotices(Func<IPluginInstance, IReadOnlyList<PluginNotice>> provide);

    /// <summary>A place in the front end that belongs to this plugin: what it is called, and how to
    /// fill it. The window draws the view the plugin hands back and sends the view's actions to the
    /// instance the panel was rendered for, so a plugin gains a management page of its own without
    /// any window learning what it manages. See <see cref="PluginPanel"/>.</summary>
    void RegisterPanel(PluginPanel panel);
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
/// <para>
/// <see cref="Automatic"/> is the plugin saying the action needs no confirmation: the reader is not
/// being asked to agree to anything, the plugin is asking to be put in front of a sign-in page.
/// A front end carries such an action out as soon as it sees the notice, once per time the
/// condition stands, and still sends the command exactly as a click would — nothing about what it
/// means moves out of the plugin. It exists for the one thing a service process cannot do itself,
/// and a notice that used it for a question would be answered before the reader saw it.
/// </para>
/// </summary>
public sealed record PluginNotice(
    string Id,
    LocalizedText Title,
    LocalizedText Body,
    LocalizedText? ActionLabel = null,
    ControlRequest? Action = null,
    bool Automatic = false);

/// <summary>A layer whose descriptor declares actions of its own: the mount asks the layer
/// itself before it falls back to the actions it knows by type.</summary>
public interface ILayerActionHandler
{
    /// <summary>Applies one action of this layer's descriptor. False means the layer does not
    /// know the action, which is what lets the mount fall back to its own dispatch.</summary>
    bool TryApplyAction(string rel, string action);
}
