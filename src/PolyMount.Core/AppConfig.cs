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
        {
            var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? Default();
            // An instance's identity has to outlive the process, so one that is missing is made
            // here and written back: a store folder derived from a fresh Guid would otherwise
            // point somewhere new on every start.
            if (loaded.EnsureMountIds()) Save(path, loaded);
            return loaded;
        }

        var fresh = Default();
        Save(path, fresh);
        return fresh;
    }

    public static void Save(string path, AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        config.EnsureMountIds();
        File.WriteAllText(path, JsonSerializer.Serialize(config, Options));
    }

    /// <summary>Gives every mount the identity its store folder is derived from, and reports
    /// whether any was missing. Written down before anything is loaded from a store.</summary>
    public bool EnsureMountIds()
    {
        bool changed = false;
        foreach (var mount in Mounts) changed |= mount.EnsureId();
        return changed;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static AppConfig FromJson(string json) =>
        JsonSerializer.Deserialize<AppConfig>(json) ?? throw new ArgumentException("invalid config json");

    /// <summary>Basic validation; throws <see cref="ArgumentException"/> on problems.</summary>
    public void Validate()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stores = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in Mounts)
        {
            if (string.IsNullOrWhiteSpace(m.Name)) throw new ArgumentException("a mount has an empty Name");
            if (!names.Add(m.Name)) throw new ArgumentException("duplicate mount name: " + m.Name);
            if (string.IsNullOrWhiteSpace(m.MountPoint)) throw new ArgumentException($"mount '{m.Name}' has no MountPoint");
            // One store holds one instance's pin, cache and overlay. Two instances sharing it
            // overwrite each other's pin -- which then asks a source for a revision it does not
            // have -- and each serves what the other cached, so this is refused rather than run.
            string store;
            try { store = Path.TrimEndingDirectorySeparator(Path.GetFullPath(m.GetStoreRoot())); }
            catch (Exception ex) { throw new ArgumentException($"mount '{m.Name}' has an unusable store root '{m.StoreRoot}': {ex.Message}"); }
            if (stores.TryGetValue(store, out var other))
                throw new ArgumentException($"mounts '{other}' and '{m.Name}' share the store '{store}'; one of them needs its own Id or StoreRoot");
            stores[store] = m.Name;
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
    /// <summary>
    /// This instance's identity: a Guid made when the instance is created, and the only thing
    /// its store folder is derived from. It never follows <see cref="Name"/>, so renaming a
    /// mount keeps its cache and its overlay, and two instances cannot end up on one store —
    /// which is what happened when the folder was derived from the name: a name that had been
    /// renamed away was free for the next instance to take, and took the first one's store with
    /// it, pin file, cache and all.
    /// </summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string MountPoint { get; set; } = "";
    /// <summary>Where this instance keeps its state. Empty means the default under the common
    /// application data folder, named by <see cref="Id"/>; set it to put a store on another
    /// volume, and two mounts may then only differ in it if the paths differ too.</summary>
    public string StoreRoot { get; set; } = "";
    public long CacheCapacityBytes { get; set; } = 4L * 1024 * 1024 * 1024;
    public SourceConfig Source { get; set; } = new();

    public List<LayerConfig>? Layers { get; set; } = new();

    // Fetches directory listings in the background after mount, so the first walk through the
    // tree is answered from the cache instead of the source. It yields while the mount is being
    // read, and stops at whichever budget runs out first.
    public bool WarmListings { get; set; }
    public int WarmListingsLimit { get; set; } = 2000;
    public int WarmListingsSeconds { get; set; } = 60;

    /// <summary>Generates this instance's identity if it has never had one, and reports whether
    /// it had to. It is written to the config before anything is read out of a store.</summary>
    public bool EnsureId()
    {
        if (!string.IsNullOrWhiteSpace(Id)) return false;
        Id = Guid.NewGuid().ToString("N");
        return true;
    }

    public string GetStoreRoot()
    {
        if (!string.IsNullOrWhiteSpace(StoreRoot)) return StoreRoot;
        EnsureId();
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PolyMount", "stores", Id);
    }
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
    public Dictionary<string, JsonElement> Parameters { get; set; } = new();
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
    public Dictionary<string, JsonElement> Parameters { get; set; } = new();
}
