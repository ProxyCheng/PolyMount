namespace PolyMount.Core.Ipc;

public sealed class ProviderInfo
{
    public string Name { get; set; } = "";
    public bool Writable { get; set; }
    public string Generation { get; set; } = "";
}

public sealed class MountStatus
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Mounted { get; set; }
    public string MountPoint { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string Url { get; set; } = "";
    public long Revision { get; set; }
    public List<ProviderInfo> Providers { get; set; } = new();
    public long CacheBytes { get; set; }
    public long CacheCapacity { get; set; }
    public int CacheCount { get; set; }
    public string? Error { get; set; }
}

public sealed class ControlRequest
{
    public string Cmd { get; set; } = "";
    public string? Name { get; set; }
    public long Rev { get; set; }

    // Full local path to inspect, used by the "probe" and "layerAction" commands.
    public string? Path { get; set; }

    // Used by "layerAction": which layer to mutate and what to do.
    public string? Layer { get; set; }
    public string? Action { get; set; }

    // Serialized AppConfig payload, used by "saveConfig".
    public string? Config { get; set; }
}

public sealed class ControlResponse
{
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public List<MountStatus> Mounts { get; set; } = new();
    public List<string> SourceTypes { get; set; } = new();

    // Populated by the "probe" command.
    public ProbeResult? Probe { get; set; }

    // Populated by "getConfig": serialized AppConfig.
    public string? Config { get; set; }
}

// Per-layer presence of one path, for the layered visualization UI.
public sealed class LayerProbe
{
    public string Name { get; set; } = "";
    public bool Writable { get; set; }
    public bool Exists { get; set; }
    public bool IsDir { get; set; }
    public long Size { get; set; }
    public bool Effective { get; set; }   // this layer's entry is the one that wins
    public bool Shadowed { get; set; }     // exists here but hidden by a layer above
    public bool Tombstoned { get; set; }   // this layer hides the path (effective tombstone)
    public bool DirectTombstone { get; set; } // tombstone set on this exact path (removable)
    public string Generation { get; set; } = "";
    public string Color { get; set; } = "";   // layer-identity color "#RRGGBB"

    // 存在 / 不存在 / 不知道
    public string State =>
        Exists ? "exists" : Tombstoned ? "deleted" : "unknown";
}

public sealed class ProbeResult
{
    public string Mount { get; set; } = "";
    public string MountPoint { get; set; } = "";
    public string Rel { get; set; } = "";
    public bool Found { get; set; }        // an effective (visible) entry exists
    public bool IsDir { get; set; }
    public string EffectiveLayer { get; set; } = "";  // name of the winning layer, "" if none
    public string EffectiveColor { get; set; } = "";  // color of the winning layer (for the badge)
    public List<LayerProbe> Layers { get; set; } = new();
}

public static class ControlCommands
{
    public const string Status = "status";
    public const string SetPin = "setPin";
    public const string RefreshHead = "refreshHead";
    public const string ClearCache = "clearCache";
    public const string Mount = "mount";
    public const string Unmount = "unmount";
    public const string Probe = "probe";
    public const string LayerAction = "layerAction";
    public const string GetConfig = "getConfig";
    public const string SaveConfig = "saveConfig";
    public const string Reload = "reload";
}

// Action names accepted by the "layerAction" command.
public static class LayerActions
{
    public const string Restore = "restore";       // overlay: 不存在 -> 不知道 (remove tombstone)
    public const string Hide = "hide";             // overlay: -> 不存在 (delete copy + tombstone)
    public const string DropLocal = "dropLocal";   // overlay: 存在 -> 不知道 (discard local copy, reveal below)
    public const string EvictCache = "evict";      // cache: 存在 -> 不知道 (evict this path)
}
