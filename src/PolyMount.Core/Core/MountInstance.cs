using Fsp;
using PolyMount.Core.Ipc;
using PolyMount.Layers;

namespace PolyMount.Core;

public sealed class MountInstance : IDisposable
{
    /// <summary>Rolled by Serilog, so the reader matches the family rather than one name.</summary>
    private const string DiagnosticsLogName = "requests.log";
    private readonly string _logPath;
    private readonly string _statePath;
    private readonly object _gate = new();
    private FileSystemHost? _host;
    private Action<IReadOnlyList<FileChange>>? _changes;
    private bool _unmounting;
    /// <summary>The directory this instance attached a registered volume to, or null when the
    /// mount did not go through the Windows Mount Manager. Unmounting has to remove it: a
    /// directory mount point outlives the volume it points at otherwise.</summary>
    private string? _registeredDirectory;
    private readonly List<IMountAwareLayer> _background = new();
    private string _lastWinFspBin = "";
    private readonly Dictionary<string, ILayer> _layers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LayerConfig> _configs = new(StringComparer.OrdinalIgnoreCase);

    public MountInstance(MountProfile profile)
    {
        Profile = profile;
        profile.ValidateLayers();
        var state = Path.Combine(profile.GetStoreRoot(), "state");
        Directory.CreateDirectory(state);
        _statePath = state;
        _logPath = Path.Combine(state, "polymount.log");

        var configs = profile.GetLayers().Where(l => l.Enabled).ToList();
        var built = new ILayer[configs.Count];
        // Build bottom-up so every cache can observe the generation of its complete lower chain.
        for (int i = configs.Count - 1; i >= 0; i--)
        {
            var cfg = configs[i];
            _configs.Add(cfg.Id, cfg);
            bool legacyId = cfg.Id == cfg.Type;
            string layerState = legacyId ? state : Path.Combine(state, cfg.Id);
            Directory.CreateDirectory(layerState);
            string storage = profile.GetLayerStorage(cfg);
            int index = i;
            var provider = LayerRegistry.Create(cfg, new LayerBuildContext(storage, layerState, () =>
                {
                    var lower = built.Skip(index + 1).Select(l => l.Name + ":" + l.Generation);
                    // Preserve the old revision-based folder for the single-source chain.
                    if (built.Length - index == 2) return built[index + 1].Generation;
                    return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|", lower))));
                }));
            _layers.Add(cfg.Id, provider);
            built[i] = provider is ICacheLayer cache ? new NamedCacheLayer(cfg.Id, cache) : new NamedLayer(cfg.Id, provider);
        }
        Overlay = _layers.Values.OfType<OverlayLayer>().FirstOrDefault();
        DiskCache = _layers.Values.OfType<LruCacheLayer>().FirstOrDefault();
        PrimarySource = configs.Where(c => SourceRegistry.Contains(c.Type)).Select(c => _layers[c.Id]).FirstOrDefault();
        var layers = built.ToList();
        Vfs = new VirtualFileSystem(layers);
        Vfs.Metrics = Metrics;
        Vfs.Diagnostics = new RequestDiagnostics(Path.Combine(state, DiagnosticsLogName))
        {
            // Sessions live beside the sampled log but in a family of their own, so the export's
            // request family and its rolled segments stay exactly what they were.
            Channel = new RecordingChannel(Path.Combine(state, "recording"))
        };
        foreach (var cache in _layers.Values.OfType<LruCacheLayer>()) cache.Diagnostics = Vfs.Diagnostics;
        // Publish this instance to whatever is listening from outside (dotnet-counters). Nothing is
        // published until something asks, so this costs a mount with nobody watching nothing.
        PolyMountEventSource.Log.Register(this);
        // Layers that need the chain beneath them, or the mount's lifecycle, are wired here.
        // The mount only knows these capabilities; which layer type supplies one is the
        // chain's business, so adding a background layer does not change this file.
        for (int i = 0; i < configs.Count; i++)
        {
            var provider = _layers[configs[i].Id];
            VirtualFileSystem? lower = null;
            VirtualFileSystem Lower() => lower ??= new VirtualFileSystem(layers.Skip(i + 1)) { Diagnostics = Vfs.Diagnostics };
            if (provider is IMountAwareLayer background)
            {
                // The quiet reading is the instance's: a layer only sees the requests that
                // reach it, and background work must yield to the mount's readers.
                background.Attach(Lower(), () => Metrics.IdleFor);
                _background.Add(background);
            }
            else if (provider is ILowerChainLayer chained)
                chained.Attach(Lower());
        }
    }

    public MountProfile Profile { get; }
    public VirtualFileSystem Vfs { get; }
    public OverlayLayer? Overlay { get; }
    public LruCacheLayer? DiskCache { get; }
    public ILayer? PrimarySource { get; }

    [Obsolete("Use DiskCache.")]
    public LruCacheLayer? Cache => DiskCache;

    [Obsolete("Use PrimarySource.")]
    public ILayer? Source => PrimarySource;
    public MetricsCollector Metrics { get; } = new();

    // User color overrides (layer name -> "#RRGGBB"); auto color is used for missing keys.
    public IReadOnlyDictionary<string, string> ColorOverrides { get; set; } =
        new Dictionary<string, string>();

    public string Name => Profile.Name;
    public string MountPoint => Profile.MountPoint;

    /// <summary>Request diagnostics this instance has written, oldest segment first. Serilog
    /// rolls them, so a busy instance holds more than one file.</summary>
    public IReadOnlyList<string> GetLogFiles()
    {
        try { return Directory.GetFiles(_statePath, Path.GetFileNameWithoutExtension(DiagnosticsLogName) + "*.log").Order(StringComparer.Ordinal).ToList(); }
        catch (DirectoryNotFoundException) { return Array.Empty<string>(); }
    }

    /// <summary>Attaches the recording channel: from here on every event the tracer emits is
    /// written to the session as well, unsampled.</summary>
    public RecordingSessionInfo StartRecording(string? label)
    {
        // A recording counts from the moment it is asked for, not from its first poll: the call
        // that starts it is somebody watching, and the second in between is the one they started
        // recording to catch.
        Metrics.Watch();
        return Channel.Start(Profile.Name, label is { Length: > 0 } ? label : Profile.Name);
    }

    public RecordingSessionInfo StopRecording(string reason) => Channel.Stop(reason);

    public RecordingSessionInfo RecordingInfo() => Channel.Info();

    private RecordingChannel Channel => Vfs.Diagnostics?.Channel
        ?? throw new NotSupportedException("this instance has no recording channel");

    public bool Mounted
    {
        get { lock (_gate) return _host != null; }
    }

    public void Mount(string winFspBin)
    {
        _lastWinFspBin = winFspBin;
        lock (_gate)
        {
            if (_host != null) return;
            if (_unmounting) throw new IOException("Mount is still unmounting");
            WinFspBootstrap.Ensure(winFspBin);

            var fs = new WinFspMount(Vfs, _logPath) { MountPoint = Profile.MountPoint };
            var host = new FileSystemHost(fs)
            {
                // Tools that create junctions ask the volume what file system it is first, and
                // cmd refuses anything that does not answer "NTFS" with "Local NTFS volumes are
                // required". The mount keeps reparse points and resolves name surrogates itself,
                // so it answers as what those tools are checking for rather than lose the one
                // capability they are looking for. (A workspace that links directories — a game
                // engine's, for one — creates junctions on every start.)
                FileSystemName = "NTFS",
                VolumeSerialNumber = Serial(Profile.Name),
                CaseSensitiveSearch = false,
                CasePreservedNames = true,
                UnicodeOnDisk = true,
                // Symbolic links and junctions are reparse points: a writable layer keeps their
                // bytes and Windows resolves the target itself, which is what makes a mount with
                // an overlay behave like an ordinary directory to tools that link workspaces.
                ReparsePoints = true,
                PersistentAcls = false,
                PostCleanupWhenModifiedOnly = true,
                PassQueryDirectoryPattern = true,
                // Let WinFsp reuse metadata between stat/open/read calls. Ordinary
                // filesystem mutations update its cache; changes made directly in
                // backing layers become visible within this bounded timeout.
                FileInfoTimeout = 1000
            };

            // An elevated process can register the volume with the Windows Mount Manager,
            // which is what makes the mount a volume that AppLocker and the UAC consent path
            // can resolve — and therefore what lets programs stored on it start. Without it
            // the mount still serves data; it just cannot be a place to run something from.
            string? registered = null;
            try
            {
                if (MountPointVolume.CanRegister)
                {
                    if (MountPointVolume.IsDriveLetter(Profile.MountPoint, out string letter))
                    {
                        int rc = host.Mount(MountPointVolume.ManagerName(letter), null, false, 0);
                        if (rc < 0)
                            throw new IOException(
                                $"mount failed for '{Profile.MountPoint}' through the Windows Mount Manager: 0x{rc:X8}");
                    }
                    else
                    {
                        MountPointVolume.MountAtDirectory(host, Profile.MountPoint);
                        registered = Profile.MountPoint;
                    }
                }
                else
                {
                    PrepareMountPoint(Profile.MountPoint);
                    int rc = host.Mount(Profile.MountPoint, null, false, 0);
                    if (rc < 0)
                        throw new IOException($"mount failed for '{Profile.MountPoint}': 0x{rc:X8}");
                }
            }
            catch
            {
                host.Dispose();
                throw;
            }

            _registeredDirectory = registered;
            _host = host;
            _changes ??= OnVfsChanged;
            Vfs.Changed += _changes;
            StartBackground();
        }
    }

    /// <summary>Tells Windows what changed. The resolver raises this off the writer's thread, so
    /// a notification still in flight never delays the write that caused it, and waiting for a
    /// rename here cannot deadlock against the mutation gate.</summary>
    private void OnVfsChanged(IReadOnlyList<FileChange> changes)
    {
        var host = Volatile.Read(ref _host);
        if (host == null || changes.Count == 0) return;
        var infos = new Fsp.Interop.NotifyInfo[changes.Count];
        for (int i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            infos[i] = new Fsp.Interop.NotifyInfo
            {
                // Normalised the way WinFsp wants it: a Windows path with no drive prefix.
                FileName = "\\" + change.Path.Replace('/', '\\'),
                Action = change.Kind switch
                {
                    FileChangeKind.Added => Fsp.Interop.NotifyAction.Added,
                    FileChangeKind.Removed => Fsp.Interop.NotifyAction.Removed,
                    FileChangeKind.RenamedOld => Fsp.Interop.NotifyAction.RenamedOldName,
                    FileChangeKind.RenamedNew => Fsp.Interop.NotifyAction.RenamedNewName,
                    _ => Fsp.Interop.NotifyAction.Modified
                },
                // ChangeName covers both kinds of name, so one filter serves files and folders.
                Filter = Fsp.Interop.NotifyFilter.ChangeName | Fsp.Interop.NotifyFilter.ChangeSize
                       | Fsp.Interop.NotifyFilter.ChangeLastWrite | Fsp.Interop.NotifyFilter.ChangeCreation
                       | Fsp.Interop.NotifyFilter.ChangeAttributes
            };
        }
        try
        {
            // CANT_WAIT: a rename is in progress and Windows is not taking notifications now.
            // Dropping one batch is better than blocking a writer on a rename.
            if (host.NotifyBegin((uint)infos.Length) < 0) return;
            try { host.Notify(infos); }
            finally { host.NotifyEnd(); }
        }
        catch { /* the volume is going away */ }
    }

    /// <summary>Starts every background layer's pass. The layer does the work; the mount
    /// decides when, so an unmounted instance fetches nothing.</summary>
    private void StartBackground()
    {
        StopBackground();
        foreach (var layer in _background) layer.Start();
    }

    private void StopBackground()
    {
        foreach (var layer in _background) layer.Stop();
    }

    public void Unmount()
    {
        StopBackground();
        FileSystemHost host;
        string? registered;
        lock (_gate)
        {
            if (_host == null) return;
            host = _host;
            registered = _registeredDirectory;
            _registeredDirectory = null;
            _host = null;
            _unmounting = true;
            if (_changes != null) Vfs.Changed -= _changes;
        }
        try
        {
            // The directory mount point names a volume that is about to disappear. Removing it
            // first keeps it from being left pointing at nothing after the file system goes.
            if (registered != null) MountPointVolume.DetachDirectory(registered);
            host.Unmount();
        }
        finally
        {
            try { host.Dispose(); }
            finally { lock (_gate) _unmounting = false; }
        }
    }

    public void SetPin(long revision, string? layer = null)
    {
        var revisions = _layers.Where(p => layer == null || p.Key == layer).Select(p => p.Value).OfType<IRevisionSource>().ToList();
        if (revisions.Count == 0) throw new NotSupportedException("source has no revision");
        foreach (var rev in revisions) rev.SetRevision(revision);
        // The listings already warmed belong to the version just left; warm the new one.
        if (Mounted) StartBackground();
    }

    /// <summary>Moves the named layer to the source's HEAD and reports the revision it now
    /// serves. When no layer is named every revision source is moved, and the last one's
    /// revision is reported: callers that need a particular one name it.</summary>
    public long RefreshHead(string? layer = null)
    {
        var revisions = _layers.Where(p => layer == null || p.Key == layer).Select(p => p.Value).OfType<IRevisionSource>().ToList();
        if (revisions.Count == 0) throw new NotSupportedException("source has no revision");
        long revision = 0;
        foreach (var rev in revisions) { revision = rev.HeadRevision(); rev.SetRevision(revision); }
        if (Mounted) StartBackground();
        return revision;
    }

    public void ClearCache(string? layer = null)
    {
        foreach (var provider in _layers.Where(p => layer == null || p.Key == layer).Select(p => p.Value))
            if (provider is CacheLayer cache) cache.Clear();
            else if (provider is ICacheLayer legacy) legacy.Invalidate();
    }

    public void ClearLocalChanges(string layer, string winFspBin)
    {
        if (!_layers.TryGetValue(layer, out var provider) || provider is not OverlayLayer overlay)
            throw new NotSupportedException($"layer '{layer}' cannot clear local changes");
        bool remount = Mounted;
        Unmount();
        try { overlay.ClearLocalChanges(); Vfs.InvalidateCaches(); }
        finally { if (remount) Mount(winFspBin); }
    }

    public MountMetrics GetMetrics(double windowSeconds)
    {
        // Being asked is what turns counting on. Nothing counts an instance nobody is looking at,
        // which is what makes this affordable to have at all - the first window after a quiet spell
        // is short, and says so through MeasuredSeconds rather than pretending to a minute.
        Metrics.Watch();
        var m = Metrics.Snapshot(Profile.Name, windowSeconds);
        // Include every active provider, even when it served no reads in this window.
        m.Layers = Vfs.Layers.Select(layer => m.Layers.FirstOrDefault(l => l.Name == layer.Name) ?? new LayerMetric { Name = layer.Name }).ToList();
        foreach (var lm in m.Layers)
        {
            lm.Color = ResolveColor(lm.Name);
            lm.DisplayName = _configs[lm.Name].Name;
            if (_layers[lm.Name] is PredictionLayerBase readAhead)
            {
                lm.Predictive = true;
                lm.HeldUsed = readAhead.Used;
                lm.HeldWasted = readAhead.Wasted;
                lm.HeldCount = readAhead.HeldObjects;
                lm.HeldWastedBytes = readAhead.WastedBytes;
                m.PredictiveWasted ??= LayerTexts.Get("prediction.outcome.wasted");
            }
            // The block cache answers no reads of its own, so the only account of its work is
            // the bytes it kept out of the stream underneath.
            if (_layers[lm.Name] is MemoryCacheLayer memory)
            {
                lm.Accelerating = true;
                lm.AcceleratedBytes = memory.AcceleratedBytes;
                lm.FetchedBytes = memory.FetchedBytes;
            }
        }
        return m;
    }

    public ProbeResult Probe(string rel)
    {
        var layers = Vfs.Probe(rel);
        foreach (var lp in layers)
        {
            lp.Color = ResolveColor(lp.Name);
            lp.Type = _configs[lp.Name].Type;
            lp.Manageable = _layers[lp.Name] is IObjectManagement;
        }

        var effective = layers.FirstOrDefault(l => l.Effective);
        return new ProbeResult
        {
            Mount = Profile.Name,
            MountPoint = Profile.MountPoint,
            Rel = rel,
            Found = effective != null,
            IsDir = effective?.IsDir ?? false,
            EffectiveLayer = effective?.Name ?? "",
            EffectiveColor = effective?.Color ?? "",
            EffectiveIcon = effective is null ? "" : LayerRegistry.Describe(_configs[effective.Name].Type).Icon,
            Layers = layers
        };
    }

    // Mutate a single layer's state for one path, then return the refreshed probe.
    public ProbeResult ReleaseAbove(string rel, int boundary, IReadOnlyList<string> expected)
    {
        if (boundary < 0 || boundary > Vfs.Layers.Count || !Vfs.Layers.Select(l => l.Name).SequenceEqual(expected))
            throw new InvalidOperationException("Layer chain changed; refresh before applying");
        bool remount = Mounted;
        Unmount();
        try
        {
            foreach (var layer in Vfs.Layers.Take(boundary))
                if (_layers[layer.Name] is IObjectManagement management) management.ReleaseObject(rel);
            // Bottom-up, so each layer's parent listing is reconciled against what is now below it.
            foreach (var layer in Vfs.Layers.Take(boundary).Reverse())
                if (_layers[layer.Name] is OverlayLayer) Vfs.ReconcileAfterRelease(rel, layer.Name);
        }
        finally { if (remount) Mount(_lastWinFspBin); }
        return Probe(rel);
    }
    /// <summary>The layers this instance built, keyed by the configuration id they were built
    /// from. A plugin command that belongs to one of its own layers reaches it through this
    /// without Core learning what that layer is.</summary>
    public IReadOnlyDictionary<string, ILayer> Layers => _layers;

    [Obsolete("Use Layers.")]
    public IReadOnlyDictionary<string, ILayer> Providers => Layers;

    public ProbeResult ApplyLayerAction(string layer, string rel, string action)
    {
        if (!_layers.TryGetValue(layer, out var provider)) throw new NotSupportedException($"unknown layer '{layer}'");
        // A layer that declares actions of its own answers for them; Core only knows the
        // actions of the layer types it ships.
        if (provider is ILayerActionHandler custom && custom.TryApplyAction(rel, action)) return Probe(rel);
        switch (provider)
        {
            case OverlayLayer overlay:
                if (action != LayerActions.DropLocal) throw new NotSupportedException($"overlay has no action '{action}'");
                // Discarding a local copy changes what is visible beneath it, so anything
                // cached above the overlay for this tree is now stale.
                overlay.DropObject(rel);
                Vfs.ReconcileAfterRelease(rel, layer);
                Vfs.InvalidateCaches();
                break;

            case LruCacheLayer cache:
                if (action != LayerActions.EvictCache) throw new NotSupportedException($"cache has no action '{action}'");
                // A cached path is not visible state: dropping one says nothing about any
                // other, so this must not discard the rest of the cache. The layer reports
                // Writable because it can be a write target in a chain without an overlay,
                // which is why this cannot be decided from Writable alone.
                cache.InvalidateEntry(rel);
                break;

            default:
                throw new NotSupportedException($"layer '{layer}' is not editable");
        }

        return Probe(rel);
    }

    public MountStatus GetStatus()
    {
        // What a background layer reports is its own: the mount sums the counts and shows the
        // first layer's wording, without knowing what kind of work it is.
        var progress = _background.Select(layer => layer.Progress).OfType<LayerProgress>().ToList();
        var status = new MountStatus
        {
            Name = Profile.Name,
            Enabled = Profile.Enabled,
            Mounted = Mounted,
            MountPoint = Profile.MountPoint,
            SourceType = string.Join(" + ", _configs.Values.Where(c => SourceRegistry.Contains(c.Type)).Select(c => c.Type).Distinct()),
            Url = string.Join("\n", _configs.Values.Where(c => SourceRegistry.Contains(c.Type)).Select(c => c.Type == "dir" ? c.Source.Path : c.Source.Url)),
            CacheBytes = _layers.Values.OfType<LruCacheLayer>().Sum(c => c.TotalBytes),
            CacheCapacity = _layers.Values.OfType<LruCacheLayer>().Sum(c => c.Capacity),
            CacheCount = _layers.Values.OfType<LruCacheLayer>().Sum(c => c.Count),
            WarmedDirectories = progress.Sum(p => p.Completed),
            WarmupLimit = progress.FirstOrDefault()?.Total ?? 0,
            WarmupDetail = progress.FirstOrDefault()?.Detail ?? LayerTexts.Get("warmup.off"),
            // Two volatile reads: a front-end polls this once per request and must not make it
            // list a directory to answer whether a recording is running.
            Recording = Vfs.Diagnostics?.Recording ?? false,
            RecordingStartedUtc = Vfs.Diagnostics?.RecordingStartedUtc
        };

        foreach (var layer in Vfs.Layers)
        {
            var info = new ProviderInfo { Name = layer.Name, DisplayName = _configs[layer.Name].Name, Type = _configs[layer.Name].Type, Color = ResolveColor(layer.Name), Writable = layer.Writable, Generation = layer.Generation };
            if (_layers[layer.Name] is MemoryCacheLayer memory)
            {
                info.BlockCacheHits = memory.Hits; info.BlockCacheMisses = memory.Misses; info.BlockWriteBacks = memory.WriteBacks;
                info.MemoryDetail = LayerTexts.Get("provider.memory");
            }
            if (_layers[layer.Name] is PredictionLayerBase prediction)
            {
                info.LearnedTransitions = prediction.LearnedTransitions;
                info.PreloadsStarted = prediction.PreloadsStarted;
                info.PreloadsCompleted = prediction.PreloadsCompleted;
                info.HeldHits = prediction.CacheHits;
                info.HeldWasted = prediction.Wasted;
                info.HeldBytes = prediction.MemoryBytes;
                info.HeldCapacity = prediction.Capacity;
                info.SiblingPreloads = prediction.SiblingPreloads;
                info.SiblingCompleted = prediction.SiblingCompleted;
                info.SiblingHits = prediction.SiblingHits;
                info.SiblingWasted = prediction.SiblingWasted;
                info.SiblingDirectories = prediction.TrackedDirectories;
                info.SpatialPreloads = prediction.SpatialPreloads;
                info.SpatialCompleted = prediction.SpatialCompleted;
                info.SpatialHits = prediction.SpatialHits;
                info.SpatialWasted = prediction.SpatialWasted;
                info.BurstPreloads = prediction.BurstPreloads;
                info.BurstCompleted = prediction.BurstCompleted;
                info.BurstHits = prediction.BurstHits;
                info.BurstWasted = prediction.BurstWasted;
                info.BurstGroups = prediction.BurstGroups;
                info.BurstClosedGroups = prediction.BurstClosedGroups;
                info.PredictionStreams = prediction.ActiveStreams;
                info.Invalidated = prediction.Invalidated;
                info.PredictionDetail = LayerTexts.Get("provider.prediction");
            }
            // A source that serves read-ahead at a priority of its own reports what admitting
            // every level cost; one that serves a single priority has nothing to add here.
            if (_layers[layer.Name] is IReadLaneSource lanes) info.ReadLanes = lanes.Lanes.Snapshot();
            if (_layers[layer.Name] is ILayerStorageUsage storage)
            {
                try { info.StorageUsage = storage.GetStorageUsage(); }
                catch (IOException ex) { info.StorageError = ex.Message; }
                catch (UnauthorizedAccessException ex) { info.StorageError = ex.Message; }
            }
            status.Providers.Add(info);
        }

        if (PrimarySource is IRevisionSource rev) status.Revision = rev.Revision;
        // The process's own footprint, read by the process itself. A front end that is not
        // elevated cannot open a LocalSystem worker, and this is the total the layers' holdings
        // are read against: reported here so the reconciliation works whichever account asks.
        try { status.WorkingSetBytes = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64; }
        catch (Exception) { }
        return status;
    }

    public void Dispose()
    {
        PolyMountEventSource.Log.Unregister();
        StopBackground();
        Vfs.Prediction?.Dispose();
        try { Unmount(); }
        finally
        {
            foreach (var provider in _layers.Values.OfType<IDisposable>()) provider.Dispose();
            Vfs.Diagnostics?.Dispose();
        }
    }

    private string ResolveColor(string name) => _configs.TryGetValue(name, out var config) && !string.IsNullOrEmpty(config.Color)
        ? config.Color : LayerColors.Resolve(_configs.TryGetValue(name, out config) ? config.Type : name, ColorOverrides);

    // WinFsp creates its own reparse-point directory for a directory mount point, so the
    // target must NOT already exist. A pre-existing (even empty) directory causes
    // STATUS_OBJECT_NAME_COLLISION (0xC0000035). Drive-letter mount points (e.g. "X:") are
    // left untouched. This is the path an unprivileged process takes; an elevated one mounts
    // through the Windows Mount Manager instead (MountPointVolume), where the directory is
    // kept and becomes the mount point itself.
    internal static void PrepareMountPoint(string mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint)) return;

        // Drive-letter mount points ("X:" or "X:\") are handled by WinFsp directly.
        var trimmed = mountPoint.TrimEnd('\\', '/');
        if (trimmed.Length == 2 && trimmed[1] == ':' && char.IsLetter(trimmed[0])) return;

        if (!Directory.Exists(mountPoint)) return;

        bool empty = !Directory.EnumerateFileSystemEntries(mountPoint).Any();
        if (!empty)
            throw new IOException(
                $"mount point '{mountPoint}' already exists and is not empty; choose an empty or non-existent path");

        try
        {
            Directory.Delete(mountPoint, false);
        }
        catch (Exception ex)
        {
            throw new IOException($"failed to clear existing mount point '{mountPoint}': {ex.Message}", ex);
        }
    }

    private static uint Serial(string name)
    {
        uint hash = 2166136261;
        foreach (var c in name) { hash ^= c; hash *= 16777619; }
        return hash;
    }
}
