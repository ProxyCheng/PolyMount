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
    /// <summary>Localised format for the block-cache summary.</summary>
    public LocalizedText? MemoryDetail { get; set; }
    /// <summary>Transitions the predictive preloader has learned. It never serves reads, so
    /// its hit count stays zero and these are the numbers that show whether it is working.</summary>
    public long LearnedTransitions { get; set; }
    public long PreloadsStarted { get; set; }
    public long PreloadsCompleted { get; set; }
    /// <summary>Reads served from the read-ahead layer's own copy.</summary>
    public long HeldHits { get; set; }
    /// <summary>Read-ahead entries evicted without ever being read.</summary>
    public long HeldWasted { get; set; }
    public long HeldBytes { get; set; }
    public long HeldCapacity { get; set; }
    /// <summary>Preloads that came from the stride walking a directory the reader had been
    /// listing in order, rather than from the learned successor table. Subsets of the two
    /// preload counts above and of <see cref="HeldHits"/>/<see cref="HeldWasted"/>, so the two
    /// ways of guessing can be read apart without the stride owning a confidence of its own.
    /// Zero also means the walk never had a held listing to take its position from — the layer
    /// chain, not the reader, decides whether a stride can run at all.</summary>
    public long SiblingPreloads { get; set; }
    public long SiblingCompleted { get; set; }
    public long SiblingHits { get; set; }
    public long SiblingWasted { get; set; }
    /// <summary>Directories whose walk the stride is following right now.</summary>
    public int SiblingDirectories { get; set; }
    /// <summary>How a source layer that serves several priorities shared itself out. What the
    /// reader's class keeps is what read-ahead may not take, so read-ahead being busy is never
    /// what its wait is made of; read-ahead waits in its own class's queue instead, and that
    /// rising while the reader's stays flat is what "the reader is never behind read-ahead" reads
    /// as. Request counts are the denominators, so each wait is a mean as well as a total.</summary>
    public List<ReadLaneStat> ReadLanes { get; set; } = new();
    /// <summary>Format string for the read-ahead summary, localised by the core.</summary>
    public LocalizedText? PredictionDetail { get; set; }
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
    // Directories whose listing the background warm-up has stored, against its budget;
    // zero budget means the instance does not warm.
    public long WarmedDirectories { get; set; }
    public long WarmupLimit { get; set; }
    /// <summary>Warm-up progress in the reader's language, formatted with the two counts above.</summary>
    public LocalizedText? WarmupDetail { get; set; }
    public string? Error { get; set; }

    // What the supervisor knows about the process running this instance: the pipe it answers on
    // (empty when nothing is running it), which process it is (0 when none is), and how many times
    // it has had to be started again. The instance's own numbers are the worker's to report; these
    // are the supervisor's, and they are what a front-end needs to ask the worker directly.
    public string Pipe { get; set; } = "";
    public int WorkerPid { get; set; }
    public int Restarts { get; set; }

    // Whether a recording channel is attached to this instance right now, so a front-end can
    // show it without asking a second question.
    public bool Recording { get; set; }
    public string? RecordingStartedUtc { get; set; }
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

    // Free-form label for a "recordingStart", kept in the session's name.
    public string? Label { get; set; }

    // Why a "recordingStop" is stopping: stopped / capacity / tray-exit …
    public string? Reason { get; set; }
}

/// <summary>Where one instance is served: the process that owns it, and the pipe it answers on.
///
/// It exists so a front-end can ask once and then talk to the instance itself — nothing is
/// forwarded through the supervisor that answered the question. The pipe is named for
/// <see cref="Id"/>, which is the same identity the instance's store folder is named by, so it
/// survives a restart: a front-end that kept the address is still right afterwards.
///
/// An instance with no process of its own — one that is disabled — has no endpoint; it is in the
/// configuration and in the supervisor's status, and nowhere else.</summary>
public sealed class InstanceEndpoint
{
    /// <summary>The instance's identity: <c>MountProfile.Id</c>, the Guid its store is named by.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>What the worker mounts, so a front-end holding a local path can tell which
    /// instance it belongs to without asking anyone.</summary>
    public string MountPoint { get; set; } = "";
    /// <summary>The pipe the instance answers on. A front-end connects here rather than to the
    /// supervisor for anything that concerns this instance.</summary>
    public string Pipe { get; set; } = "";
    public int WorkerPid { get; set; }
    /// <summary>When that process started, so a front-end can tell a restart from the same one.</summary>
    public string StartedUtc { get; set; } = "";
}

public sealed class ControlResponse
{
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public List<MountStatus> Mounts { get; set; } = new();
    public List<string> SourceTypes { get; set; } = new();
    public List<LayerDescriptor> LayerDescriptors { get; set; } = new();
    public List<LayerPreset> Presets { get; set; } = new();

    // Populated by the "instances" command: where each instance is served.
    public List<InstanceEndpoint> Instances { get; set; } = new();

    // Populated by the "probe" command.
    public ProbeResult? Probe { get; set; }

    // Populated by "getConfig": serialized AppConfig.
    public string? Config { get; set; }

    // Populated by "metrics": per-mount read analytics.
    public List<MountMetrics> Metrics { get; set; } = new();

    // Populated by "logs": the diagnostics files an export can bundle.
    public List<LogFile> Logs { get; set; } = new();

    // Populated by the "recording*" commands.
    public RecordingSessionInfo? Recording { get; set; }
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
    /// <summary>Name for the read-ahead waste series, localised by the core.</summary>
    public LocalizedText? PredictiveWasted { get; set; }
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
    /// <summary>Where every instance is served: the one command a front-end asks the supervisor
    /// for the addresses with, after which it talks to each instance directly.</summary>
    public const string Instances = "instances";
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
    public const string Logs = "logs";
    public const string RecordingStart = "recordingStart";
    public const string RecordingStatus = "recordingStatus";
    public const string RecordingStop = "recordingStop";
    public const string Shutdown = "shutdown";
}

/// <summary>One recording on the service: whether it is running, and what it has counted.
///
/// There is nothing to tail. A recording counts rather than logs, so what comes back here is the
/// whole of it — exact counts of every request the instance served, whatever the sampled log chose
/// to write down about them.</summary>
public sealed class RecordingSessionInfo
{
    public bool Active { get; set; }
    public string Mount { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Identifies a session across a reload, so a front end can tell the one it started
    /// from one it found running.</summary>
    public string SessionId { get; set; } = "";
    public string StartedUtc { get; set; } = "";
    public string? StoppedUtc { get; set; }
    /// <summary>Why it is not running: saved / stopped / disposed / tray-exit …</summary>
    public string? StoppedReason { get; set; }
    /// <summary>What was counted. Null only before the first session of a process.</summary>
    public RecordingCounts? Counts { get; set; }
    /// <summary>Identifies the service process, so a restart can be told from a reload.</summary>
    public string ServiceInstanceId { get; set; } = "";
    public string? Error { get; set; }
}

/// <summary>One diagnostics file an export can bundle. The service reports where the file
/// lives; the front-end copies it, so a growing log is read once and never quoted.</summary>
public sealed class LogFile
{
    /// <summary>Where the file belongs inside an export, relative and slash-separated
    /// (for example <c>L10/requests.log</c>).</summary>
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public long Bytes { get; set; }
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
    /// <summary>Outcome split for a read-ahead layer, whose entries are either read, wasted
    /// on eviction, or still held — the three add up to everything it kept.</summary>
    public bool Predictive { get; set; }
    public long HeldUsed { get; set; }
    public long HeldWasted { get; set; }
    public long HeldCount { get; set; }
    public long HeldWastedBytes { get; set; }
    /// <summary>A layer that keeps blocks of the reads other layers served. Its own read count
    /// says nothing about that work: the bytes below are the whole of it, and they are
    /// lifetime totals for the reader to difference over its own window.</summary>
    public bool Accelerating { get; set; }
    public long AcceleratedBytes { get; set; }
    public long FetchedBytes { get; set; }
}

/// <summary>One process's share of a window: who is reading and writing the mount, and how much
/// of it. The process is identified by the id WinFsp reports for the request, which the front end
/// turns into a name — the service never asks the operating system about another process.</summary>
public sealed class ProcessMetric
{
    public int Pid { get; set; }
    public long Reads { get; set; }
    public long Writes { get; set; }
    public long Bytes { get; set; }
}

/// <summary>One priority class's share of a source that serves several. Low is urgent: the
/// reader is class 0 and holds nearly the whole source, read-ahead is a class with a floor of
/// one and a small ceiling.</summary>
public sealed class ReadLaneStat
{
    public int Priority { get; set; }
    /// <summary>Requests kept for this class whatever else is happening.</summary>
    public int Floor { get; set; }
    /// <summary>The most this class may ever hold, spare capacity included.</summary>
    public int Ceiling { get; set; }
    public long Requests { get; set; }
    public int InFlight { get; set; }
    /// <summary>Waiting for room right now. Read-ahead that has fallen behind the reader shows
    /// up here rather than as a bigger share.</summary>
    public int Queued { get; set; }
    /// <summary>Total time this class's requests spent being admitted. Read-ahead's is its
    /// queue; the reader's is room the source did not have, and it can only be that — what is
    /// kept for the reader is subtracted from what read-ahead may take, so read-ahead being busy
    /// is never the reason this number moves.</summary>
    public double WaitMs { get; set; }
    /// <summary>Read-ahead at this class that a reader arrived during and joined instead of
    /// fetching a second time — the guesses the reader then confirmed by asking.</summary>
    public long Promotions { get; set; }
}

public sealed class MountMetrics
{
    public string Mount { get; set; } = "";
    /// <summary>Name for the read-ahead waste series, localised by the core.</summary>
    public LocalizedText? PredictiveWasted { get; set; }
    public int WindowSeconds { get; set; }
    public long TotalReads { get; set; }
    public long TotalWrites { get; set; }
    public double CacheHitPercent { get; set; }
    public double AvgCostMs { get; set; }
    public List<LayerMetric> Layers { get; set; } = new();
    /// <summary>Who has been using this mount in the window, ranked by requests. Empty when the
    /// window holds nothing, or when the callers are not WinFsp requests (id 0).</summary>
    public List<ProcessMetric> Processes { get; set; } = new();
}

// Action names accepted by the "layerAction" command.
public static class LayerActions
{
    public const string DropLocal = "dropLocal";   // overlay: exists -> unknown (discard local copy, reveal below)
    public const string EvictCache = "evict";      // cache: exists -> unknown (evict this path)
}
