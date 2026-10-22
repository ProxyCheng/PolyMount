using PolyMount.Core;

namespace PolyMount.Layers;

/// <summary>The namespace store, and the background pass that fills it.
///
/// A listing carries its children's headers, so one directory answers a whole directory's stat
/// traffic, and every path beneath it needs it resolved first. That is why this store keeps
/// listings and never evicts one: a miss costs a source round trip for the directory, and losing
/// an ancestor makes everything below it unreachable. A capacity-ordered eviction would pick
/// exactly the entries whose loss is the most expensive, which is why the rule lives here rather
/// than in the content cache that happens to sit below. File content is not this layer's business
/// at all — it holds no bytes.
///
/// The pass after a mount walks the tree and stores each listing here. A client's own reads fill
/// it too, through the resolver's fill path, so a directory read once is warm for the next reader
/// at the same generation. Keys carry the lower chain's generation: a source that moved is
/// answered by fetching rather than by these rows, and the rows themselves are dropped when the
/// generation changes, so the store is bounded by one revision's tree rather than by every
/// revision the instance has ever read.
/// </summary>
public sealed class ListingWarmupLayer : CacheLayer, ICacheLayer, IWriteAwareCache, IMountAwareLayer,
    IQuietProbe, ILayerStorageUsage, INamespaceStore, IDisposable
{
    /// <summary>The type name this layer is registered and configured under.</summary>
    public const string Type = "warmup";

    /// <summary>How often the lower chain's generation is re-read. The resolver asks for it on
    /// every store and every hit, and the chain's answer is a hash of the layers below — far too
    /// much work to repeat per lookup. A revision is a management action, so a quarter of a
    /// second of staleness on a source that moves under a running mount is not observable; the
    /// mount's own revision commands refresh it explicitly through <see cref="Invalidate"/>.</summary>
    private const int GenerationCheckMilliseconds = 250;

    private readonly ObjectDatabase _store;
    private readonly Func<string> _generation;
    private readonly int _limit;
    private readonly int _seconds;
    private readonly int _quietMs;
    private readonly object _gate = new();
    private readonly string _generationPath;
    private string _generationText;
    private long _generationCheckedAt;
    /// <summary>The generation the rows in the store were written under. A row names its own
    /// generation, so a moved source is answered by fetching; this is what tells the layer when
    /// the old rows have become garbage and the store is worth emptying, which is the only bound
    /// a store that never evicts has.</summary>
    private string _storedGeneration;
    private VirtualFileSystem? _lower;
    private Func<TimeSpan>? _idleFor;
    private ListingWarmup? _walk;
    private long _lastWarmed;
    private long _hits;
    private bool _disposed;

    public ListingWarmupLayer(string storage, Func<string> generation, int limit, int seconds, int quietMs = 250)
    {
        _generation = generation;
        _generationText = generation();
        _generationCheckedAt = Environment.TickCount64 + GenerationCheckMilliseconds;
        _limit = limit;
        _seconds = seconds;
        _quietMs = quietMs;
        // A cache's store: the last transactions may be lost to a power cut, and that costs a
        // fetch from the source rather than a user's data. Rows of the generation being mounted
        // are loaded first, so a store that still holds an older revision cannot crowd the
        // current one out of the resident index before it is used.
        _store = new ObjectDatabase(storage, compress: false, recoverable: true,
            preferredPrefix: _generationText + "|");
        _generationPath = Path.Combine(storage, "generation.txt");
        // What the rows in the store were written under. A first run has no file: the store is
        // empty or was left by a version that kept it elsewhere, so the current generation is
        // the honest answer and the file is written to be compared against next time.
        string? stored = ReadStoredGeneration();
        if (stored == null) RecordGeneration(_generationText);
        _storedGeneration = stored ?? _generationText;
    }

    public override string Name => Type;
    public override string Generation => CurrentGeneration();
    public override bool Writable => false;

    /// <summary>Directories this layer may fetch in one pass.</summary>
    public int Limit => _limit;

    /// <summary>Listings this layer has answered since it was built.</summary>
    /// <summary>Listings this layer has answered since it was built.</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Directory records this store holds right now.</summary>
    public int Listings => _store.Count;

    /// <summary>Asks the chain for its generation now and drops the memo.</summary>
    private string RefreshGenerationText()
    {
        _generationText = _generation();
        _generationCheckedAt = Environment.TickCount64 + GenerationCheckMilliseconds;
        return _generationText;
    }

    private string? ReadStoredGeneration()
    {
        try { return File.Exists(_generationPath) ? File.ReadAllText(_generationPath).Trim() : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private void RecordGeneration(string generation)
    {
        _storedGeneration = generation;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_generationPath)!);
            File.WriteAllText(_generationPath, generation);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Empties the store when it was filled under another generation. A row names its
    /// own generation and would never be read again, so keeping it would let a store that never
    /// evicts grow by a revision's tree every time the source moves.</summary>
    private void DropOtherGeneration(string generation)
    {
        if (string.Equals(_storedGeneration, generation, StringComparison.Ordinal)) return;
        _store.Clear();
        RecordGeneration(generation);
    }

    /// <summary>The chain's generation, refreshed at most every <see cref="GenerationCheckMilliseconds"/>
    /// so a lookup does not hash the layers below it.</summary>
    private string CurrentGeneration()
    {
        long now = Environment.TickCount64;
        if (now >= _generationCheckedAt)
        {
            _generationCheckedAt = now + GenerationCheckMilliseconds;
            _generationText = _generation();
        }
        return _generationText;
    }

    /// <summary>Whether the rows in the store belong to the generation now being served. A
    /// moved source makes the whole store stale at once, and the store is emptied rather than
    /// keyed by generation: a lookup on the hot path is a dictionary read, not a composite
    /// string. Until the pass that empties it has run, nothing here is served.</summary>
    private bool Current() => string.Equals(CurrentGeneration(), _storedGeneration, StringComparison.Ordinal);

    /// <summary>Drops the memoised generation so the next lookup asks the chain again. The
    /// production path reaches the same state through <see cref="Invalidate"/>; the tests use
    /// this to pin the generation rule without waiting out the check interval.</summary>
    internal void RefreshGeneration() => _generationCheckedAt = 0;

    public override bool TryReadObject(string path, out VirtualObject value)
    {
        if (TryPeek(path, out value))
        {
            Interlocked.Increment(ref _hits);
            return true;
        }
        return false;
    }

    /// <summary>The store's answer, without counting it as a use. Inspection — the layer view,
    /// Explorer's origin badges — asks through <see cref="TryPeekEntry"/>.
    ///
    /// The path is the key: the resolver normalizes before it resolves, and the store is emptied
    /// when the generation moves, so neither a normalizing pass nor a composite of the generation
    /// and the path is worth paying per lookup.</summary>
    private bool TryPeek(string path, out VirtualObject value)
    {
        if (!Current() || !_store.TryGet(path, out value) || !value.Header.IsDir || value.Children == null)
        {
            value = null!;
            return false;
        }
        return true;
    }

    /// <summary>A complete listing only. This layer holds no file content, so it can answer for
    /// a directory and for nothing else; a file's header reaches the resolver through its
    /// parent's listing, which is where the header came from in the first place.</summary>
    public override bool TryGetEntry(string path, out VfsEntry entry)
    {
        if (TryReadObject(path, out var value)) { entry = value.Header; return true; }
        entry = null!;
        return false;
    }

    public bool TryPeekEntry(string path, out VfsEntry entry)
    {
        if (TryPeek(path, out var value)) { entry = value.Header; return true; }
        entry = null!;
        return false;
    }

    /// <summary>The children of a listing the resolver has already resolved here. It is a
    /// follow-up to <see cref="TryGetEntry"/>, not a second answer, so it does not count.</summary>
    public override IReadOnlyList<VfsEntry> ListOwn(string path) =>
        TryPeek(path, out var value) ? value.Children ?? Array.Empty<VfsEntry>() : Array.Empty<VfsEntry>();

    public override bool TryOpenRead(string path, out VfsEntry entry, out Stream stream)
    {
        entry = null!;
        stream = null!;
        return false;
    }

    /// <summary>Content stores are not this layer's to keep: the resolver offers every cache
    /// the object it just read, and this one takes only the listings.</summary>
    public void Store(string path, string version, VfsEntry entry, Stream content) { }

    public void StoreObject(string path, VirtualObject value, Stream? content = null)
    {
        if (!value.Header.IsDir || value.Children == null) return;
        // The first pass after a restart re-walks everything the last one stored. Re-writing a
        // row that is already there costs a serialization and a transaction per directory for an
        // answer that has not changed; anything that does change arrives through a write. The
        // resident index answers this without reading the store back.
        if (_store.Contains(path)) return;
        StoreListing(path, value);
    }

    private void StoreListing(string path, VirtualObject value)
    {
        if (_disposed || !Current()) return;
        _store.Apply(new[] { new ObjectChange(path, value.Snapshot()) });
    }

    /// <summary>Nothing here is ordered by recency, so a read of a path a higher layer answered
    /// has nothing to refresh.</summary>
    public void TouchObject(string path) { }

    /// <summary>A write makes this listing stale: the parent's new listing arrives with the
    /// change and replaces the old one, and a path that was removed or replaced loses its row.
    /// Descendants are not swept — the resolver cannot reach a path whose ancestor no longer
    /// resolves here, and a directory that is re-created re-fills its own listing.</summary>
    public void UpdateAfterWrite(IReadOnlyList<ObjectChange> changes)
    {
        if (_disposed || changes.Count == 0 || !Current()) return;
        var edits = new List<ObjectChange>(changes.Count);
        foreach (var change in changes)
        {
            if (change.Object is { Header.IsDir: true, Children: not null } directory)
                edits.Add(new ObjectChange(change.Path, directory.Snapshot()));
            else
                edits.Add(new ObjectChange(change.Path, null));
        }
        _store.Apply(edits);
    }

    /// <summary>Drops every listing and picks up the generation now being served. Called when
    /// the instance's caches are invalidated, which is what a revision change does.</summary>
    public void Invalidate()
    {
        string generation = RefreshGenerationText();
        _store.Clear();
        RecordGeneration(generation);
    }

    public override void Clear() => Invalidate();

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
            StopWalk();
            _lastWarmed = 0;
            // A revision that moved under the last pass left rows nothing will read again.
            DropOtherGeneration(RefreshGenerationText());
            // The pass reads through this layer as well as the chain beneath it. It is the
            // store it fills: without itself in the chain every directory would resolve its
            // ancestors from the source again, and a tree of depth d would cost the source
            // d times what listing it once costs. The caches below are still read — that is
            // how a directory another reader already fetched stays a local answer — and the
            // resolver does not hand them a second copy of a listing (see NamespaceStoreAbove).
            var chain = new ILayer[1 + _lower.Layers.Count];
            chain[0] = this;
            for (int i = 0; i < _lower.Layers.Count; i++) chain[i + 1] = _lower.Layers[i];
            _walk = new ListingWarmup(new VirtualFileSystem(chain) { Diagnostics = _lower.Diagnostics },
                _idleFor, _limit, _quietMs, _seconds);
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

    public LayerStorageUsage GetStorageUsage() => _store.GetStorageUsage();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopWalk();
            _store.Dispose();
        }
    }
}
