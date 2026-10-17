using PolyMount.Core;

namespace PolyMount.Layers;

/// <summary>
/// Warms directory listings into the caches below this layer, and serves no read itself.
/// <para>
/// Where the layer sits is what it warms: the walk goes through a resolver built from the
/// layers beneath it, so every listing is stored by the caches between this layer and the
/// layer that answered. At the top of a chain that is all of them; between a memory cache
/// and a disk cache it is the disk cache only. A layer with nothing below it has nothing
/// to warm and starts nothing.
/// </para>
/// <para>
/// It is a pass-through: every lookup returns false, so the resolver moves on to the next
/// layer. The walk is <see cref="ListingWarmup"/>'s, unchanged: one directory at a time,
/// only while the instance has been quiet, and only up to its budget. The mount tells it
/// when to start and stop, so an instance that is not mounted has no walk going.
/// </para>
/// </summary>
public sealed class ListingWarmupLayer : ILayer, IMountAwareLayer, IDisposable
{
    /// <summary>The type name this layer is registered and configured under.</summary>
    public const string Type = "warmup";

    private readonly Func<string> _generation;
    private readonly int _limit;
    private readonly int _seconds;
    private readonly int _quietMs;
    private readonly object _gate = new();
    private VirtualFileSystem? _lower;
    private Func<TimeSpan>? _idleFor;
    private ListingWarmup? _walk;
    private long _lastWarmed;
    private bool _disposed;

    public ListingWarmupLayer(Func<string> generation, int limit, int seconds, int quietMs = 250)
    {
        _generation = generation;
        _limit = limit;
        _seconds = seconds;
        _quietMs = quietMs;
    }

    public string Name => Type;
    public string Generation => _generation();
    public bool Writable => false;

    /// <summary>Directories this layer may fetch in one pass.</summary>
    public int Limit => _limit;

    public bool TryGetEntry(string rel, out VfsEntry entry) { entry = null!; return false; }

    public IReadOnlyList<VfsEntry> ListOwn(string rel) => Array.Empty<VfsEntry>();

    public bool TryOpenRead(string rel, out VfsEntry entry, out Stream stream)
    {
        entry = null!;
        stream = null!;
        return false;
    }

    /// <summary>Hands the layer the chain below it and the instance's quiet-time reading.
    /// Called once the whole chain is built, before the mount starts it.</summary>
    public void Attach(VirtualFileSystem lower, Func<TimeSpan> idleFor)
    {
        lock (_gate)
        {
            _lower = lower;
            _idleFor = idleFor;
        }
    }

    /// <summary>Starts a pass. A pass already running belongs to the version it started at,
    /// so it is stopped and replaced, not joined.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _lower == null || _idleFor == null || _limit <= 0) return;
            // A pass is only useful if something below keeps what it fetches. Nothing here
            // assumes a cache is present, or which kind: the chain is asked, and a chain of
            // sources alone simply has nothing to warm.
            if (!_lower.Layers.Any(layer => layer is IObjectCache)) return;
            StopWalk();
            _lastWarmed = 0;
            _walk = new ListingWarmup(_lower, _idleFor, _limit, _quietMs, _seconds);
        }
    }

    /// <summary>Stops the pass. The count stays readable, so a status poll after an unmount
    /// still reports what the last pass warmed.</summary>
    public void Stop()
    {
        lock (_gate) StopWalk();
    }

    private void StopWalk()
    {
        if (_walk == null) return;
        _lastWarmed = _walk.WarmedDirectories;
        _walk.Dispose();
        _walk = null;
    }

    /// <summary>Directories the last pass fetched and stored.</summary>
    public long WarmedDirectories
    {
        get { lock (_gate) return _walk?.WarmedDirectories ?? _lastWarmed; }
    }

    /// <summary>What the instance status shows for this layer. The wording belongs to the
    /// layer, so the mount does not have to know what kind of background work it started.</summary>
    public LayerProgress? Progress
    {
        get
        {
            if (_limit <= 0) return new LayerProgress(0, 0, LayerTexts.Get("warmup.off"));
            return new LayerProgress(WarmedDirectories, _limit, LayerTexts.Get("warmup.progress"));
        }
    }

    /// <summary>Waits for the current pass to finish, for tests and for scripts that would
    /// rather observe the result than poll.</summary>
    public bool WaitForCompletion(TimeSpan timeout)
    {
        lock (_gate) return _walk?.WaitForCompletion(timeout) ?? true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            StopWalk();
        }
    }
}
