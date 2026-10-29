using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PolyMount.Core;
using PolyMount.Core.Ipc;
using PolyMount.Layers;

namespace PolyMount;

public sealed class AppConfig
{
    public string WinFspBin { get; set; } = @"C:\Program Files (x86)\WinFsp\bin";
    public string ControlPipe { get; set; } = "PolyMount.Control";
    public List<MountProfile> Mounts { get; set; } = new();
    public List<PluginConfig> Plugins { get; set; } = new();

    /// <summary>The accounts that may drive the control plane: the tray, an agent's MCP server,
    /// the Explorer overlay. Identity is the SID; the name is what a reader sees, refreshed by
    /// whoever seeded the list.
    ///
    /// Empty is not "nobody" — it is "not configured", which keeps the interactive grant the
    /// product had before this list existed, and is reported as a warning. The installer seeds it
    /// from the account that ran it; a service started in session 0 cannot know who that is, so a
    /// fresh configuration created there stays open until the next install.</summary>
    public List<ControlUser> ControlUsers { get; set; } = new();

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
        fresh.SeedControlUsers();
        Save(path, fresh);
        return fresh;
    }

    /// <summary>Writes the configuration through a temporary file in the same directory.
    ///
    /// The file is read by the hot-reload watcher and by the tray the moment it changes, and both
    /// used to be able to see a half-written document — <c>File.WriteAllText</c> truncates first
    /// and fills in after, which is a window a reader can land in. Writing beside the file and
    /// renaming over it means a reader sees the old document or the new one, and a power loss
    /// leaves one of them rather than a prefix of one.</summary>
    public static void Save(string path, AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        config.EnsureMountIds();
        string text = JsonSerializer.Serialize(config, Options);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(text);
                writer.Flush();
                // The rename is only worth anything if the bytes behind it reached the disk.
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception)
        {
            try { File.Delete(temporary); } catch (Exception) { }
            throw;
        }
    }

    /// <summary>Names the account this process runs as, when it has one a person signed in to.
    ///
    /// A configuration created from a tray (or a service started by hand) is created for that
    /// account, and this is what keeps the tray able to talk to it afterwards. A service started
    /// by the service control manager runs in session 0 under LocalService or LocalSystem, which
    /// nobody connects from: seeding that would leave the real user locked out, so it does not.</summary>
    public void SeedControlUsers()
    {
        if (ControlUsers.Count > 0) return;
        try
        {
            if (Process.GetCurrentProcess().SessionId == 0) return;
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is not { } user) return;
            ControlUsers.Add(new ControlUser { Sid = user.Value, Name = identity.Name });
        }
        catch (Exception)
        {
            // A process that cannot name its own account leaves the list empty, which is the
            // open policy it would otherwise have had silently. The warning says so.
        }
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

    /// <summary>Basic validation; throws <see cref="ArgumentException"/> on problems.
    ///
    /// It is <see cref="ValidateStructure"/> plus the layer types, because those are the only
    /// part that cannot be answered until the plugin set this configuration names has been
    /// loaded: a layer type is a name a plugin registers. The split is what lets a document be
    /// refused before any of the code it names runs.</summary>
    public void Validate()
    {
        ValidateStructure();
        foreach (var m in Mounts) m.ValidateLayers();
    }

    /// <summary>Everything about a configuration that can be decided without loading a plugin:
    /// names, identities, stores, colors, the accounts allowed to drive the service, and the
    /// shape of the plugin entries themselves.</summary>
    public void ValidateStructure()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stores = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var identity = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in Mounts)
        {
            if (string.IsNullOrWhiteSpace(m.Name)) throw new ArgumentException("a mount has an empty Name");
            if (!names.Add(m.Name)) throw new ArgumentException("duplicate mount name: " + m.Name);
            if (string.IsNullOrWhiteSpace(m.MountPoint)) throw new ArgumentException($"mount '{m.Name}' has no MountPoint");
            // The identity is a Guid this product makes, and it is concatenated into a store
            // folder, a log file name and a pipe name. A value that can carry a separator or a
            // rooted path is therefore a path out of all three, so anything that is not a plain
            // token is refused. An empty one is allowed: Save makes it before it writes, and the
            // editor hands over a new instance exactly that way.
            if (!string.IsNullOrEmpty(m.Id))
            {
                if (m.Id.Length > 64 || m.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
                    throw new ArgumentException($"mount '{m.Name}' has an invalid Id '{m.Id}': an id is made of letters, digits, '-' and '_'");
                if (!identity.Add(m.Id)) throw new ArgumentException("duplicate mount id: " + m.Id);
            }
            // One store holds one instance's pin, cache and overlay. Two instances sharing it
            // overwrite each other's pin -- which then asks a source for a revision it does not
            // have -- and each serves what the other cached, so this is refused rather than run.
            string store;
            try { store = Path.TrimEndingDirectorySeparator(Path.GetFullPath(m.GetStoreRoot())); }
            catch (Exception ex) { throw new ArgumentException($"mount '{m.Name}' has an unusable store root '{m.StoreRoot}': {ex.Message}"); }
            if (stores.TryGetValue(store, out var other))
                throw new ArgumentException($"mounts '{other}' and '{m.Name}' share the store '{store}'; one of them needs its own Id or StoreRoot");
            stores[store] = m.Name;
        }
        foreach (var kv in LayerColors)
            if (!PolyMount.Core.LayerColors.TryNormalizeHex(kv.Value, out _))
                throw new ArgumentException($"invalid color '{kv.Value}' for layer '{kv.Key}'");
        // An account that cannot be resolved to a SID is a rule that would never match, so it is
        // refused where it is written instead of becoming a pipe nobody can connect to.
        foreach (var account in ControlUsers)
            if (!ControlAccess.IsWellFormed(account.Sid))
                throw new ArgumentException($"ControlUsers holds '{account.Sid}', which is not a security identifier (S-1-5-...)");
        // One entry per assembly. Two entries naming the same file load the same code twice and
        // register everything twice, which is a configuration nobody meant to write.
        var plugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in Plugins)
        {
            if (string.IsNullOrWhiteSpace(plugin.AssemblyPath))
                throw new ArgumentException("a plugin has an empty AssemblyPath");
            if (!plugins.Add(plugin.AssemblyPath.Replace('\\', '/')))
                throw new ArgumentException("duplicate plugin: " + plugin.AssemblyPath);
        }
    }

    public static AppConfig Default() => new();
}

public sealed class PluginConfig
{
    public string AssemblyPath { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>One account the control plane accepts. The SID is the identity — it cannot be
/// re-pointed by renaming an account or by a machine that resolves names differently — and the
/// name is there so a person can read the file and the log.</summary>
public sealed class ControlUser
{
    public string Sid { get; set; } = "";
    public string Name { get; set; } = "";
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
