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

    // Full local path to inspect, used by the "probe" command.
    public string? Path { get; set; }
}

public sealed class ControlResponse
{
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public List<MountStatus> Mounts { get; set; } = new();
    public List<string> SourceTypes { get; set; } = new();

    // Populated by the "probe" command.
    public ProbeResult? Probe { get; set; }
}

// Per-layer presence of one path, for the layered visualization UI.
public sealed class LayerProbe
{
    public string Name { get; set; } = "";
    public bool Exists { get; set; }
    public bool IsDir { get; set; }
    public long Size { get; set; }
    public bool Effective { get; set; }   // this layer's entry is the one that wins
    public bool Shadowed { get; set; }     // exists here but hidden by a layer above
    public bool Tombstoned { get; set; }   // this layer actively hides the path (tombstone)
    public string Generation { get; set; } = "";
}

public sealed class ProbeResult
{
    public string Mount { get; set; } = "";
    public string MountPoint { get; set; } = "";
    public string Rel { get; set; } = "";
    public bool Found { get; set; }        // an effective (visible) entry exists
    public bool IsDir { get; set; }
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
}
