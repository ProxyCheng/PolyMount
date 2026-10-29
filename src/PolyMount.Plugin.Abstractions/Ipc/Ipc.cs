namespace PolyMount.Plugins.Ipc;

/// <summary>What the process answering a control request is doing, as far as it can say.
///
/// It is the responder's own state, and only two of its lifecycle are states a process can report
/// about itself: it is up, or it has accepted a shutdown and is on its way out. The two a window
/// also shows — starting and stopped — are the absence of an answer, because a process that has
/// not come up yet and one that has gone cannot say anything about themselves. A front-end that
/// wants all four reads this together with whether the pipe answered at all.
///
/// Written to the wire as its name: these payloads are also read by an agent over MCP, and
/// "Stopping" says what 1 does only to whoever holds this file.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ServiceState>))]
public enum ServiceState
{
    /// <summary>Up and taking commands. The default, so every answer that has nothing to say
    /// about a shutdown says what is true of the process that sent it.</summary>
    Running = 0,
    /// <summary>A shutdown was accepted and the instances are being taken down before the process
    /// exits. It keeps answering while that happens — a worker is given ten seconds to unmount —
    /// so an answer on its own is not proof that the service is still there for ordinary use.</summary>
    Stopping = 1,
}

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
    /// <summary>Preloads a spatial batch issued: the rest of a directory the caller had
    /// enumerated and started reading. Subsets of the preload and held counters above.</summary>
    public long SpatialPreloads { get; set; }
    public long SpatialCompleted { get; set; }
    public long SpatialHits { get; set; }
    public long SpatialWasted { get; set; }
    /// <summary>Preloads a co-read/burst model issued, and their share of hits and waste.
    /// Subsets of the preload and held counters above; the burst model is cross-thread.</summary>
    public long BurstPreloads { get; set; }
    public long BurstCompleted { get; set; }
    public long BurstHits { get; set; }
    public long BurstWasted { get; set; }
    /// <summary>Triggers whose follower set has repeated and may preload, and groups feedback
    /// closed because their guesses kept going unread. One trigger path is one group.</summary>
    public int BurstGroups { get; set; }
    public int BurstClosedGroups { get; set; }
    /// <summary>Caller streams with a history right now. One per process/thread the predictor
    /// has seen recently; a bounded runtime hint, not part of the persisted model.</summary>
    public int PredictionStreams { get; set; }
    /// <summary>Held guesses dropped because a lower layer accepted a write to their path.
    /// They were correct until the write changed them, so they are not prediction misses.</summary>
    public long Invalidated { get; set; }
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
    /// <summary>What the process serving this instance holds resident right now, by its own
    /// reading. The worker reports it because it is the process in question: a front end running
    /// as another account cannot open a LocalSystem worker's process handle, and the one number
    /// the layers' own holdings are reconciled against has to be there whichever account is
    /// reading. Zero when nothing is running the instance, or when it could not be read.</summary>
    public long WorkingSetBytes { get; set; }
    // Directories whose listing the background warm-up has stored, against its budget;
    // zero budget means the instance does not warm.
    public long WarmedDirectories { get; set; }
    public long WarmupLimit { get; set; }
    /// <summary>Warm-up progress in the reader's language, formatted with the two counts above.</summary>
    public LocalizedText? WarmupDetail { get; set; }
    public string? Error { get; set; }
    /// <summary>What kind of failure <see cref="Error"/> is, when the producer can say: "auth" when
    /// the source asked for credentials the machine did not have, "origin" when the credential the
    /// layer names is not allowed to open the address it talks to, "startup" for everything else
    /// that stops the instance from being built. A front-end chooses the remedy it offers from
    /// this rather than from the message.</summary>
    public string? ErrorKind { get; set; }
    /// <summary>The supervisor has stopped restarting this instance: the same failure ended it
    /// several times in a row, and nothing about it can change by waiting. The last error stays in
    /// <see cref="Error"/>. Starting the instance again — or reloading the configuration — is what
    /// clears this, so it says "look at the reason" rather than "this instance is broken".</summary>
    public bool Failed { get; set; }

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

    /// <summary>The programs that opened something on this mount in the last couple of minutes,
    /// newest first, whether or not anyone is watching the counters. It is who is using the mount,
    /// which the counters cannot say without a reader: they only count while a window is open, and
    /// a question asked about a program nobody is watching has no answer then. What a reader makes
    /// of the answer is the reader's business. The worker supplies the image path because it can
    /// open another account's process and a front end running as the reader cannot.</summary>
    public List<MountCaller> Callers { get; set; } = new();
}

/// <summary>One program that has used a mount lately: who it is, where its image is, and when it
/// was last seen. It is a hint for a reader — the image is what gets inspected — not a count of
/// anything.</summary>
public sealed class MountCaller
{
    public int Pid { get; set; }
    /// <summary>The process name, or empty when the worker could not read it.</summary>
    public string Name { get; set; } = "";
    /// <summary>The image's own path, or empty when it could not be read. Empty is not "nothing to
    /// worry about": a program whose image cannot be inspected is one nothing can be said about.</summary>
    public string ImagePath { get; set; } = "";
    /// <summary>When the process started, so a reader can tell one that predates an install from
    /// one that will pick it up on its next start. Empty when it could not be read.</summary>
    public string StartedUtc { get; set; } = "";
    /// <summary>How long ago it was last seen opening something on the mount.</summary>
    public double SecondsAgo { get; set; }
}

/// <summary>The values <see cref="MountStatus.ErrorKind"/> takes. A front-end picks its remedy from
/// one of these rather than from the message, which is written by whoever refused and in whatever
/// words that layer had.</summary>
public static class FailureKinds
{
    /// <summary>The source asked for an account, or refused the one it was given.</summary>
    public const string Auth = "auth";

    /// <summary>The credential the layer names is bound to other origins: its secret is intact, and
    /// the address has to be added to the record before it may be presented here.</summary>
    public const string Origin = "origin";

    /// <summary>Anything else that stopped the instance from being built.</summary>
    public const string Startup = "startup";
}

/// <summary>One named credential as the interface submits it. A null password on an update means
/// "keep the one already stored", so the interface never has to read a secret back.</summary>
public sealed class CredentialPayload
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public string Username { get; set; } = "";
    public string? Password { get; set; }
    /// <summary>The credential's kind: one of <see cref="CredentialTypes.All"/>, or a kind a
    /// plugin describes (<see cref="CredentialKinds"/>). Empty means a password credential, which
    /// is what a client that predates kinds sends.</summary>
    public string Kind { get; set; } = "";
    /// <summary>The PKCS#12 file in base64, for a certificate credential. Null keeps the one the
    /// record already holds.</summary>
    public string? Certificate { get; set; }
    /// <summary>The password that opens <see cref="Certificate"/>. Null keeps the stored one.</summary>
    public string? CertificatePassword { get; set; }
    /// <summary>The origins this credential may be presented at, replacing what the record holds.
    /// Null keeps them, which is what a client that does not offer them sends. A credential is
    /// bound to the origin it was first used for; a second server that shares the same account is
    /// named here, once, by whoever may change the configuration.</summary>
    public List<string>? Origins { get; set; }
    /// <summary>The fields of a plugin kind, keyed by path. Null is what a client that does not
    /// know the kind sends; an empty value on an update keeps the stored one, the same rule as a
    /// null <see cref="Password"/>.</summary>
    public Dictionary<string, string>? Values { get; set; }
}

/// <summary>A sign-in a plugin asks a front end to perform on its behalf.
///
/// The service cannot show a page, so a credential that comes from one — an internal single
/// sign-on, a cloud console — is obtained by the window that can, and handed back as the values
/// the plugin asked for. Which page and which cookies are the plugin's to say; the front end
/// reads the view's cookie store and writes each named cookie into the field named beside it, so
/// nothing on this side of the description understands the values.</summary>
public sealed class BrowserLogin
{
    /// <summary>The page to show. An absolute http(s) address.</summary>
    public string Url { get; set; } = "";

    /// <summary>The credential record the values are written into. It has to exist: a sign-in
    /// fills in a record a person already made, and does not invent one.</summary>
    public string CredentialId { get; set; } = "";

    /// <summary>Which cookie fills which field, in the order the plugin wants them read.</summary>
    public List<LoginValue> Values { get; set; } = new();
}

/// <summary>One value a sign-in leaves behind: a cookie of the page, and the field of the
/// credential record it fills.</summary>
public sealed class LoginValue
{
    public string Cookie { get; set; } = "";
    public string Field { get; set; } = "";
}

public sealed class ControlRequest
{
    public string Cmd { get; set; } = "";
    public string? Name { get; set; }
    public long Rev { get; set; }

    // Full local path to inspect, used by the "probe" and "layerAction" commands.
    public string? Path { get; set; }

    // Populated by "layerAction": which layer to mutate and what to do.
    public string? Layer { get; set; }

    // Populated by "saveCredential".
    public CredentialPayload? Credential { get; set; }

    // Populated by "deleteCredential".
    public string? CredentialId { get; set; }
    public string? Action { get; set; }
    public int Boundary { get; set; }
    public List<string> ExpectedLayers { get; set; } = new();

    // Serialized AppConfig payload, used by "saveConfig".
    public string? Config { get; set; }

    // Used by "setEnabled": whether the named instance should be part of what runs. Nullable
    // rather than defaulted, so a request that never said is refused instead of read as "off" —
    // which would stop an instance on the strength of a missing field.
    public bool? Enabled { get; set; }

    // Rolling-window size (seconds) for the "metrics" command; 0 => default. A fraction is
    // allowed: the performance window asks for the time since its previous sample, which is
    // shorter than a second as soon as it refreshes faster than once a second.
    public double WindowSeconds { get; set; }

    // Free-form label for a "recordingStart", kept in the session's name.
    public string? Label { get; set; }

    // Why a "recordingStop" is stopping: stopped / capacity / tray-exit …
    public string? Reason { get; set; }

    // Plugin management: the configured entry a command is about, the value to switch it to,
    // and the file an import should copy from.
    public string? AssemblyPath { get; set; }
    public bool? PluginEnabled { get; set; }
    public string? SourcePath { get; set; }

    // Populated by "checkSource": the source settings to ask with — an address and a credential,
    // as a window's draft holds them rather than as a saved instance does.
    public SourceConfig? Source { get; set; }
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
    /// <summary>This answer came from a development host rather than the installed service: a
    /// supervisor started by hand with its own pipe, store and plugins. A front end says so,
    /// because what it is showing is not what the machine is actually running.</summary>
    public bool Dev { get; set; }

    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    /// <summary>What the answering process is doing. Only the supervisor ever answers
    /// <see cref="ServiceState.Stopping"/>: a worker does not report that it is stopping, it stops
    /// answering.</summary>
    public ServiceState State { get; set; }
    public List<MountStatus> Mounts { get; set; } = new();
    public List<string> SourceTypes { get; set; } = new();
    public List<LayerDescriptor> LayerDescriptors { get; set; } = new();
    public List<LayerPreset> Presets { get; set; } = new();

    /// <summary>Something the configuration says is weaker than it should be, in the words to
    /// show a reader — today, a control plane with no account list. It is answered by every
    /// command so a front-end that polls status sees it without asking a second question.</summary>
    public string? SecurityWarning { get; set; }

    // Populated by the "instances" command: where each instance is served.
    public List<InstanceEndpoint> Instances { get; set; } = new();

    // Populated by "build": which process answered, and where it lives. It is asked once, by
    // whoever has to tell two halves of this product apart, so it rides on no other reply.
    public BuildInfo? Build { get; set; }

    // Populated by the "probe" command.
    public ProbeResult? Probe { get; set; }

    // Populated by "getConfig": serialized AppConfig.
    public string? Config { get; set; }

    // Populated by "checkSource": whether the source will serve with the settings it was asked
    // about. Not part of the status: it is the answer to one question, asked on purpose.
    public SourceCheckResult? Check { get; set; }

    // Populated by "refreshHead": the revision the named layer was moved to, so a caller that
    // wants to make it the pin does not have to read it back out of the status.
    public long Revision { get; set; }

    // Populated by "metrics": per-mount read analytics.
    public List<MountMetrics> Metrics { get; set; } = new();

    // Populated by "logs": the diagnostics files an export can bundle.
    public List<LogFile> Logs { get; set; } = new();

    // Populated by the "recording*" commands.
    public RecordingSessionInfo? Recording { get; set; }

    // Populated by "pluginNotices": what the plugins want said about this instance, each with the
    // command to act on it. A front end shows them and sends the action back; Core never reads
    // what a notice is about.
    public List<PluginNotice> Notices { get; set; } = new();

    // Populated by "pluginPanels": the management pages the loaded plugins offer, as the list a
    // sidebar is built from. What one of them shows is asked for separately ("pluginPanel"), and
    // only an instance can answer that — a panel may read its mount.
    public List<PluginPanelDescriptor> Panels { get; set; } = new();

    // Populated by "pluginPanel": the page one plugin offered, filled in for this instance.
    public PluginPanelView? Panel { get; set; }

    // Populated by a command a plugin registered: the payload that plugin wants its caller to
    // read, as JSON. Core does not look inside it; a front-end that offers the plugin's
    // operations hands it back unchanged.
    public string? Result { get; set; }

    /// <summary>A sign-in the answering command needs, when it needs one. The service has no
    /// window, so the page is shown by the front end and what it leaves behind is written into
    /// the credential named here — the front end never learns what the values mean, only which
    /// cookie fills which field.</summary>
    public BrowserLogin? Login { get; set; }

    // Populated by "credentials": the accounts this product keeps. Never a secret — the
    // interface does not see one, so it can neither leak nor echo it.
    public List<CredentialRecord> Credentials { get; set; } = new();

    // Populated by "credentials": the credential kinds loaded plugins describe, so the window
    // that edits an account can draw the kind it does not ship. The two built-in kinds are not
    // listed — a front end knows them without being told — and the delegate that builds a
    // credential stays in the process that loaded the plugin, which is why this is a copy.
    public List<CredentialKindDescriptor> CredentialKinds { get; set; } = new();

    // Populated by "credentials": the layer types that say which credentials they can present.
    // A record carries the answer for itself (CredentialRecord.UseFor); this list says which
    // layer types are worth asking at all, so a picker knows whether "this layer takes none of
    // them" means an empty list or a layer that never said.
    public List<string> CredentialConsumers { get; set; } = new();

    // Populated by "plugins": what the configuration names, what each one contributed when it
    // was loaded, and the DLLs in the host's plugin folder that are not named yet.
    public List<PluginView> Plugins { get; set; } = new();
    public List<PluginFileView> PluginFiles { get; set; } = new();
    public string? PluginDirectory { get; set; }
    /// <summary>The configuration the plugins list lives in. A front-end that edits plugins
    /// while the service is stopped needs it, and this is the only process that knows which file
    /// it was started with.</summary>
    public string? ConfigPath { get; set; }
}

/// <summary>One configured plugin as the plugin manager shows it: the entry, the file behind it,
/// whether the last load succeeded, and the extension points it registered.</summary>
public sealed class PluginView
{
    /// <summary>The configuration's own value, relative to the host directory.</summary>
    public string AssemblyPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>Whether the file is there. A configured entry whose file is gone is an error the
    /// host refuses to start with, so the manager shows it as such rather than hiding it.</summary>
    public bool Present { get; set; }
    /// <summary>Whether the last load registered it. False for a disabled plugin, and for one
    /// whose load failed.</summary>
    public bool Loaded { get; set; }
    public string Version { get; set; } = "";
    /// <summary>What the plugin calls itself, when it implements <c>IPluginMetadata</c>; null for
    /// a plugin that only has a file name.</summary>
    public LocalizedText? Title { get; set; }
    public LocalizedText? Description { get; set; }
    public string Icon { get; set; } = "";
    /// <summary>The plugin's identifier from its manifest, or empty for one built before manifests
    /// existed.</summary>
    public string Id { get; set; } = "";
    /// <summary>The contract version the plugin was compiled against, as it recorded it. Empty for
    /// a plugin that records none.</summary>
    public string ApiVersion { get; set; } = "";
    /// <summary>What the plugin declares it reaches for — network, filesystem, registry, native.
    /// A declaration for the reader, not a restriction: see the plugin contract's own note.</summary>
    public List<string> Permissions { get; set; } = new();
    /// <summary>Which of the product's processes run this plugin: <c>backend</c> (the servers,
    /// where a layer or a source lives) and <c>frontend</c> (a front-end, where a control
    /// operation runs). Both when the manifest names none.</summary>
    public List<string> Roles { get; set; } = new();
    /// <summary>The manifest exists and was usable. False is a legacy plugin, which still loads.</summary>
    public bool HasManifest { get; set; }
    public string? Error { get; set; }
    public List<string> SourceTypes { get; set; } = new();
    public List<string> LayerTypes { get; set; } = new();
    public List<string> Presets { get; set; } = new();
    public List<string> Strategies { get; set; } = new();
    public List<string> Commands { get; set; } = new();
    public List<string> Operations { get; set; } = new();
    /// <summary>The credential kinds this plugin describes, if any. A plugin that adds one is
    /// offering a way to authenticate, which belongs on its row beside the layers it added.</summary>
    public List<string> CredentialKinds { get; set; } = new();
    /// <summary>The management pages this plugin offers, by id: what the sidebar shows as a place
    /// of its own. Listed here so a row can say the plugin has one.</summary>
    public List<string> Panels { get; set; } = new();
}

/// <summary>A DLL in the host's plugin folder that the configuration does not name.</summary>
public sealed class PluginFileView
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public long Size { get; set; }
    public string ModifiedUtc { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>Whether the running host has this assembly loaded. A loaded file cannot be
    /// replaced until the service restarts.</summary>
    public bool InUse { get; set; }
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
    /// <summary>The winning layer's type icon, from its descriptor ("" when it declares none).
    /// The Explorer badge picks its glyph with this, so a file carries the same mark as the layer
    /// row in the window.</summary>
    public string EffectiveIcon { get; set; } = "";
    public List<LayerProbe> Layers { get; set; } = new();
}

/// <summary>Which build is answering, and where it lives.
///
/// The same facts the tray's About section shows about itself, asked of the service instead of
/// read from a window: a caller diagnosing a machine needs to know which half is the older one,
/// and the two are installed and updated separately. Nothing here is a secret — a path, a version
/// and an account name, none of them chosen by the caller.
///
/// <see cref="Version"/> is the release; <see cref="Revision"/> is the commit it was built from,
/// which is the only thing that tells two builds of the same number apart. A development build
/// carries the assembly default rather than a release number, which is why
/// <see cref="Informational"/> is sent whole as well.</summary>
public sealed class BuildInfo
{
    public string Version { get; set; } = "";
    public string Revision { get; set; } = "";
    /// <summary>The whole informational version, revision included.</summary>
    public string Informational { get; set; } = "";
    /// <summary>Started by hand as a development host: its pipe, store and plugins are its own, so
    /// what it reports is not what the machine is running.</summary>
    public bool Dev { get; set; }
    /// <summary>Put here by the setup, which leaves install.json beside the program folders.</summary>
    public bool Installed { get; set; }
    /// <summary>The folder the program lives in.</summary>
    public string Root { get; set; } = "";
    /// <summary>The configuration file this process reads and writes.</summary>
    public string ConfigPath { get; set; } = "";
    public string PluginsDirectory { get; set; } = "";
    /// <summary>The .NET runtime this process runs on, not the one it was built against.</summary>
    public string Runtime { get; set; } = "";
    /// <summary>The WinFsp build the mounts run against, or "unknown".</summary>
    public string WinFsp { get; set; } = "";
    public string System { get; set; } = "";
    public int ProcessId { get; set; }
    /// <summary>The account the service runs under — the identity its network requests use, and the
    /// reason a credential that works in a window can be refused by a mount.</summary>
    public string Account { get; set; } = "";
    public string StartedUtc { get; set; } = "";
}

public static class ControlCommands
{
    public const string Status = "status";
    /// <summary>Which build is answering, and where it lives. Answered by the supervisor, because
    /// it is a fact about the process rather than about a mount, and because the tray's own
    /// version is not the service's: the two halves are installed and updated separately.</summary>
    public const string Build = "build";
    /// <summary>Where every instance is served: the one command a front-end asks the supervisor
    /// for the addresses with, after which it talks to each instance directly.</summary>
    public const string Instances = "instances";
    public const string SetPin = "setPin";
    public const string RefreshHead = "refreshHead";
    public const string ClearCache = "clearCache";
    public const string ClearLocalChanges = "clearLocalChanges";
    public const string Mount = "mount";
    public const string Unmount = "unmount";
    /// <summary>Turns one instance on or off: the configuration field that decides whether the
    /// supervisor runs it at all, as opposed to "unmount", which stops the process it is running
    /// now without changing what the configuration says.
    ///
    /// It exists because the two are not the same question and a front-end should not have to
    /// answer the narrow one by handing over the whole configuration: a getConfig/saveConfig pair
    /// round trip would let a window that is holding a draft overwrite whatever else changed in
    /// between. Here the supervisor reads, changes and writes its own copy, so there is nothing
    /// to overwrite.</summary>
    public const string SetEnabled = "setEnabled";
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
    /// <summary>What the instance's plugins want a person told, and the commands to act on it.
    /// Asked of the instance, not of the process: notices are about a mount.</summary>
    public const string PluginNotices = "pluginNotices";
    /// <summary>The management pages the loaded plugins offer. Answered by the supervisor, because
    /// the list is a fact about what the process loaded rather than about one mount; the page
    /// itself is asked of an instance (<see cref="PluginPanel"/>).</summary>
    public const string PluginPanels = "pluginPanels";
    /// <summary>One plugin's management page, filled in. Answered by the instance's own process,
    /// which is the one a plugin's render can read a mount from. <c>Action</c> names the panel.</summary>
    public const string PluginPanel = "pluginPanel";
    public const string Shutdown = "shutdown";

    /// <summary>The one list of credentials this product keeps. Answered by the supervisor, which
    /// is the process that owns the file and the only one that can decrypt it.</summary>
    public const string Credentials = "credentials";
    /// <summary>Adds or changes a named credential. A missing password keeps the stored one.</summary>
    public const string SaveCredential = "saveCredential";
    public const string DeleteCredential = "deleteCredential";

    /// <summary>Asks a source whether it will serve with the settings in the request, without
    /// mounting anything: the editor's "does this account work?" button. Answered by the
    /// supervisor, which is the process that holds the plugins and can open the credential.
    ///
    /// The settings come from a window's draft, so there is no instance to route this to — it is
    /// about a configuration that does not exist yet. It changes nothing: a check that succeeds
    /// is not a mount, and a check that fails writes no error anywhere.</summary>
    public const string CheckSource = "checkSource";

    /// <summary>The plugin list: configured entries with what they contributed, and the DLLs
    /// beside the host that the configuration does not name.</summary>
    public const string Plugins = "plugins";
    /// <summary>Switches one configured plugin on or off. The value is explicit, like
    /// <see cref="SetEnabled"/> for instances: a missing field is a caller with a bug, not a
    /// reason to guess.</summary>
    public const string SetPluginEnabled = "setPluginEnabled";
    /// <summary>Removes a plugin from the configuration. The DLL stays where it is and appears
    /// among the files that can be added again.</summary>
    public const string RemovePlugin = "removePlugin";
    /// <summary>Copies a DLL into the host's plugin folder and names it in the configuration.</summary>
    public const string ImportPlugin = "importPlugin";
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
    /// (for example <c>demo/requests.log</c>).</summary>
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
    /// <summary>What one write this layer applied took, averaged over the writes it applied in the
    /// window, and the slowest of them. A write here is the layer's own operation — the apply that
    /// creates, commits, deletes or renames, which is where its time goes — rather than each call
    /// an application made into the staging handle; see <c>MetricsCollector.RecordWriteBytes</c>.
    /// Kept apart from the read figures above: the two directions are read against each other, and
    /// one average over both would be a number neither of them is.</summary>
    public double AvgWriteCostMs { get; set; }
    public double MaxWriteCostMs { get; set; }
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

/// <summary>One process's share of an instance's traffic within a recent window. The layers say
/// what answered; this says who asked, which is the question an instance being hammered by one
/// tool raises and the layer table cannot answer.</summary>
public sealed class ProcessMetric
{
    /// <summary>Which process it was, 0 for the folded bucket. The name is what a reader sees; the
    /// id is what names a process whose name could not be read at all.</summary>
    public int Pid { get; set; }
    /// <summary>Its name, or empty when it could not be read — a service cannot name another
    /// user's process. A process that has ended keeps the name it was last seen under, because the
    /// window it was counted in is a minute wide and the reader is owed the name of who did it.
    /// </summary>
    public string Name { get; set; } = "";
    /// <summary>A stable colour for the name, from the same palette the layers use, so one
    /// process keeps one colour from sample to sample and from run to run.</summary>
    public string Color { get; set; } = "";
    /// <summary>Requests this process made of the mount — the reads it opened and read, and the
    /// writes it opened and wrote — not the layers those requests were served by.</summary>
    public long Reads { get; set; }
    public long Writes { get; set; }
    /// <summary>What moved across its reads and its writes, for a reader that wants the volume
    /// rather than the call count. Free to collect where the transfer already knows its length.</summary>
    public long ReadBytes { get; set; }
    public long WriteBytes { get; set; }
    /// <summary>What the layers did on this process's behalf, busiest layer first. The counts above
    /// are the requests it made, which is a different total: a metadata call is a request no layer
    /// answered. This is the half that puts the two margins of the access section together — a
    /// process's layers add up to its own traffic, and every process's do to the layers'.</summary>
    public List<ProcessLayerCount> ByLayer { get; set; } = new();
    /// <summary>The bucket the processes past the named ones were folded into, so a pie of them can
    /// still add up to everything that happened.</summary>
    public bool Other { get; set; }
}

/// <summary>What one caller's traffic was served by, on one layer.</summary>
public sealed class ProcessLayerCount
{
    public string Name { get; set; } = "";
    public long Reads { get; set; }
    public long Writes { get; set; }
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
    /// <summary>The window this sample was measured over, in seconds. It can be a fraction: a
    /// reader that refreshes faster than once a second asks for a shorter one, and every count in
    /// here is an amount over that time rather than a rate.</summary>
    public double WindowSeconds { get; set; }
    /// <summary>How much of that window was really measured, in seconds: an instance that has only
    /// been up for three seconds has three seconds of data, and a reader turning these amounts into
    /// rates must divide by this rather than by what it asked for. Zero means nothing was observed
    /// in the window at all. Rounded to the collection's own resolution, never larger than the
    /// ring.</summary>
    public double MeasuredSeconds { get; set; }
    public long TotalReads { get; set; }
    public long TotalWrites { get; set; }
    /// <summary>Requests taken at the file system boundary in this window, and the time they took,
    /// whatever answered them. The layers can only be charged for the requests they served, so this
    /// is the total the per-layer cost is measured against: what is left over is the time spent in
    /// requests no layer answered for, and in the paths that record no cost per layer.</summary>
    public long RequestCount { get; set; }
    public double RequestMs { get; set; }
    public double CacheHitPercent { get; set; }
    public double AvgCostMs { get; set; }
    /// <summary>What one write cost, averaged over the writes the layers applied in the window:
    /// the same figure <see cref="AvgCostMs"/> is for reads, in the other direction.</summary>
    public double AvgWriteCostMs { get; set; }
    /// <summary>How many times a thread had to wait for a monitor since this worker started
    /// (<c>Monitor.LockContentionCount</c>). Cumulative, not an amount over the window: compare
    /// two samples, and a delta of zero means nothing queued on a lock in between — the reading
    /// that says the locks are not where this instance's time goes.</summary>
    public long LockContention { get; set; }
    public List<LayerMetric> Layers { get; set; } = new();
    /// <summary>Who asked, over the same window: one entry per process that touched the mount,
    /// the busiest first, with everything past the first few folded into one bucket. A process that
    /// has since exited keeps the name it was last seen under.</summary>
    public List<ProcessMetric> Processes { get; set; } = new();
    /// <summary>The layer traffic that had no caller, by layer: the read-ahead and the warm-up the
    /// service did for itself, which runs in tasks with their flow suppressed and so belongs to no
    /// process. It is kept apart from <see cref="Processes"/> rather than folded in as process 0 —
    /// a row saying "nobody" sitting among rows naming tools invites a comparison, and this is the
    /// one row that is not a tool. Without it the layer side of the access section would not add
    /// up: the layers served that work, and no process asked for it.</summary>
    public List<ProcessLayerCount> Unattributed { get; set; } = new();
}

// Action names accepted by the "layerAction" command.
public static class LayerActions
{
    public const string DropLocal = "dropLocal";   // overlay: exists -> unknown (discard local copy, reveal below)
    public const string EvictCache = "evict";      // cache: exists -> unknown (evict this path)
}
