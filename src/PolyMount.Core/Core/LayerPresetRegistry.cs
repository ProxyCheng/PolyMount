namespace PolyMount.Core;

public sealed class LayerPreset
{
    public string Id { get; set; } = "";
    public Dictionary<string, string> Titles { get; set; } = new();
    public List<LayerConfig> Layers { get; set; } = new();
}

public static class LayerPresetRegistry
{
    private static readonly Dictionary<string, LayerPreset> Presets = new(StringComparer.OrdinalIgnoreCase);
    static LayerPresetRegistry()
    {
        Register(new() { Id = "local-directory", Titles = new() { ["zh-CN"] = "本地目录 + 本地修改 + 内存缓存", ["en"] = "Local directory + overlay + memory cache" },
            Layers = new() { new() { Id = "memory-cache", Type = "memory-cache" },
                new() { Id = "overlay", Type = "overlay" }, new() { Id = "dir", Type = "dir", Source = new() { Type = "dir" } } } });
    }
    public static void Register(LayerPreset preset) => Presets[preset.Id] = preset;
    public static IReadOnlyList<LayerPreset> All => Presets.Values.ToArray();
    internal static void Unregister(string id) => Presets.Remove(id);
}
