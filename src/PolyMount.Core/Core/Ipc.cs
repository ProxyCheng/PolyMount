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
}

public sealed class ControlResponse
{
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public List<MountStatus> Mounts { get; set; } = new();
    public List<string> SourceTypes { get; set; } = new();
}

public static class ControlCommands
{
    public const string Status = "status";
    public const string SetPin = "setPin";
    public const string RefreshHead = "refreshHead";
    public const string ClearCache = "clearCache";
    public const string Mount = "mount";
    public const string Unmount = "unmount";
}
