using System.Globalization;
using PolyMount.Core;

namespace PolyMount.Plugins.Svn;

public sealed class SvnLayer : ILayer, IRevisionSource, IReadLaneSource, IDisposable
{
    private readonly ISvnBackend _svn;
    private readonly string _pinPath;
    private readonly Dictionary<string, IReadOnlyList<VfsEntry>> _memo = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VfsEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SharedRequest<IReadOnlyList<VfsEntry>>> _listingRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SharedRequest<string>> _contentRequests = new(StringComparer.OrdinalIgnoreCase);
    // Memo keys whose listing came from read-ahead and that no demand call has used yet,
    // and keys whose children have already been queued for read-ahead.
    private readonly HashSet<string> _prefetched = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _cascaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly SvnPrefetchOptions _prefetchOptions;
    private readonly ReadLaneGate _lanes;
    private readonly SvnListingPrefetch? _prefetch;
    private long _prefetchHits;

    private long _rev;
    private DateTime _pinMtime = DateTime.MinValue;
    private readonly object _gate = new();
    private long _nextPinCheck;
    private readonly Dictionary<string, long> _memoAccess = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _memoSizes = new(StringComparer.OrdinalIgnoreCase);
    private long _metadataBytes;
    public long EstimatedMetadataBytes { get { lock (_gate) return _metadataBytes; } }
    public int CachedDirectories { get { lock (_gate) return _memo.Count; } }
    private void ClearMemo()
    {
        _memo.Clear(); _entries.Clear(); _memoAccess.Clear(); _memoSizes.Clear(); _metadataBytes = 0;
        _prefetched.Clear(); _cascaded.Clear(); _contentRequests.Clear();
    }
    private void StoreListing(string key, string generation, string path, IReadOnlyList<VfsEntry> list, bool speculative)
    {
        if (_memo.ContainsKey(key)) return;
        long size = CacheBudgets.Estimate(key, new VirtualObject(new VfsEntry { Name = VfsPath.Name(path), IsDir = true }, list));
        if (size > CacheBudgets.MetadataBytes) return;
        while (_memo.Count >= CacheBudgets.MaxObjects || _metadataBytes + size > CacheBudgets.MetadataBytes)
        {
            string oldest = _memoAccess.MinBy(p => p.Value).Key;
            var evicted = _memo[oldest];
            string prefix = oldest + (oldest.EndsWith('|') ? "" : "/");
            foreach (var child in evicted) _entries.Remove(prefix + child.Name);
            _metadataBytes -= _memoSizes[oldest];
            _memo.Remove(oldest); _memoAccess.Remove(oldest); _memoSizes.Remove(oldest);
            _prefetched.Remove(oldest);
            _cascaded.Remove(oldest);
        }
        _memo[key] = list; _memoAccess[key] = Environment.TickCount64; _metadataBytes += size;
        _memoSizes[key] = size;
        foreach (var child in list) _entries[generation + "|" + VfsPath.Child(path, child.Name)] = child;
        if (!speculative) return;
        if (_prefetched.Count >= 4096) _prefetched.Clear();
        _prefetched.Add(key);
    }

    /// <summary>Child directories worth fetching ahead: bounded, and only those whose listing
    /// is neither known nor already on its way.</summary>
    private List<string>? PendingChildren(string generation, string path, IReadOnlyList<VfsEntry> list)
    {
        if (_prefetch == null) return null;
        var pending = new List<string>();
        foreach (var child in list)
        {
            if (pending.Count >= _prefetchOptions.Children) break;
            if (!child.IsDir) continue;
            string rel = VfsPath.Child(path, child.Name);
            string key = generation + "|" + rel;
            if (_memo.ContainsKey(key) || _listingRequests.ContainsKey(key)) continue;
            pending.Add(rel);
        }
        return pending.Count > 0 ? pending : null;
    }

    internal SvnLayer(ISvnBackend svn, string pinPath, SvnPrefetchOptions? prefetch = null)
    {
        _svn = svn;
        _pinPath = pinPath;
        _prefetchOptions = prefetch ?? new SvnPrefetchOptions();
        // The source serves a fixed number of requests at once and the classes share it by
        // reservation. The reader is guaranteed all of it bar what read-ahead may hold at most,
        // so read-ahead being busy is never the reason a reader waits. Read-ahead keeps a floor
        // of one whatever the reader is doing — prediction that makes no progress under load
        // cannot pay for itself — and reaches its configured few on capacity nobody is using.
        int readAhead = _prefetchOptions.SpeculativeConcurrency;
        _lanes = new ReadLaneGate(SvnHttpClient.MaxConnections,
            new ReadLevel(SvnHttpClient.MaxConnections - readAhead, SvnHttpClient.MaxConnections),
            new ReadLevel(1, readAhead));
        if (prefetch is { Concurrency: > 0, Children: > 0 })
            _prefetch = new SvnListingPrefetch(prefetch.Concurrency, prefetch.QueueDepth, () => Revision, FetchListing);        Directory.CreateDirectory(Path.GetDirectoryName(pinPath)!);
    }

    /// <summary>
    /// Prefers the in-process HTTP transport for http(s) repositories, which avoids the
    /// per-request process spawn, and falls back to the svn command line client when the
    /// server refuses that transport or the mount relies on the svn credential store.
    /// </summary>
    public static SvnLayer Create(string url, string trustFailures, string pinPath,
        string? username = null, string? password = null, SvnPrefetchOptions? prefetch = null)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new(new FallbackSvnBackend(
                new SvnHttpClient(url, trustFailures, username, password),
                () => new SvnClient(url, trustFailures, username, password)), pinPath, prefetch);
        return new(new SvnClient(url, trustFailures, username, password), pinPath, prefetch);
    }

    public void Dispose()
    {
        _prefetch?.Dispose();
        _svn.Dispose();
    }

    /// <summary>The transport serving this layer; exposed so tests can assert the selection.</summary>
    internal ISvnBackend Backend => _svn;

    /// <summary>Listings fetched ahead of demand, and how many of them a demand call then used.</summary>
    public long PrefetchedListings => _prefetch?.Fetched ?? 0;
    public long PrefetchHits => Interlocked.Read(ref _prefetchHits);
    public long PrefetchStale => _prefetch?.Stale ?? 0;
    public long PrefetchFailures => _prefetch?.Failures ?? 0;

    /// <summary>Waits for read-ahead to settle; zero or one worker with an empty queue returns at once.</summary>
    internal bool WaitForPrefetch(TimeSpan timeout) => _prefetch?.WaitForIdle(timeout) ?? true;

    /// <summary>Fetches one directory for read-ahead, reporting whether a listing was kept.
    /// The diagnostics captured when the work was queued keep it visible in the request log,
    /// which a scope-less worker thread could not otherwise be.</summary>
    private bool FetchListing(string rel, RequestDiagnostics? diagnostics)
    {
        string key;
        lock (_gate)
        {
            key = Generation + "|" + rel;
            // A listing that is already known, or already being fetched for a caller,
            // needs no speculative request.
            if (_memo.ContainsKey(key) || _listingRequests.ContainsKey(key)) return false;
        }
        Listing(rel, speculative: true, diagnostics);
        lock (_gate) return _memo.ContainsKey(key);
    }

    public string Name => "svn";
    public bool Writable => false;

    /// <summary>Admission for this source's priority classes, so the mount can report what
    /// read-ahead queued for and what the reader was never made to wait for.</summary>
    public ReadLaneGate Lanes => _lanes;

    /// <summary>
    /// One source request that later callers join instead of issuing a second, and the class it
    /// was reserved in.
    /// <para>
    /// The reservation is taken where the request is published but admitted where it actually
    /// issues, which is what leaves a reader that arrives in between able to claim it: a reader
    /// joining a read-ahead request that is still queued for its class would otherwise be
    /// queueing behind the class it just joined, which is the whole thing the classes exist to
    /// prevent.
    /// </para>
    /// </summary>
    private sealed class SharedRequest<T>(ReadLaneGate.Admission admission, Func<T> work)
    {
        private readonly Lazy<T> _lazy = new(() =>
        {
            using (admission.Admit()) return work();
        }, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>Waits for the request, claiming it when the caller is the reader: read-ahead
        /// the reader then asked for is a guess that paid off, and it must not be something the
        /// reader waits behind.</summary>
        public T Join(int priority)
        {
            admission.Promote(priority);
            return _lazy.Value;
        }
    }

    public long HeadRevision() => _svn.GetHeadRevision();

    public void SetRevision(long rev)
    {
        File.WriteAllText(_pinPath, rev.ToString());
        lock (_gate)
        {
            _rev = rev;
            _pinMtime = File.GetLastWriteTimeUtc(_pinPath);
            ClearMemo();
        }
    }

    public long Revision
    {
        get
        {
            // Generation is consulted for every cache lookup. Poll external pin
            // changes at most four times per second rather than stat per object.
            long now = Environment.TickCount64;
            long next = Volatile.Read(ref _nextPinCheck);
            if (now < next || Interlocked.CompareExchange(ref _nextPinCheck, now + 250, next) != next)
                return Interlocked.Read(ref _rev);
            try
            {
                var mtime = File.GetLastWriteTimeUtc(_pinPath);
                if (mtime != _pinMtime)
                {
                    var txt = File.ReadAllText(_pinPath).Trim();
                    if (long.TryParse(txt, out var r) && r > 0)
                    {
                        lock (_gate)
                        {
                            if (r != _rev) { _rev = r; ClearMemo(); }
                            _pinMtime = mtime;
                        }
                    }
                }
            }
            catch { }
            return Interlocked.Read(ref _rev);
        }
    }

    /// <summary>The revision as a string, kept: this is consulted for every cache lookup the layers
    /// above make, and `Revision.ToString()` allocates one string each time. The revision moves at
    /// most four times a second (see <see cref="Revision"/>), so the kept value is the same string
    /// instance almost always.</summary>
    public string Generation
    {
        get
        {
            long revision = Revision;
            string? kept = Volatile.Read(ref _generation);
            if (kept != null && Volatile.Read(ref _generationRevision) == revision) return kept;
            string computed = revision.ToString(CultureInfo.InvariantCulture);
            Volatile.Write(ref _generationRevision, revision);
            Volatile.Write(ref _generation, computed);
            return computed;
        }
    }

    private string? _generation;
    private long _generationRevision = -1;


    public bool TryGetEntry(string rel, out VfsEntry entry)
    {
        entry = null!;
        if (rel.Length == 0)
        {
            entry = new VfsEntry { Name = string.Empty, IsDir = true };
            return true;
        }

        long revision = Revision;
        string key = revision + "|" + rel;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out entry!)) return true;
        }
        // A header is the object's own data, so ask for the path itself. Listing the parent
        // answered this one and every sibling at once, but it charged a whole directory fetch
        // per header, the memo above is memory-only so every restart paid for all of them
        // again, and an oversized directory is never memoized at all -- its children could not
        // be remembered even within one run. Nothing joins this request, so it holds a lane for
        // exactly the length of the call and gives it straight back.
        SvnEntry? info;
        using (var lane = _lanes.Enter(ReadPriority.Ambient))
            info = _svn.Info(revision, rel, RequestDiagnostics.Ambient);
        if (info == null) return false;
        entry = new VfsEntry { Name = info.Name, IsDir = info.IsDir, Size = info.Size, TimeUtc = info.TimeUtc,
            ContentVersion = info.ContentVersion.Length == 0 ? null : info.ContentVersion };
        lock (_gate)
        {
            if (_rev == revision) _entries[key] = entry;
        }
        return true;
    }

    public IReadOnlyList<VfsEntry> ListOwn(string rel) => Listing(rel, speculative: false, RequestDiagnostics.Ambient);

    /// <summary>
    /// Serves one directory listing. A speculative request comes from read-ahead: it counts
    /// as neither demand nor a reason to queue more work, which is what keeps read-ahead one
    /// level deep instead of turning a browse into a whole-tree download. It also travels in
    /// the read-ahead lane, since it runs on a worker that has no lane of its own.
    /// </summary>
    private IReadOnlyList<VfsEntry> Listing(string rel, bool speculative, RequestDiagnostics? diagnostics)
    {
        using var lane = speculative ? ReadPriority.Enter(ReadPriority.Speculative) : null;
        // WinFsp and shell probes are case-insensitive; SVN URLs are not. Resolve
        // every component to the spelling advertised by its parent listing.
        string canonical = "";
        foreach (string component in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryGetEntry(VfsPath.Child(canonical, component), out var header) || !header.IsDir)
                return Array.Empty<VfsEntry>();
            canonical = VfsPath.Child(canonical, header.Name);
        }
        rel = canonical;
        long revision = Revision;
        var generation = revision.ToString();
        var key = generation + "|" + rel;
        SharedRequest<IReadOnlyList<VfsEntry>>? pending = null;
        IReadOnlyList<VfsEntry>? memoized = null;
        lock (_gate)
        {
            if (_memo.TryGetValue(key, out memoized)) _memoAccess[key] = Environment.TickCount64;
            else if (!_listingRequests.TryGetValue(key, out pending!))
            {
                string directory = rel;
                pending = new SharedRequest<IReadOnlyList<VfsEntry>>(_lanes.Begin(ReadPriority.Ambient), () =>
                {
                    try
                    {
                        return _svn.List(revision, directory, diagnostics)
                            .Select(e => new VfsEntry { Name = e.Name, IsDir = e.IsDir, Size = e.Size, TimeUtc = e.TimeUtc,
                                ContentVersion = e.ContentVersion.Length == 0 ? null : e.ContentVersion }).ToArray();
                    }
                    catch (SvnNotFoundException) { return Array.Empty<VfsEntry>(); }
                });
                _listingRequests[key] = pending;
            }
        }
        IReadOnlyList<VfsEntry> list;
        if (memoized != null) list = memoized;
        else
        {
            try
            {
                // Same-directory callers share one request; different directories can
                // execute svn list concurrently without holding the metadata mutex. A demand
                // caller that finds read-ahead already fetching this directory rides it.
                list = pending!.Join(ReadPriority.Ambient);
                lock (_gate)
                {
                    if (_rev == revision) StoreListing(key, generation, rel, list, speculative);
                }
            }
            finally
            {
                lock (_gate)
                    if (_listingRequests.TryGetValue(key, out var current) && current == pending) _listingRequests.Remove(key);
            }
        }
        if (speculative) return list;
        // What a caller asks for is what makes the level below it worth fetching: read-ahead
        // never queues children of its own, and a listing it fetched earlier still has to
        // cascade once demand arrives — otherwise the chain stalls exactly when read-ahead
        // is ahead, which is the case it exists for.
        List<string>? children;
        lock (_gate)
        {
            if (_prefetched.Remove(key)) Interlocked.Increment(ref _prefetchHits);
            children = _cascaded.Add(key) ? PendingChildren(generation, rel, list) : null;
        }
        // Queued outside the metadata lock: a speculative fetch must not delay a caller.
        if (children != null) _prefetch!.Enqueue(revision, children, diagnostics);
        _prefetch?.DemandServed();
        return list;
    }

    public bool TryOpenRead(string rel, out VfsEntry entry, out Stream stream)
    {
        stream = null!;
        if (!TryGetEntry(rel, out entry) || entry.IsDir) return false;

        // Content is not cached here — the read cache above owns retention — but callers
        // that want the same file at the same time wait for one download instead of
        // starting a second. Read-ahead and the foreground read a resource together often
        // enough, and a whole-file download is the most expensive thing this layer does.
        string key = Revision + "|" + rel;
        SharedRequest<string> request;
        lock (_gate)
        {
            if (!_contentRequests.TryGetValue(key, out request!))
            {
                long revision = Revision;
                request = new SharedRequest<string>(_lanes.Begin(ReadPriority.Ambient), () => Download(revision, rel));
                _contentRequests[key] = request;
            }
        }
        string tmp;
        try
        {
            tmp = request.Join(ReadPriority.Ambient);
        }
        finally
        {
            lock (_gate)
                if (_contentRequests.TryGetValue(key, out var current) && ReferenceEquals(current, request)) _contentRequests.Remove(key);
        }
        stream = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            128 * 1024, FileOptions.DeleteOnClose);
        return true;
    }

    /// <summary>Fetches one file's content to a private temp file that the open handles own.</summary>
    private string Download(long revision, string rel)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "PolyMount-svn-" + Guid.NewGuid().ToString("N") + ".tmp");
        _svn.CatToFile(revision, rel, tmp, RequestDiagnostics.Ambient);
        return tmp;
    }

    public bool TryReadObject(string path, out VirtualObject value)
    {
        value = null!;
        if (!TryGetEntry(path, out var header)) return false;
        value = new(header, header.IsDir ? ListOwn(path) : null);
        return true;
    }
    public bool TryApply(IReadOnlyList<ObjectChange> changes) => false;
}
