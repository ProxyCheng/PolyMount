using System.Text.Json;
using System.Text.Json.Serialization;
using PolyMount.Core;
using PolyMount.Layers;

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

    /// <summary>Reads a configuration without creating or repairing one. A front-end that only
    /// needs to know which plugins are enabled — the MCP server, for one — must not write to the
    /// service's file, and a path that does not exist is a configuration nobody wrote yet, not
    /// one this process gets to invent.</summary>
    public static AppConfig? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? Default() : null;

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

    /// <summary>Turns one instance on or off, by name, and reports whether that changed anything.
    ///
    /// The lookup is by name because that is what the protocol uses everywhere; it is not case
    /// sensitive, for the same reason <see cref="Validate"/> refuses two names that differ only in
    /// case. A name that matches nothing is an error rather than a no-op: a front-end that switched
    /// an instance and then named one that is not configured has a stale view, and answering "done"
    /// would leave it believing a change it never made.
    ///
    /// Whether the value changed is worth having because the caller writes the whole file and
    /// reloads it either way: a request that asked for what is already so should not restart a
    /// worker to arrive where it started.</summary>
    public bool SetEnabled(string name, bool enabled)
    {
        var profile = Mounts.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("no such instance: " + name);
        if (profile.Enabled == enabled) return false;
        profile.Enabled = enabled;
        Validate();
        return true;
    }

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
    //
    // Kept for configurations written before warm-up became a layer: an enabled flag is
    // translated by GetLayers into the same layer such a configuration would have had if it
    // had been written today. An explicit warm-up layer, once present, suppresses this.
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

    /// <summary>The configured chain, plus the layer an older configuration's warm-up flag
    /// stands for. The synthesized layer goes on top — all caches, and the whole chain below
    /// it, which is what the flag meant when the walk went through the mount's own resolver.
    /// It is not written back here; the editor stores it the next time this profile is saved,
    /// and the flag then has nothing left to synthesize.</summary>
    public List<LayerConfig> GetLayers()
    {
        var layers = Layers ?? new();
        if (!WarmListings || WarmListingsLimit <= 0) return layers;
        if (layers.Any(l => string.Equals(l.Type, ListingWarmupLayer.Type, StringComparison.OrdinalIgnoreCase)))
            return layers;
        string id = ListingWarmupLayer.Type;
        for (int suffix = 1; layers.Any(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase)); suffix++)
            id = ListingWarmupLayer.Type + "-" + suffix;
        var effective = new List<LayerConfig>(layers.Count + 1)
        {
            new()
            {
                Id = id,
                Type = ListingWarmupLayer.Type,
                Parameters = new()
                {
                    ["Limit"] = JsonSerializer.SerializeToElement(WarmListingsLimit),
                    ["Seconds"] = JsonSerializer.SerializeToElement(WarmListingsSeconds)
                }
            }
        };
        effective.AddRange(layers);
        return effective;
    }

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

    /// <summary>The named credential this source uses, or empty for none. A name that no longer
    /// exists is an error rather than a reason to try another account: which account a layer uses
    /// should never be a guess.</summary>
    public string CredentialId { get; set; } = "";
    public Dictionary<string, JsonElement> Parameters { get; set; } = new();
}
