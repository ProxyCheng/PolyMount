using PolyMount.Plugins;

namespace PolyMount.Plugin.Puppet;

/// <summary>A plugin that exists only to be installed, kept, listed and removed by something else.
///
/// The upgrade paths — the update script's sweep, the setup's plugin page — are about plugins
/// somebody installed by hand, and testing them with a stand-in file would exercise the file
/// handling while missing what makes the case real. This is a genuine plugin: it loads, it
/// registers a layer type of its own, and nothing depends on it. It is never published and never
/// shipped; it lives under <c>tests/plugins</c> so neither the plugin publish script nor the
/// setup's payload picks it up.
/// </summary>
public sealed class PuppetPlugin : IPolyMountPlugin, IPluginMetadata
{
    /// <summary>The layer type it registers, and the evidence it really loaded.</summary>
    public const string Type = "puppet";

    public LocalizedText Title => new("Puppet plugin", "傀儡插件");
    public LocalizedText Description => new(
        "A plugin used by tests to stand in for one installed by hand.",
        "供测试使用的插件，用来代替手工安装的第三方插件。");
    public string Icon => "plugin";

    public void Register(IPluginContext context) => context.RegisterLayer(Type,
        (config, build) => new Layer(config, build), null, new LayerDescriptor
        {
            Type = Type,
            Title = Title,
            Description = Description
        });

    /// <summary>A layer that has nothing and says so: it answers every path with "not mine", so
    /// putting it in a chain changes nothing about what that chain serves.</summary>
    private sealed class Layer(LayerConfig config, LayerBuildContext build) : ILayer
    {
        public string Name => config.Id;
        public string Generation => build.LowerGeneration();
        public bool Writable => false;

        public bool TryGetEntry(string path, out VfsEntry entry)
        {
            entry = null!;
            return false;
        }

        public IReadOnlyList<VfsEntry> ListOwn(string path) => Array.Empty<VfsEntry>();

        public bool TryOpenRead(string path, out VfsEntry entry, out Stream stream)
        {
            entry = null!;
            stream = null!;
            return false;
        }
    }
}
