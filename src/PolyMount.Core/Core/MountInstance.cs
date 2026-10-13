using Fsp;
using PolyMount.Core.Ipc;
using PolyMount.Layers;

namespace PolyMount.Core;

public sealed class MountInstance : IDisposable
{
    private readonly string _logPath;
    private readonly object _gate = new();
    private FileSystemHost? _host;

    public MountInstance(MountProfile profile)
    {
        Profile = profile;
        var state = Path.Combine(profile.StoreRoot, "state");
        Directory.CreateDirectory(state);
        _logPath = Path.Combine(state, "polymount.log");

        Overlay = new OverlayLayer(Path.Combine(profile.StoreRoot, "overlay"), Path.Combine(state, "tombstones"));
        Source = SourceRegistry.Create(profile.Source, state);

        var layers = new List<ILayer> { Overlay };

        // Local sources already live on disk, so a read-through cache only wastes space.
        if (Source is not ILocalSource)
        {
            Cache = new LruCacheLayer(Path.Combine(profile.StoreRoot, "cache"), () => Source.Generation,
                profile.CacheCapacityBytes);
            layers.Add(Cache);
        }

        layers.Add(Source);
        Vfs = new VirtualFileSystem(layers);
    }

    public MountProfile Profile { get; }
    public VirtualFileSystem Vfs { get; }
    public OverlayLayer Overlay { get; }
    public LruCacheLayer? Cache { get; }
    public ILayer Source { get; }

    public string Name => Profile.Name;
    public string MountPoint => Profile.MountPoint;

    public bool Mounted
    {
        get { lock (_gate) return _host != null; }
    }

    public void Mount(string winFspBin)
    {
        lock (_gate)
        {
            if (_host != null) return;
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
                PassQueryDirectoryPattern = false,
                FileInfoTimeout = 0
            };

            int rc = host.Mount(Profile.MountPoint, null, false, 0);
            if (rc < 0)
            {
                host.Dispose();
                throw new IOException($"mount failed for '{Profile.MountPoint}': 0x{rc:X8}");
            }
            _host = host;
        }
    }

    public void Unmount()
    {
        lock (_gate)
        {
            if (_host == null) return;
            try { _host.Unmount(); } catch { }
            try { _host.Dispose(); } catch { }
            _host = null;
        }
    }

    public void SetPin(long revision)
    {
        if (Source is IRevisionSource rev) rev.SetRevision(revision);
        else throw new NotSupportedException("source '" + Profile.Source.Type + "' has no revision");
    }

    public void RefreshHead()
    {
        if (Source is IRevisionSource rev) rev.SetRevision(rev.HeadRevision());
        else throw new NotSupportedException("source has no revision");
    }

    public void ClearCache() => Cache?.Purge();

    public ProbeResult Probe(string rel)
    {
        var layers = Vfs.Probe(rel);
        var effective = layers.FirstOrDefault(l => l.Effective);
        return new ProbeResult
        {
            Mount = Profile.Name,
            MountPoint = Profile.MountPoint,
            Rel = rel,
            Found = effective != null,
            IsDir = effective?.IsDir ?? false,
            Layers = layers
        };
    }

    // Mutate a single layer's state for one path, then return the refreshed probe.
    public ProbeResult ApplyLayerAction(string layer, string rel, string action)
    {
        switch (layer.ToLowerInvariant())
        {
            case "overlay":
                switch (action)
                {
                    case LayerActions.Restore:   Overlay.ClearTombstone(rel); break;        // 不存在 -> 不知道
                    case LayerActions.Hide:      Overlay.Delete(rel, shadowedBelow: true); break;   // -> 不存在
                    case LayerActions.DropLocal: Overlay.Delete(rel, shadowedBelow: false); break;  // 存在 -> 不知道
                    default: throw new NotSupportedException($"overlay has no action '{action}'");
                }
                break;

            case "cache":
                if (action != LayerActions.EvictCache)
                    throw new NotSupportedException($"cache has no action '{action}'");
                Cache?.InvalidateEntry(rel);
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
            SourceType = Profile.Source.Type,
            Url = Profile.Source.Url,
            CacheBytes = Cache?.TotalBytes ?? 0,
            CacheCapacity = Cache?.Capacity ?? 0,
            CacheCount = Cache?.Count ?? 0
        };

        foreach (var layer in Vfs.Layers)
            status.Providers.Add(new ProviderInfo { Name = layer.Name, Writable = layer.Writable, Generation = layer.Generation });

        if (Source is IRevisionSource rev) status.Revision = rev.Revision;
        return status;
    }

    public void Dispose() => Unmount();

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
