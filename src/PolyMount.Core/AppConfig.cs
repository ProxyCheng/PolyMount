using System.Text.Json;
using System.Text.Json.Serialization;
using PolyMount.Core;

namespace PolyMount;

public sealed class AppConfig
{
    public string WinFspBin { get; set; } = @"C:\Program Files (x86)\WinFsp\bin";
    public string ControlPipe { get; set; } = "PolyMount.Control";
    public List<MountProfile> Mounts { get; set; } = new();
    public List<PluginConfig> Plugins { get; set; } = new();

    // Optional per-layer color overrides (key = layer name/type e.g. "overlay"/"svn"/"dir",
    // value = "#RRGGBB"). Missing keys use the deterministic auto color.
    public Dictionary<string, string> LayerColors { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static AppConfig LoadOrCreate(string path)
    {
        if (File.Exists(path))
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? Default();

        var fresh = Default();
        Save(path, fresh);
        return fresh;
    }

    public static void Save(string path, AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(config, Options));
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static AppConfig FromJson(string json) =>
        JsonSerializer.Deserialize<AppConfig>(json) ?? throw new ArgumentException("invalid config json");

    /// <summary>Basic validation; throws <see cref="ArgumentException"/> on problems.</summary>
    public void Validate()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in Mounts)
        {
            if (string.IsNullOrWhiteSpace(m.Name)) throw new ArgumentException("a mount has an empty Name");
            if (!names.Add(m.Name)) throw new ArgumentException("duplicate mount name: " + m.Name);
            if (string.IsNullOrWhiteSpace(m.MountPoint)) throw new ArgumentException($"mount '{m.Name}' has no MountPoint");
            m.ValidateLayers();
        }
        foreach (var kv in LayerColors)
            if (!PolyMount.Core.LayerColors.TryNormalizeHex(kv.Value, out _))
                throw new ArgumentException($"invalid color '{kv.Value}' for layer '{kv.Key}'");
    }

    public static AppConfig Default() => new();
}

public sealed class PluginConfig
{
    public string AssemblyPath { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class MountProfile
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string MountPoint { get; set; } = "";
    public string StoreRoot { get; set; } = "";
    public long CacheCapacityBytes { get; set; } = 4L * 1024 * 1024 * 1024;
    public SourceConfig Source { get; set; } = new();

    public List<LayerConfig>? Layers { get; set; } = new();

    public string GetStoreRoot() => string.IsNullOrWhiteSpace(StoreRoot)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PolyMount", "stores",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Name)))[..16])
        : StoreRoot;
    public string GetLayerStorage(LayerConfig layer) => string.IsNullOrWhiteSpace(layer.Path)
        ? Path.Combine(GetStoreRoot(), layer.Id) : layer.Path;

    public List<LayerConfig> GetLayers() => Layers ?? new();

    public void ValidateLayers()
    {
        var layers = GetLayers();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in layers)
        {
            if (string.IsNullOrWhiteSpace(layer.Id) || layer.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
                throw new ArgumentException($"mount '{Name}' has an invalid layer ID '{layer.Id}'");
            if (!ids.Add(layer.Id)) throw new ArgumentException($"duplicate layer ID '{layer.Id}' in '{Name}'");
            if (!LayerRegistry.Contains(layer.Type))
                throw new ArgumentException($"unknown layer type '{layer.Type}' in '{Name}'");
            if (!layer.Enabled) continue;
            LayerRegistry.Validate(layer);
            if (!string.IsNullOrEmpty(layer.Color) && !Core.LayerColors.TryNormalizeHex(layer.Color, out _))
                throw new ArgumentException($"invalid color for layer '{layer.Id}'");
        }
    }
}

public sealed class LayerConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Type { get; set; } = "overlay";
    public string Path { get; set; } = "";
    public long CacheCapacityBytes { get; set; } = 4L * 1024 * 1024 * 1024;
    public SourceConfig Source { get; set; } = new();
    public string Color { get; set; } = "";
    public bool Compression { get; set; }
}

public sealed class SourceConfig
{
    public string Type { get; set; } = "svn";
    public string Url { get; set; } = "";
    public long PinnedRevision { get; set; } = 0;
    public string TrustServerCertFailures { get; set; } = "unknown-ca,cn-mismatch,expired,not-yet-valid,other";
    public string? Username { get; set; }
    public string? Password { get; set; }

    // Used by the "dir" source: the local directory this layer maps.
    public string Path { get; set; } = "";

    // Used by the "dir" source: allow writing straight through to the backing directory.
    public bool Writable { get; set; } = false;
}
