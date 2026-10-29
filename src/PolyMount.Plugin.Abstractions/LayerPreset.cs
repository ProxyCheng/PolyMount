namespace PolyMount.Plugins;

/// <summary>A whole chain the configuration editor offers as a starting point.
///
/// A preset is how a layer type actually reaches a person: registering the type makes it
/// available, but a preset is what the editor can put in front of someone who has not read the
/// manual. A plugin that adds a layer type without adding a preset leaves it invisible.
/// </summary>
public sealed class LayerPreset
{
    public string Id { get; set; } = "";
    public Dictionary<string, string> Titles { get; set; } = new();
    public List<LayerConfig> Layers { get; set; } = new();
}
