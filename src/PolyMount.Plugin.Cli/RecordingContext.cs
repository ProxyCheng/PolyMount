using System.Reflection;
using System.Runtime.Loader;

namespace PolyMount.Plugins;

/// <summary>Records what a plugin registers, so a person can see what they built.
///
/// <c>verify</c> calls the plugin's own <c>Register</c> against this. It answers nothing: no layer
/// is built, no source is opened, no command runs. What it produces is the list — which is exactly
/// what the host would learn from the same call, minus the host.
/// </summary>
internal sealed class RecordingContext : IPluginContext
{
    public List<string> Layers { get; } = new();
    public List<string> Sources { get; } = new();
    public List<string> Presets { get; } = new();
    public List<string> Strategies { get; } = new();
    public List<string> Commands { get; } = new();
    public List<string> Operations { get; } = new();
    public List<string> CredentialKinds { get; } = new();
    public List<string> CredentialConsumers { get; } = new();
    public List<string> Panels { get; } = new();
    public bool Notices { get; private set; }

    public bool IsEmpty => Layers.Count + Sources.Count + Presets.Count + Strategies.Count + Commands.Count
        + Operations.Count + CredentialKinds.Count + CredentialConsumers.Count + Panels.Count == 0 && !Notices;

    public void RegisterLayer(string type, Func<LayerConfig, LayerBuildContext, ILayer> create,
        Action<LayerConfig>? validate = null, LayerDescriptor? descriptor = null) => Layers.Add(type);

    public void RegisterCacheLayer<T>(string type, Func<LayerConfig, LayerBuildContext, T> create,
        Action<LayerConfig>? validate = null, LayerDescriptor? descriptor = null) where T : CacheLayer => Layers.Add(type);

    public void RegisterSource(string type, Func<SourceConfig, string, ILayer> create,
        Action<SourceConfig>? validate = null, LayerDescriptor? descriptor = null) => Sources.Add(type);

    public void RegisterPreset(LayerPreset preset) => Presets.Add(preset.Id);
    public void RegisterPredictionStrategy(string name, Func<PredictionStrategyContext, IPredictionModel> factory) => Strategies.Add(name);
    public void RegisterCommand(string command, Action<PluginCommand> handler) => Commands.Add(command);
    public void RegisterCredentialKind(CredentialKindDescriptor descriptor) => CredentialKinds.Add(descriptor.Kind);
    public void RegisterCredentialConsumer(string type, Func<Credential, bool> accepts) => CredentialConsumers.Add(type);
    public void RegisterControlOperation(ControlOperation operation) => Operations.Add(operation.Name);
    public void RegisterNotices(Func<IPluginInstance, IReadOnlyList<PluginNotice>> provide) => Notices = true;
    public void RegisterPanel(PluginPanel panel) => Panels.Add(panel.Id);
}

/// <summary>Loads a plugin assembly from its own directory, the way the host does, so a plugin
/// whose dependencies sit beside it can be checked without installing it anywhere.</summary>
internal sealed class PluginLoadContext(string directory) : AssemblyLoadContext(isCollectible: false)
{
    private static readonly string Contract = typeof(IPolyMountPlugin).Assembly.GetName().Name!;

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // The contract is this process's own copy: a plugin checked against a different one would
        // be checking something nobody will run.
        if (assemblyName.Name == Contract) return null;
        string candidate = Path.Combine(directory, assemblyName.Name + ".dll");
        return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
    }
}
