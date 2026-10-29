namespace PolyMount.Plugins;

/// <summary>The entry point of a plugin assembly. Everything a plugin contributes is added
/// through the context it is given — layers, sources, presets, prediction strategies and
/// commands — so a plugin is not constrained to being a data source or a layer.</summary>
public interface IPolyMountPlugin { void Register(IPluginContext context); }

/// <summary>The old name of <see cref="IPolyMountPlugin"/>. A plugin no longer has to provide a
/// layer, so the name no longer describes the contract. This alias is kept only so an assembly
/// built against an earlier release still loads; new plugins implement the new interface.</summary>
[Obsolete("Implement IPolyMountPlugin; this alias exists only so older plugin assemblies still load.")]
public interface ILayerPlugin : IPolyMountPlugin { }

/// <summary>What a plugin calls itself in a window. Optional: a plugin that does not implement
/// this is shown by its file name, which is what every plugin looked like before there was
/// anything else to show. The title is the short human name ("SVN repository"); the description
/// is the sentence a reader gets on hover; the icon is one of the tray's line-icon names, so a
/// plugin chooses a mark without shipping a picture — and the scheme still tints it.</summary>
public interface IPluginMetadata
{
    LocalizedText Title { get; }
    LocalizedText Description { get; }
    string Icon { get; }
}
