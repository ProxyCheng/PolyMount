namespace PolyMount.Core.Ipc;

public sealed class ProviderInfo
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Type { get; set; } = "";
    public string Color { get; set; } = "";
    public bool Writable { get; set; }
    public string Generation { get; set; } = "";
    public LayerStorageUsage? StorageUsage { get; set; }
    public string? StorageError { get; set; }
    public long BlockCacheHits { get; set; }
    public long BlockCacheMisses { get; set; }
    public long BlockWriteBacks { get; set; }
    /// <summary>Transitions the predictive preloader has learned. It never serves reads, so
    /// its hit count stays zero and these are the numbers that show whether it is working.</summary>
    public long LearnedTransitions { get; set; }
    public long PreloadsStarted { get; set; }
    public long PreloadsCompleted { get; set; }
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
    public int Boundary { get; set; }
    public List<string> ExpectedLayers { get; set; } = new();

    // Serialized AppConfig payload, used by "saveConfig".
    public string? Config { get; set; }

    // Rolling-window size (seconds) for the "metrics" command; 0 => default.
    public int WindowSeconds { get; set; }
}

public sealed class ControlResponse
{
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public List<MountStatus> Mounts { get; set; } = new();
    public List<string> SourceTypes { get; set; } = new();
    public List<LayerDescriptor> LayerDescriptors { get; set; } = new();
    public List<LayerPreset> Presets { get; set; } = new();

    // Populated by the "probe" command.
    public ProbeResult? Probe { get; set; }

    // Populated by "getConfig": serialized AppConfig.
    public string? Config { get; set; }

    // Populated by "metrics": per-mount read analytics.
    public List<MountMetrics> Metrics { get; set; } = new();
}

// Per-layer presence of one path, for the layered visualization UI.
public sealed class LayerProbe
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Writable { get; set; }
    public bool Exists { get; set; }
    public bool Manageable { get; set; }
    public bool IsDir { get; set; }
    public long Size { get; set; }
    public bool Effective { get; set; }   // this layer's entry is the one that wins
    public bool Shadowed { get; set; }     // exists here but hidden by a layer above
    public string Generation { get; set; } = "";
    public string Color { get; set; } = "";   // layer-identity color "#RRGGBB"

    // exists / deleted / unknown
    public string State =>
        Exists ? "exists" : "unknown";
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
    public const string ClearLocalChanges = "clearLocalChanges";
    public const string Mount = "mount";
    public const string Unmount = "unmount";
    public const string Probe = "probe";
    public const string LayerAction = "layerAction";
    public const string ReleaseAbove = "releaseAbove";
    public const string GetConfig = "getConfig";
    public const string SaveConfig = "saveConfig";
    public const string Reload = "reload";
    public const string Metrics = "metrics";
    public const string Shutdown = "shutdown";
}

// Per-layer read analytics within a recent time window.
public sealed class LayerMetric
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Color { get; set; } = "";
    public long Hits { get; set; }          // reads served by this layer
    public long Writes { get; set; }
    public long WriteBytes { get; set; }
    public double AccessSharePercent { get; set; }
    public double SharePercent { get; set; } // share of total reads
    public double AvgCostMs { get; set; }    // average read-resolution latency
    public double MaxCostMs { get; set; }
    public long Bytes { get; set; }          // bytes served
}

public sealed class MountMetrics
{
    public string Mount { get; set; } = "";
    public int WindowSeconds { get; set; }
    public long TotalReads { get; set; }
    public long TotalWrites { get; set; }
    public double CacheHitPercent { get; set; }
    public double AvgCostMs { get; set; }
    public List<LayerMetric> Layers { get; set; } = new();
}

// Action names accepted by the "layerAction" command.
public static class LayerActions
{
    public const string DropLocal = "dropLocal";   // overlay: exists -> unknown (discard local copy, reveal below)
    public const string EvictCache = "evict";      // cache: exists -> unknown (evict this path)
}
