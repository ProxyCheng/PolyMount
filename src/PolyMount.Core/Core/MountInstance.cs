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
    private bool _unmounting;
    private ListingWarmup? _warmup;
    private string _lastWinFspBin = "";
    private readonly Dictionary<string, ILayer> _providers = new(StringComparer.OrdinalIgnoreCase);
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
            _providers.Add(cfg.Id, provider);
            built[i] = provider is ICacheLayer cache ? new NamedCacheLayer(cfg.Id, cache) : new NamedLayer(cfg.Id, provider);
        }
        Overlay = _providers.Values.OfType<OverlayLayer>().FirstOrDefault();
        Cache = _providers.Values.OfType<LruCacheLayer>().FirstOrDefault();
        Source = configs.Where(c => SourceRegistry.Contains(c.Type)).Select(c => _providers[c.Id]).FirstOrDefault();
        var layers = built.ToList();
        Vfs = new VirtualFileSystem(layers);
        Vfs.Metrics = Metrics;
        Vfs.Diagnostics = new RequestDiagnostics(Path.Combine(state, DiagnosticsLogName))
        {
            // Sessions live beside the sampled log but in a family of their own, so the export's
            // request family and its rolled segments stay exactly what they were.
            Channel = new RecordingChannel(Path.Combine(state, "recording"))
        };
        foreach (var cache in _providers.Values.OfType<LruCacheLayer>()) cache.Diagnostics = Vfs.Diagnostics;
        for (int i = 0; i < configs.Count; i++)
            if (_providers[configs[i].Id] is PredictionCacheLayer prediction)
                prediction.Attach(new VirtualFileSystem(layers.Skip(i + 1)) { Diagnostics = Vfs.Diagnostics });
    }

    public MountProfile Profile { get; }
    public VirtualFileSystem Vfs { get; }
    public OverlayLayer? Overlay { get; }
    public LruCacheLayer? Cache { get; }
    public ILayer? Source { get; }
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
        => Channel.Start(Profile.Name, label is { Length: > 0 } ? label : Profile.Name);

    public RecordingSessionInfo StopRecording(string reason) => Channel.Stop(reason);

    public RecordingSessionInfo RecordingInfo() => Channel.Info();

    /// <summary>Where the recording channel lives, and the size it stops itself at.</summary>
    public long RecordingCapacityBytes => RecordingChannel.SessionBytes;

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

            PrepareMountPoint(Profile.MountPoint);

            var fs = new WinFspMount(Vfs, _logPath);
            var host = new FileSystemHost(fs)
            {
                FileSystemName = "PolyMount",
                VolumeSerialNumber = Serial(Profile.Name),
                CaseSensitiveSearch = false,
                CasePreservedNames = true,
                UnicodeOnDisk = true,
                PersistentAcls = false,
                PostCleanupWhenModifiedOnly = true,
                PassQueryDirectoryPattern = true,
                // Let WinFsp reuse metadata between stat/open/read calls. Ordinary
                // filesystem mutations update its cache; changes made directly in
                // backing layers become visible within this bounded timeout.
                FileInfoTimeout = 1000
            };

            int rc = host.Mount(Profile.MountPoint, null, false, 0);
            if (rc < 0)
            {
                host.Dispose();
                throw new IOException($"mount failed for '{Profile.MountPoint}': 0x{rc:X8}");
            }
            _host = host;
            StartWarmup();
        }
    }

    /// <summary>Walks the listing tree in the background while the mount is not being read.</summary>
    private void StartWarmup()
    {
        StopWarmup();
        if (Profile.WarmListings && Profile.WarmListingsLimit > 0)
            _warmup = new ListingWarmup(Vfs, Metrics, Profile.WarmListingsLimit, seconds: Profile.WarmListingsSeconds);
    }

    private void StopWarmup() => Interlocked.Exchange(ref _warmup, null)?.Dispose();

    public void Unmount()
    {
        StopWarmup();
        FileSystemHost host;
        lock (_gate)
        {
            if (_host == null) return;
            host = _host;
            _host = null;
            _unmounting = true;
        }
        try { host.Unmount(); }
        finally
        {
            try { host.Dispose(); }
            finally { lock (_gate) _unmounting = false; }
        }
    }

    public void SetPin(long revision, string? layer = null)
    {
        var revisions = _providers.Where(p => layer == null || p.Key == layer).Select(p => p.Value).OfType<IRevisionSource>().ToList();
        if (revisions.Count == 0) throw new NotSupportedException("source has no revision");
        foreach (var rev in revisions) rev.SetRevision(revision);
        // The listings already warmed belong to the version just left; warm the new one.
        if (Mounted) StartWarmup();
    }

    public void RefreshHead(string? layer = null)
    {
        var revisions = _providers.Where(p => layer == null || p.Key == layer).Select(p => p.Value).OfType<IRevisionSource>().ToList();
        if (revisions.Count == 0) throw new NotSupportedException("source has no revision");
        foreach (var rev in revisions) rev.SetRevision(rev.HeadRevision());
        if (Mounted) StartWarmup();
    }

    public void ClearCache(string? layer = null)
    {
        foreach (var provider in _providers.Where(p => layer == null || p.Key == layer).Select(p => p.Value))
            if (provider is CacheLayer cache) cache.Clear();
            else if (provider is ICacheLayer legacy) legacy.Invalidate();
    }

    public void ClearLocalChanges(string layer, string winFspBin)
    {
        if (!_providers.TryGetValue(layer, out var provider) || provider is not OverlayLayer overlay)
            throw new NotSupportedException($"layer '{layer}' cannot clear local changes");
        bool remount = Mounted;
        Unmount();
        try { overlay.ClearLocalChanges(); Vfs.InvalidateCaches(); }
        finally { if (remount) Mount(winFspBin); }
    }

    public MountMetrics GetMetrics(int windowSeconds)
    {
        var m = Metrics.Snapshot(Profile.Name, windowSeconds);
        // Include every active provider, even when it served no reads in this window.
        m.Layers = Vfs.Layers.Select(layer => m.Layers.FirstOrDefault(l => l.Name == layer.Name) ?? new LayerMetric { Name = layer.Name }).ToList();
        foreach (var lm in m.Layers)
        {
            lm.Color = ResolveColor(lm.Name);
            lm.DisplayName = _configs[lm.Name].Name;
            if (_providers[lm.Name] is PredictionCacheLayer readAhead)
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
            if (_providers[lm.Name] is MemoryCacheLayer memory)
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
            lp.Manageable = _providers[lp.Name] is IObjectManagement;
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
                if (_providers[layer.Name] is IObjectManagement management) management.ReleaseObject(rel);
        }
        finally { if (remount) Mount(_lastWinFspBin); }
        return Probe(rel);
    }
    public ProbeResult ApplyLayerAction(string layer, string rel, string action)
    {
        if (!_providers.TryGetValue(layer, out var provider)) throw new NotSupportedException($"unknown layer '{layer}'");
        switch (provider)
        {
            case OverlayLayer overlay:
                if (action != LayerActions.DropLocal) throw new NotSupportedException($"overlay has no action '{action}'");
                // Discarding a local copy changes what is visible beneath it, so anything
                // cached above the overlay for this tree is now stale.
                overlay.DropObject(rel);
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
        var status = new MountStatus
        {
            Name = Profile.Name,
            Enabled = Profile.Enabled,
            Mounted = Mounted,
            MountPoint = Profile.MountPoint,
            SourceType = string.Join(" + ", _configs.Values.Where(c => SourceRegistry.Contains(c.Type)).Select(c => c.Type).Distinct()),
            Url = string.Join("\n", _configs.Values.Where(c => SourceRegistry.Contains(c.Type)).Select(c => c.Type == "dir" ? c.Source.Path : c.Source.Url)),
            CacheBytes = _providers.Values.OfType<LruCacheLayer>().Sum(c => c.TotalBytes),
            CacheCapacity = _providers.Values.OfType<LruCacheLayer>().Sum(c => c.Capacity),
            CacheCount = _providers.Values.OfType<LruCacheLayer>().Sum(c => c.Count),
            WarmedDirectories = _warmup?.WarmedDirectories ?? 0,
            WarmupLimit = Profile.WarmListings ? Profile.WarmListingsLimit : 0,
            WarmupDetail = LayerTexts.Get(Profile.WarmListings && Profile.WarmListingsLimit > 0 ? "warmup.progress" : "warmup.off"),
            // Two volatile reads: a front-end polls this once per request and must not make it
            // list a directory to answer whether a recording is running.
            Recording = Vfs.Diagnostics?.Recording ?? false,
            RecordingStartedUtc = Vfs.Diagnostics?.RecordingStartedUtc
        };

        foreach (var layer in Vfs.Layers)
        {
            var info = new ProviderInfo { Name = layer.Name, DisplayName = _configs[layer.Name].Name, Type = _configs[layer.Name].Type, Color = ResolveColor(layer.Name), Writable = layer.Writable, Generation = layer.Generation };
            if (_providers[layer.Name] is MemoryCacheLayer memory)
            {
                info.BlockCacheHits = memory.Hits; info.BlockCacheMisses = memory.Misses; info.BlockWriteBacks = memory.WriteBacks;
                info.MemoryDetail = LayerTexts.Get("provider.memory");
            }
            if (_providers[layer.Name] is PredictionCacheLayer prediction)
            {
                info.LearnedTransitions = prediction.LearnedTransitions;
                info.PreloadsStarted = prediction.PreloadsStarted;
                info.PreloadsCompleted = prediction.PreloadsCompleted;
                info.HeldHits = prediction.CacheHits;
                info.HeldWasted = prediction.Wasted;
                info.HeldBytes = prediction.MemoryBytes;
                info.HeldCapacity = prediction.Capacity;
                info.PredictionDetail = LayerTexts.Get("provider.prediction");
            }
            if (_providers[layer.Name] is ILayerStorageUsage storage)
            {
                try { info.StorageUsage = storage.GetStorageUsage(); }
                catch (IOException ex) { info.StorageError = ex.Message; }
                catch (UnauthorizedAccessException ex) { info.StorageError = ex.Message; }
            }
            status.Providers.Add(info);
        }

        if (Source is IRevisionSource rev) status.Revision = rev.Revision;
        return status;
    }

    public void Dispose()
    {
        StopWarmup();
        Vfs.Prediction?.Dispose();
        try { Unmount(); }
        finally
        {
            foreach (var provider in _providers.Values.OfType<IDisposable>()) provider.Dispose();
            Vfs.Diagnostics?.Dispose();
        }
    }

    private string ResolveColor(string name) => _configs.TryGetValue(name, out var config) && !string.IsNullOrEmpty(config.Color)
        ? config.Color : LayerColors.Resolve(_configs.TryGetValue(name, out config) ? config.Type : name, ColorOverrides);

    // WinFsp creates its own reparse-point directory for a directory mount point, so the
    // target must NOT already exist. A pre-existing (even empty) directory causes
    // STATUS_OBJECT_NAME_COLLISION (0xC0000035). Drive-letter mount points (e.g. "X:") are
    // left untouched.
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
