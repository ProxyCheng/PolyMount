using PolyMount.Core.Ipc;

namespace PolyMount.Core;

/// <summary>Ordered object resolver. Directories are complete, authoritative namespace values.</summary>
public sealed class VirtualFileSystem
{
    private readonly List<ILayer> _layers;
    /// <summary>The trace name of each layer, per kind of operation, in layer order.</summary>
    private readonly string[] _getEntry, _readObject, _probe;
    // Only mutations coordinate parent snapshot updates. Provider reads never
    // take this lock, including network-backed metadata and directory queries.
    private readonly object _mutationGate = new();
    private readonly object _cacheUpdateGate = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, bool Fill, long Epoch), Lazy<(bool Found, VirtualObject Value, int Index)>> _resolutions = new(ResolutionKey.Instance);
    private long _resolutionEpoch;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SharedObjectFile> _openFiles = new(StringComparer.OrdinalIgnoreCase);
    public VirtualFileSystem(IEnumerable<ILayer> layers)
    {
        _layers = layers.ToList();
        // The names a trace line carries for a layer, built once per layer: a probe is the most
        // frequent operation there is, and `"Layer.GetEntry:" + name` at the call site allocates a
        // string for every one of them - including the ones the sampling rule drops unread.
        _getEntry = _layers.Select(layer => "Layer.GetEntry:" + layer.Name).ToArray();
        _readObject = _layers.Select(layer => "Layer.ReadObject:" + layer.Name).ToArray();
        _probe = _layers.Select(layer => "Layer.Probe:" + layer.Name).ToArray();
    }
    public IReadOnlyList<ILayer> Layers => _layers;
    /// <summary>The reparse data a name carries, from the first layer that holds one. A layer
    /// that accepts writes keeps them; the layers above and below only answer for what they
    /// have, so a link created locally is found before anything the source says about the path.
    /// </summary>
    public bool TryGetReparsePoint(string path, out byte[]? data)
    {
        path = VfsPath.Normalize(path);
        foreach (var layer in _layers)
            if (ReparsePointStore(layer) is { } store && store.TryGetReparsePoint(path, out data)) return true;
        data = null;
        return false;
    }

    public bool SetReparsePoint(string path, byte[] data)
    {
        path = VfsPath.Normalize(path);
        foreach (var layer in _layers)
            if (ReparsePointStore(layer) is { } store) return store.SetReparsePoint(path, data);
        return false;
    }

    public bool DeleteReparsePoint(string path)
    {
        path = VfsPath.Normalize(path);
        foreach (var layer in _layers)
            if (ReparsePointStore(layer) is { } store && store.DeleteReparsePoint(path)) return true;
        return false;
    }

    /// <summary>The store behind a layer. Layer chains wrap providers in a named decorator, which
    /// is not the capability itself.</summary>
    private static IReparsePointStore? ReparsePointStore(ILayer layer) =>
        (layer is NamedLayer named ? named.Inner : layer) as IReparsePointStore;

    /// <summary>The first component of <paramref name="path"/> that is a name surrogate, and the
    /// character offset where it begins. Windows resolves a link when the link itself is the name
    /// it opens, but this mount is asked about whole names: for "\link\sub\file" it is only asked
    /// whether that name exists, and answering "no" ends the search. Naming the component is how
    /// the mount tells Windows which part it should resolve.
    ///
    /// The offset is in <paramref name="path"/> as given, not in the normalized form: WinFsp
    /// re-reads the offset out of the original name to start resolution there.</summary>
    public bool TryFindReparsePoint(string path, out int index)
    {
        index = 0;
        int start = 0;
        while (start < path.Length && path[start] is '\\' or '/') start++;
        for (int i = start; i < path.Length; i++)
        {
            if (path[i] is not ('\\' or '/')) continue;
            if (TryGetReparseTag(path[..i], out uint tag) && ReparseTags.IsNameSurrogate(tag))
            {
                index = start;
                return true;
            }
            start = i + 1;
            while (start < path.Length && path[start] is '\\' or '/') start++;
        }
        return false;
    }

    /// <summary>Whether a name carries a reparse point, by tag only. This runs on every lookup
    /// that misses — a probe for a name that is not there — so it must not read the bytes: a
    /// store keeps the tags in memory and loads the data only when something asks for it.</summary>
    private bool TryGetReparseTag(string path, out uint tag)
    {
        foreach (var layer in _layers)
            if (ReparsePointStore(layer) is { } store && store.TryGetReparseTag(path, out tag)) return true;
        tag = 0;
        return false;
    }


    /// <summary>Stamps an entry with the reparse point a writable layer keeps for its name. The
    /// mount reports it as FILE_ATTRIBUTE_REPARSE_POINT, and that attribute is the only reason
    /// Windows ever asks the file system to resolve a link.</summary>
    private void MarkReparse(string path, VfsEntry entry)
    {
        if (entry.IsReparsePoint) return;
        foreach (var layer in _layers)
            if (ReparsePointStore(layer) is { } store && store.TryGetReparseTag(path, out uint tag))
            {
                entry.IsReparsePoint = true;
                entry.ReparseTag = tag;
                return;
            }
    }

    public bool Writable => _layers.Any(l => l.Writable);
    public MetricsCollector? Metrics { get; set; }
    public RequestDiagnostics? Diagnostics { get; set; }
    public ReadPrediction? Prediction { get; set; }
    /// <summary>The chain's generation, which every cache key is built from. It is a key rather
    /// than a checksum anyone reads: what matters is that it changes when what it covers changes,
    /// and the chain itself is fixed once this is built - so the only thing that can move
    /// underneath is the bottom layer's own generation, the source's revision.
    ///
    /// Kept rather than recomputed, and that is the whole point: it used to be a SHA256 of the
    /// joined chain, computed on every access, and it is on the path of every content open, every
    /// probe and every cache lookup. The sampling profiler put it at 3% of the process's samples
    /// with the mount otherwise idle. The key is the bottom layer's generation, which a source
    /// memoises in turn, so the common case is a reference comparison.
    ///
    /// Two threads that race here compute the same string, so no lock is needed: the last writer
    /// wins and both callers get the answer that was asked for.</summary>
    public string Generation
    {
        get
        {
            string key = _layers[^1].Generation;
            // Key and hash are published together: two fields written one after the other let a
            // reader pair the new key with the previous hash and hand out the old generation.
            var kept = Volatile.Read(ref _generation);
            if (kept != null && (ReferenceEquals(key, kept.Key) || string.Equals(key, kept.Key, StringComparison.Ordinal)))
                return kept.Hash;
            string computed = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(string.Join("|", _layers.Select(l => l.Name + ":" + l.Generation)))));
            Volatile.Write(ref _generation, new GenerationStamp(key, computed));
            return computed;
        }
    }

    private sealed record GenerationStamp(string Key, string Hash);
    /// <summary>The generation last handed out, with the bottom layer's generation it was computed from.</summary>
    private GenerationStamp? _generation;

    /// <summary>Compares resolution keys the way the mount does: paths case-insensitively.
    /// This is a comparer rather than upper-casing the path at the call site because that
    /// allocated a string on every resolution — every layer lookup and every ancestor of a
    /// listing builds one of these keys, hit or miss.</summary>
    private sealed class ResolutionKey : IEqualityComparer<(string Path, bool Fill, long Epoch)>
    {
        public static readonly ResolutionKey Instance = new();
        public bool Equals((string Path, bool Fill, long Epoch) left, (string Path, bool Fill, long Epoch) right) =>
            left.Fill == right.Fill && left.Epoch == right.Epoch &&
            string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Path, bool Fill, long Epoch) key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Path), key.Fill, key.Epoch);
    }
    private bool Resolve(string path, out VirtualObject value, out int index, bool fill = true)
    {
        var key = (path, fill, System.Threading.Interlocked.Read(ref _resolutionEpoch));
        // A hit needs neither the closure nor the Lazy the factory builds: it captures the
        // path and allocates two objects per resolution, which is a listing's worth of
        // garbage for answers that were already in the dictionary. GetOrAdd stays the
        // arbiter on a miss, so two callers racing still evaluate exactly one resolution.
        if (!_resolutions.TryGetValue(key, out var pending))
        {
            pending = _resolutions.GetOrAdd(key, _ => new(() =>
            {
                bool found = ResolveOwn(path, out var resolved, out int hit, fill);
                return (found, resolved, hit);
            }, LazyThreadSafetyMode.ExecutionAndPublication));
        }
        try
        {
            var result = pending.Value;
            value = result.Value; index = result.Index;
            return result.Found;
        }
        finally
        {
            ((ICollection<KeyValuePair<(string Path, bool Fill, long Epoch), Lazy<(bool Found, VirtualObject Value, int Index)>>>)_resolutions)
                .Remove(new(key, pending));
        }
    }
    /// <summary>What a fill has to still be true when it is published: no write landed and the
    /// source did not move since the read began. Two reads, no allocation.</summary>
    private readonly record struct FillToken(long Epoch, string? Generation);
    private FillToken BeginFill() => new(Interlocked.Read(ref _resolutionEpoch), _layers.Count == 0 ? null : _layers[^1].Generation);
    private bool StillCurrent(FillToken token)
    {
        if (token.Epoch != Interlocked.Read(ref _resolutionEpoch)) return false;
        if (_layers.Count == 0) return true;
        string? now = _layers[^1].Generation;
        return ReferenceEquals(now, token.Generation) || string.Equals(now, token.Generation, StringComparison.Ordinal);
    }

    private bool ResolveOwn(string path, out VirtualObject value, out int index, bool fill)
    {
        var token = BeginFill();
        for (index = 0; index < _layers.Count; index++)
        {
            using var request = RequestDiagnostics.Begin(_readObject[index], path);
            if (!((IObjectLayer)_layers[index]).TryReadObject(path, out value!)) continue;
            TouchBelow(path, index);
            if (fill && value.Header.IsDir)
                lock (_cacheUpdateGate)
                    if (StillCurrent(token)) Fill(path, value, null, index);
            return true;
        }
        value = null!;
        if (path.Length == 0) { value = VirtualObject.Directory("", Array.Empty<VfsEntry>()); return true; }
        return false;
    }

    // Layers resolve top down and the first hit wins, so a cache that keeps answering a
    // path hides it from every cache underneath. Those entries would never record a read
    // and a recency-ordered eviction would then discard the objects still in use, which
    // is most of the damage for directories: losing one costs every path below it.
    private void TouchBelow(string path, int hit)
    {
        for (int i = hit + 1; i < _layers.Count; i++)
            if (_layers[i] is IObjectCache cache) cache.TouchObject(path);
    }

    private bool Visible(string path, bool fill = true) => Visible(path, out _, out _, fill);

    /// <summary>Walks the ancestors as the resolver does and, when the path is visible,
    /// hands back the listing that contains its last component and the layer that answered
    /// it. That listing already holds the child's header — the resolver would otherwise ask
    /// every layer for the same facts by exact path, and the source would repeat on the next
    /// request what it just said in the parent's listing.</summary>
    private bool Visible(string path, out DirectoryObjectContent? parent, out int parentIndex, bool fill = true)
    {
        parent = null; parentIndex = -1;
        if (path.Length == 0) return true;
        int start = 0;
        string current = "";
        while (true)
        {
            int slash = path.IndexOf('/', start);
            int end = slash < 0 ? path.Length : slash;
            if (!Resolve(current, out var directory, out var directoryIndex, fill) || !directory.Header.IsDir ||
                directory.Content is not DirectoryObjectContent members ||
                !members.Contains(path.AsSpan(start, end - start))) return false;
            if (slash < 0) { parent = members; parentIndex = directoryIndex; return true; }
            // The prefix is the next lookup's key, so it has to exist; the component itself
            // does not, and Split used to build every one of them plus an array.
            current = path[..end];
            start = slash + 1;
        }
    }
    // Attribute queries must not read a directory's children. Remote providers can
    // obtain the header from its parent's already resolved listing.
    private bool ResolveHeader(string path, out VfsEntry entry, out int index, int? limit = null,
        DirectoryObjectContent? parent = null, int parentIndex = -1)
    {
        // The authoritative listing is the one the visibility walk already resolved, and the
        // member table is case-insensitive: NTFS semantics, which is what the mount advertises
        // (MountInstance sets CaseSensitiveSearch=false). Take the header from there, and ask
        // only the layers above that listing for an override — a copy-up, a renamed entry.
        // `limit` is the enumeration path, which is already holding the listing and only wants
        // those overrides, so it keeps the exact-path walk.
        if (limit == null && parentIndex >= 0 && parent != null &&
            parent.TryGetEntry(VfsPath.Name(path), out var listed))
        {
            for (index = 0; index < parentIndex; index++)
            {
                using var request = RequestDiagnostics.Begin(_getEntry[index], path);
                if (!_layers[index].TryGetEntry(path, out entry!)) continue;
                TouchBelow(path, index);
                MarkReparse(path, entry);
                return true;
            }
            entry = listed;
            index = parentIndex;
            MarkReparse(path, entry);
            return true;
        }
        for (index = 0; index < (limit ?? _layers.Count); index++)
        {
            using var request = RequestDiagnostics.Begin(_getEntry[index], path);
            if (!_layers[index].TryGetEntry(path, out entry!)) continue;
            TouchBelow(path, index);
            MarkReparse(path, entry);
            return true;
        }
        entry = null!;
        if (path.Length == 0) { entry = new VfsEntry { Name = "", IsDir = true }; return true; }
        return false;
    }
    /// <summary>Resolves a file or a folder for whoever asked. <paramref name="recordAccess"/> is
    /// set by the mount adapter, where this is the request being served: listing and opening were
    /// counted, so an attribute query that reaches the source was the one resolution whose wait
    /// nothing reported. The VFS's own consistency checks during a mutation call the default and
    /// are not reads.</summary>
    public bool TryLookup(string path, out VfsEntry entry, bool recordAccess = false)
    {
        using var request = RequestDiagnostics.Begin("VFS.Lookup", path, Diagnostics);
        path = VfsPath.Normalize(path);
        if (_openFiles.TryGetValue(path, out var open) && open.TryGetHeader(VfsPath.Name(path), out entry))
        {
            if (recordAccess) Metrics?.RecordRead(open.LayerName, TimeSpan.Zero, entry.Size);
            return true;
        }
        request?.Stage("resolving metadata");
        entry = null!;
        // A lookup no layer answered has no layer to charge, and is left out.
        int index = _layers.Count;
        var token = BeginFill();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool found = Visible(path, out var parent, out int parentIndex) &&
                     ResolveHeader(path, out entry, out index, parent: parent, parentIndex: parentIndex);
        // Counted exactly where a layer would have been charged, so what is left over when the two
        // are put together is the time in requests this account covers but nothing answered for —
        // not the traffic it never claimed to cover.
        if (recordAccess) Metrics?.RecordRequest(watch.Elapsed);
        if (recordAccess && index < _layers.Count)
            Metrics?.RecordRead(_layers[index].Name, watch.Elapsed, 0, _layers[index] is ICacheLayer);
        if (found && index < _layers.Count)
            lock (_cacheUpdateGate)
                // A write that landed while this was resolving already told the caches what
                // changed; publishing the older answer over it would undo that.
                if (StillCurrent(token)) FillHeader(path, entry, index);
        return found;
    }
    /// <summary>Read either payload kind through the same object-loading boundary.
    /// <paramref name="speculative"/> marks every layer the load reaches as read-ahead, so a
    /// source that serves both lanes can schedule them apart.</summary>
    public VirtualObject LoadObject(string path, bool speculative = false, bool fill = true)
    {
        using var lane = speculative ? ReadPriority.Enter(ReadPriority.Speculative) : null;
        if (!TryLookup(path, out var header)) throw new VfsNotFoundException(path);
        if (header.IsDir) return new VirtualObject(header, List(path, recordAccess: !speculative, speculative: speculative, fill: fill));
        using var stream = OpenRead(path, out header, speculative);
        return VirtualObject.WithContent(header, new FileObjectContent(header.Size,
            () => OpenRead(path, out _, speculative: true)));
    }
    /// <summary>One directory's listing. <paramref name="fill"/> is what a caller that will keep
    /// the answer elsewhere turns off: the warm-up stores listings in its own layer, and letting
    /// the caches below it keep a second copy would spend their budgets on records nothing reads
    /// from them.</summary>
    public List<VfsEntry> List(string path, bool recordAccess = true, bool speculative = false, bool fill = true)
    {
        using var lane = speculative ? ReadPriority.Enter(ReadPriority.Speculative) : null;
        using var predictionDemand = recordAccess ? ObservePredictions(VfsPath.Normalize(path), listing: true) : null;
        using var request = RequestDiagnostics.Begin("VFS.List", path, Diagnostics);
        request?.Stage("resolving directory");
        path = VfsPath.Normalize(path);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (!Visible(path, fill: fill) || !Resolve(path, out var value, out var index, fill) || !value.Header.IsDir)
        {
            if (recordAccess) Metrics?.RecordRequest(watch.Elapsed);
            return new();
        }
        // Provider objects are immutable directory snapshots. Copy before merging
        // upper child headers so unrelated reads and mutations can proceed.
        // The authoritative listing already contains its provider's child headers.
        // Only higher-priority layers can override them; never query lower sources
        // for thousands of children of a cached directory.
        var items = value.Children!.Select(child => ResolveHeader(VfsPath.Child(path, child.Name), out var header, out _, index)
            ? header : child).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        // The listing's own headers came from a layer that does not keep reparse points; the
        // writable one does, and every entry the mount hands out carries the attribute.
        foreach (var item in items) MarkReparse(VfsPath.Child(path, item.Name), item);
        if (recordAccess && index < _layers.Count) Metrics?.RecordRead(_layers[index].Name, watch.Elapsed, 0, _layers[index] is ICacheLayer);
        if (recordAccess) Metrics?.RecordRequest(watch.Elapsed);
        return items;
    }
    public Stream OpenRead(string path, out VfsEntry entry, bool speculative = false)
    {
            using var lane = speculative ? ReadPriority.Enter(ReadPriority.Speculative) : null;
            using var demand = speculative ? null : Prediction?.Demand(VfsPath.Normalize(path), RequestDiagnostics.AmbientStreamId);
            using var layerDemand = speculative ? null : ObservePredictions(VfsPath.Normalize(path));
            using var request = RequestDiagnostics.Begin("VFS.OpenRead", path, Diagnostics);
            path = VfsPath.Normalize(path);
            if (_openFiles.TryGetValue(path, out var open) && open.TryAcquire(false, out var handle))
            {
                entry = new VfsEntry { Name = VfsPath.Name(path), Size = handle.Length, TimeUtc = open.LastWriteTimeUtc };
                Metrics?.RecordRead(open.LayerName, TimeSpan.Zero, entry.Size);
                Metrics?.RecordRequest(TimeSpan.Zero);
                return handle;
            }
            if (!Visible(path, out var parent, out int parentIndex))
            {
                Metrics?.RecordRequest(TimeSpan.Zero);
                throw new VfsNotFoundException(path);
            }
            VfsEntry? contentHeader = null;
            if (ResolveHeader(path, out var visibleHeader, out _, parent: parent, parentIndex: parentIndex))
            {
                if (visibleHeader.IsDir)
                {
                    Metrics?.RecordRequest(TimeSpan.Zero);
                    throw new IOException("Path is a directory");
                }
                contentHeader = visibleHeader;
            }
            var token = BeginFill();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < _layers.Count; i++)
            {
                var layer = _layers[i];
                // Named only when there is a scope to name it on: `request?.Stage("..." + name)`
                // builds the string whatever request is, which is the cost this avoids.
                if (request != null) request.Stage("opening content: " + layer.Name);
                bool opened = layer.TryOpenRead(path, out entry!, out var stream);
                // The namespace key may have moved on while the bytes did not. A layer that can
                // open by the source's own content version gets the header the resolver just
                // read and answers from content it already holds; one that cannot says so only
                // by not implementing the interface, so no layer here assumes a backend.
                if (!opened && contentHeader is { IsDir: false, ContentVersion: { Length: > 0 } } candidate &&
                    layer is IContentVersionCache versioned)
                    opened = versioned.TryOpenVersioned(path, candidate, out entry!, out stream);
                if (!opened) continue;
                TouchBelow(path, i);
                Metrics?.RecordRead(layer.Name, watch.Elapsed, entry.Size, layer is ICacheLayer);
                // The request the layer was just charged for, with the same elapsed: the two counts
                // have to be about the same resolutions, or their difference means nothing.
                Metrics?.RecordRequest(watch.Elapsed);
                try
                {
                    // Content is copied outside the gate - holding it across a large file's copy
                    // would stall every other fill and every write's invalidation behind it. The
                    // check comes after the copy instead: a write or a source move that happened
                    // since this read began means the bytes just published may predate it, and
                    // they are taken back out rather than served until the next generation.
                    Fill(path, new(entry), stream, i);
                    if (i > 0)
                        lock (_cacheUpdateGate)
                            if (!StillCurrent(token)) Unfill(path, i);
                    return WrapContent(path, stream, i, false, ContentIdentity(entry));
                }
                catch { stream.Dispose(); throw; }
            }
            throw new VfsNotFoundException(path);
    }
    /// <summary>
    /// What a block cache keys its pages by: which bytes are visible, not which provider
    /// served them. An open moves between providers as the caches above the source fill up,
    /// and a key naming the provider would strand the pages one of them captured for the
    /// other. The origin layer's generation covers the data underneath moving; the object's
    /// own size and timestamp cover the object itself changing.
    /// </summary>
    private string ContentIdentity(VfsEntry entry) =>
        entry.Size + ":" + entry.TimeUtc.Ticks + ":" +
        // A source-declared version identifies the bytes themselves, so pages survive the
        // namespace moving on. Without one, the origin generation is the only identity there
        // is. The prefix keeps the two namespaces from ever colliding on the same string.
        (entry.ContentVersion is { Length: > 0 } version ? "c:" + version : "g:" + _layers[^1].Generation);
    private Stream WrapContent(string path, Stream stream, int providerIndex, bool writable, string version)
    {
        for (int i = providerIndex - 1; i >= 0; i--)
        {
            var inner = _layers[i] is NamedLayer named ? named.Inner : _layers[i];
            if (inner is IFileStreamLayer accelerator) stream = accelerator.WrapStream(path, version, stream, writable);
        }
        return stream;
    }
    /// <summary>Takes back what <see cref="Fill"/> just published for one path, because the read
    /// that produced it raced a write or a source move. Rare by construction; the caches
    /// treat it as the deletion of that path, which is the conservative reading.</summary>
    private void Unfill(string path, int hit)
    {
        var removal = new[] { new ObjectChange(path, null) };
        for (int i = hit - 1; i >= 0; i--)
        {
            var inner = _layers[i] is NamedLayer named ? named.Inner : _layers[i];
            if (inner is IWriteAwareCache tracking) tracking.UpdateAfterWrite(removal);
            else if (inner is IWriteInvalidation invalidating) invalidating.InvalidateAfterWrite(removal);
            else if (inner is ICacheLayer cache) cache.Invalidate();
        }
    }
    private void Fill(string path, VirtualObject value, Stream? content, int hit)
    {
        // A listing belongs to the chain's namespace store when it has one. Handing the same
        // listing to the caches below it would put a copy under the rule that never evicts a
        // directory, which is capacity the content caches can never reclaim.
        bool directory = value.Header.IsDir;
        for (int i = hit - 1; i >= 0; i--)
        {
            if (_layers[i] is not IObjectCache cache || !AcceptsFill(i, hit)) continue;
            if (directory && NamespaceStoreAbove(i)) continue;
            long position = content?.Position ?? 0;
            try { cache.StoreObject(path, value, content); }
            finally { if (content != null) content.Position = position; }
        }
    }
    /// <summary>Whether a namespace store sits above the layer at <paramref name="index"/>.
    /// The store keeps the chain's listings; a cache beneath it is for content, and a listing
    /// it caught would be one more record its capacity could never take back.</summary>
    private bool NamespaceStoreAbove(int index)
    {
        for (int i = 0; i < index; i++)
            if ((_layers[i] is NamedLayer named ? named.Inner : _layers[i]) is INamespaceStore) return true;
        return false;
    }
    /// <summary>
    /// Remembers, above the layer that answered, that this path exists and what it looks
    /// like. Resolving a path by name is the one read that has no other way to be answered
    /// from memory: the parent's listing holds the same header, but only for whoever
    /// enumerates it, so a repeated open of one file used to walk the whole chain down to
    /// the provider every time. Enumeration deliberately does not do this: writing a record
    /// per listed child is the growth this avoids.
    /// </summary>
    private void FillHeader(string path, VfsEntry entry, int hit)
    {
        VirtualObject? record = null;
        for (int i = hit - 1; i >= 0; i--)
        {
            if (_layers[i] is not IObjectCache cache || !AcceptsFill(i, hit)) continue;
            cache.StoreObject(path, record ??= VirtualObject.HeaderOnly(entry));
        }
    }
    /// <summary>
    /// Whether the layer at <paramref name="index"/> may keep a copy of something the layer
    /// at <paramref name="hit"/> answered. A writable layer between them owns the path from
    /// now on, so a copy underneath it would only be shadowed — and a layer below the answer
    /// must never receive a record the layer above would shadow forever.
    /// </summary>
    private bool AcceptsFill(int index, int hit)
    {
        var inner = _layers[index] is NamedLayer wrapper ? wrapper.Inner : _layers[index];
        // Durable writes and read-through caches expose separate capabilities.
        if (inner is IWriteAwareCache) return true;
        // Only the layers strictly between the cache and the answer: the answering layer being
        // writable is what the write-invalidation path already covers.
        return !_layers.Skip(index + 1).Take(hit - index - 1).Any(l =>
            (l is NamedLayer named ? named.Inner : l) is PolyMount.Layers.LruCacheLayer lru ? lru.HasDurableWrites : l.Writable);
    }
    private IDisposable ObservePredictions(string path, bool listing = false)
    {
        // One stream key for every predictor in the chain: the caller's process and the thread
        // handling this demand. The key is a runtime hint only; the learned table is keyed by
        // paths, so it survives threads and runs changing underneath it.
        long streamId = RequestDiagnostics.AmbientStreamId;
        var scopes = _layers.Select(l => l is NamedLayer named ? named.Inner : l)
            .OfType<PolyMount.Layers.PredictionLayerBase>().Select(l => l.Observe(path, streamId, listing)).ToArray();
        return new PredictionScopes(scopes);
    }
    private sealed class PredictionScopes(IDisposable?[] scopes) : IDisposable
    { public void Dispose() { foreach (var scope in scopes) scope?.Dispose(); } }
    private List<VfsEntry> ParentChildren(string path)
    {
        string parent = VfsPath.Parent(path);
        if (!TryLookup(parent, out var header) || !header.IsDir) throw new VfsNotFoundException(parent);
        return List(parent, recordAccess: false);
    }
    private static void ReplaceChild(List<VfsEntry> children, VfsEntry child)
    {
        children.RemoveAll(c => string.Equals(c.Name, child.Name, StringComparison.OrdinalIgnoreCase));
        children.Add(child);
    }
    private ILayer Apply(IReadOnlyList<ObjectChange> changes, ILayer? selected = null)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var change in changes) if (change.Content != null) change.Content.Position = 0;
        if (selected != null)
        {
            if (!((IObjectLayer)selected).TryApply(changes)) throw new IOException("The selected write handle no longer supports this operation");
            // The commit a staging handle makes when it is flushed: the layer's own write, and the
            // one whose duration is worth reading. Charged like the applies below it, so the write
            // cards have one event per layer operation rather than one per call into the staging
            // handle — those only carry bytes, see the recordWrite callback in OpenWrite.
            Metrics?.RecordWrite(selected.Name, watch.Elapsed, 0);
            UpdateCachesAfterWrite(changes, selected);
            return selected;
        }
        foreach (var layer in _layers)
        {
            foreach (var change in changes) if (change.Content != null) change.Content.Position = 0;
            if (!((IObjectLayer)layer).TryApply(changes)) continue;
            Metrics?.RecordWrite(layer.Name, watch.Elapsed, 0);
            Metrics?.RecordRequest(watch.Elapsed);
            UpdateCachesAfterWrite(changes, layer);
            return layer;
        }
        throw new UnauthorizedAccessException("No provider accepts this write operation");
    }
    public Stream OpenWrite(string path, bool truncate, out VfsEntry entry)
    {
        lock (_mutationGate)
        {
            path = VfsPath.Normalize(path);
            if (path.Length == 0) throw new IOException("Cannot write the root as a file");
            if (_openFiles.TryGetValue(path, out var shared) && shared.TryAcquire(true, out var sharedHandle))
            {
                if (truncate) sharedHandle.SetLength(0);
                entry = new VfsEntry { Name = VfsPath.Name(path), Size = sharedHandle.Length, TimeUtc = shared.LastWriteTimeUtc };
                return sharedHandle;
            }
            var children = ParentChildren(path);
            Stream scratch = PolyMount.Layers.ObjectDatabase.Scratch();
            try
            {
                if (TryLookup(path, out var existing))
                {
                    if (existing.IsDir) throw new IOException("Path is a directory");
                    if (!truncate) { using var source = OpenRead(path, out _); source.CopyTo(scratch); }
                }
                entry = new VfsEntry { Name = VfsPath.Name(path), Size = scratch.Length, TimeUtc = DateTime.UtcNow };
                ReplaceChild(children, entry);
                var firstWritable = _layers.FirstOrDefault(layer => layer.Writable);
                var inner = firstWritable is NamedLayer named ? named.Inner : firstWritable;
                // Existing durable files already have published content and membership.
                // Opening without truncation must not rewrite the whole old blob before
                // the caller has changed a single byte. Generic providers retain Apply.
                bool reusePublished = !truncate &&
                    inner is PolyMount.Layers.OverlayLayer &&
                    firstWritable!.TryGetEntry(path, out var published) && !published.IsDir;
                var target = reusePublished ? firstWritable! : Apply(new ObjectChange[]
                    { new(path, new(entry), scratch), new(VfsPath.Parent(path), VirtualObject.Directory(VfsPath.Parent(path), children)) });
                scratch.Position = 0;
                scratch = WrapContent(path, scratch, _layers.IndexOf(target), true, "staging");
                SharedObjectFile file = null!;
                file = new SharedObjectFile(path, scratch, target.Name, bytes => Metrics?.RecordWriteBytes(target.Name, bytes), snapshot =>
                {
                    lock (_mutationGate)
                    {
                        // Deletion and replacement detach under this same lock, so a commit that
                        // was already on its way cannot put a removed name back into its parent.
                        // A rename moves the file instead, so the path is read here, not captured.
                        if (file.Detached) return;
                        string current = file.Path;
                        var header = new VfsEntry { Name = VfsPath.Name(current), Size = snapshot.Length, TimeUtc = DateTime.UtcNow };
                        // Parent changes are merged at each commit; concurrent siblings are retained.
                        var list = ParentChildren(current);
                        ReplaceChild(list, header);
                        Apply(new ObjectChange[] { new(current, new(header), snapshot), new(VfsPath.Parent(current), VirtualObject.Directory(VfsPath.Parent(current), list)) }, target);
                    }
                }, () => ((ICollection<KeyValuePair<string, SharedObjectFile>>)_openFiles).Remove(new(file.Path, file)));
                _openFiles[path] = file;
                file.TryAcquire(true, out var firstHandle);
                return firstHandle;
            }
            catch { scratch.Dispose(); throw; }
        }
    }
    public void CreateDirectory(string path)
    {
        lock (_mutationGate)
        {
            path = VfsPath.Normalize(path);
            if (TryLookup(path, out _)) throw new IOException("Path already exists");
            var children = ParentChildren(path);
            var value = VirtualObject.Directory(path, Array.Empty<VfsEntry>());
            ReplaceChild(children, value.Header);
            Apply(new ObjectChange[] { new(path, value), new(VfsPath.Parent(path), VirtualObject.Directory(VfsPath.Parent(path), children)) });
        }
    }
    private static bool SameOrBelow(string candidate, string root) =>
        candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        (candidate.Length > root.Length && candidate[root.Length] == '/' && candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    /// <summary>Detaches the open files at or below <paramref name="root"/>: what they still hold
    /// belongs to a name that no longer exists. Called under the mutation gate, which is the lock
    /// their commit checks the flag under.</summary>
    private void DetachOpen(string root)
    {
        foreach (var key in _openFiles.Keys.Where(p => SameOrBelow(p, root)))
            if (_openFiles.TryRemove(key, out var opened)) opened.Detach();
    }
    public void Delete(string path)
    {
        lock (_mutationGate)
        {
            path = VfsPath.Normalize(path);
            if (path.Length == 0) throw new UnauthorizedAccessException("Cannot delete the root");
            if (!TryLookup(path, out _)) throw new VfsNotFoundException(path);
            // Deleting a directory here takes its subtree with it (it leaves the parent's
            // listing, so nothing below is reachable). The mount refuses a non-empty one
            // before it gets this far, through CheckDelete, which is Windows' rule.
            DetachOpen(path);
            var children = ParentChildren(path);
            children.RemoveAll(c => string.Equals(c.Name, VfsPath.Name(path), StringComparison.OrdinalIgnoreCase));
            Apply(new ObjectChange[] { new(path, null), new(VfsPath.Parent(path), VirtualObject.Directory(VfsPath.Parent(path), children)) });
        }
    }
    /// <summary>Whether <paramref name="path"/> could be deleted now. The mount asks this when
    /// a caller sets delete-on-close, so the refusal reaches the caller instead of the close.</summary>
    public void CheckDelete(string path)
    {
        path = VfsPath.Normalize(path);
        if (path.Length == 0) throw new UnauthorizedAccessException("Cannot delete the root");
        if (!Writable) throw new UnauthorizedAccessException("No provider accepts this write operation");
        if (!TryLookup(path, out var existing)) throw new VfsNotFoundException(path);
        // A reparse point is a name, not the directory it points at: Windows deletes the link
        // without touching the target, so its target's contents must not make it "not empty".
        if (existing.IsDir && !existing.IsReparsePoint && List(path, recordAccess: false).Count > 0)
            throw new VfsDirectoryNotEmptyException(path);
    }
    public void Rename(string path, string destination, bool replace)
    {
        var streams = new List<Stream>();
        try
        {
            lock (_mutationGate) RenameLocked(path, destination, replace, streams);
        }
        finally
        {
            // Released outside the gate: releasing a handle can flush, and a flush takes the
            // file's commit lock before this gate - in the other order that is a deadlock.
            foreach (var stream in streams) stream.Dispose();
        }
    }
    private void RenameLocked(string path, string destination, bool replace, List<Stream> streams)
    {
        path = VfsPath.Normalize(path); destination = VfsPath.Normalize(destination);
        if (path.Length == 0 || destination.Length == 0 ||
            destination.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase) ||
            // Onto one of its own ancestors: the copy would replace the listing that holds it.
            path.StartsWith(destination + "/", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid rename destination");
        if (string.Equals(path, destination, StringComparison.Ordinal)) return;
        if (!TryLookup(path, out var source)) throw new VfsNotFoundException(path);
        bool caseOnly = string.Equals(path, destination, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && TryLookup(destination, out var existing))
        {
            if (!replace) throw new VfsNameCollisionException(destination);
            // Windows never replaces a directory by rename, and replacing one here would orphan
            // everything stored beneath it.
            if (existing.IsDir) throw new UnauthorizedAccessException("Cannot replace a directory");
            if (source.IsDir) throw new UnauthorizedAccessException("Cannot replace a file with a directory");
        }
        var changes = new List<ObjectChange>();
        CopyTree(path, destination, changes, streams);
        // What was open at the destination belonged to the name being replaced.
        if (!caseOnly) DetachOpen(destination);
        // What is open under the source moves with it: later writes through those handles
        // belong to the new name, not to a name that no longer exists.
        foreach (var key in _openFiles.Keys.Where(p => SameOrBelow(p, path)).ToList())
        {
            if (!_openFiles.TryRemove(key, out var opened)) continue;
            string moved = destination + key[path.Length..];
            opened.Path = moved;
            _openFiles[moved] = opened;
        }
        string fromParent = VfsPath.Parent(path), toParent = VfsPath.Parent(destination);
        var from = ParentChildren(path);
        var to = string.Equals(fromParent, toParent, StringComparison.OrdinalIgnoreCase) ? from : ParentChildren(destination);
        from.RemoveAll(c => string.Equals(c.Name, VfsPath.Name(path), StringComparison.OrdinalIgnoreCase));
        ReplaceChild(to, changes[0].Object!.Header);
        // Case-only rename is the same KV key; removing it would delete the new value.
        if (!caseOnly) changes.Add(new(path, null));
        changes.Add(new(fromParent, VirtualObject.Directory(fromParent, from)));
        if (!ReferenceEquals(from, to)) changes.Add(new(toParent, VirtualObject.Directory(toParent, to)));
        Apply(changes);
    }
    private void CopyTree(string source, string destination, List<ObjectChange> changes, List<Stream> streams)
    {
        if (!TryLookup(source, out var header)) throw new VfsNotFoundException(source);
        var renamed = new VfsEntry { Name = VfsPath.Name(destination), IsDir = header.IsDir, Size = header.Size, TimeUtc = header.TimeUtc };
        if (!header.IsDir)
        {
            var stream = OpenRead(source, out _); streams.Add(stream); changes.Add(new(destination, new(renamed), stream)); return;
        }
        var children = List(source);
        changes.Add(new(destination, new(renamed, children)));
        foreach (var child in children) CopyTree(VfsPath.Child(source, child.Name), VfsPath.Child(destination, child.Name), changes, streams);
    }
    /// <summary>
    /// After <paramref name="owner"/> forgot its record of <paramref name="path"/>, makes its
    /// listing of the parent agree: the name stays with the header of the first layer below that
    /// still has it, and leaves the listing when none does. Without this a released local file
    /// stays listed as a name nothing can open, and a released edit keeps listing the edited size.
    /// Only a layer that holds the parent's listing has anything to reconcile.
    /// </summary>
    public void ReconcileAfterRelease(string path, string ownerName)
    {
        lock (_mutationGate)
        {
            path = VfsPath.Normalize(path);
            if (path.Length == 0) return;
            int at = _layers.FindIndex(l => string.Equals(l.Name, ownerName, StringComparison.OrdinalIgnoreCase));
            if (at < 0) return;
            var owner = _layers[at];
            string parent = VfsPath.Parent(path), name = VfsPath.Name(path);
            if (!((IObjectLayer)owner).TryReadObject(parent, out var directory) || !directory.Header.IsDir || directory.Children == null) return;
            VfsEntry? below = null;
            for (int i = at + 1; i < _layers.Count && below == null; i++)
                if (_layers[i].TryGetEntry(path, out var found)) below = found;
            var children = directory.Children.ToList();
            children.RemoveAll(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (below != null)
                children.Add(new VfsEntry { Name = name, IsDir = below.IsDir, Size = below.Size, TimeUtc = below.TimeUtc, ContentVersion = below.ContentVersion });
            var changes = new[] { new ObjectChange(parent, VirtualObject.Directory(parent, children)) };
            if (!((IObjectLayer)owner).TryApply(changes)) return;
            UpdateCachesAfterWrite(changes, owner);
        }
    }
    public void InvalidateCaches()
    {
        lock (_cacheUpdateGate)
        {
            Interlocked.Increment(ref _resolutionEpoch);
            foreach (var cache in _layers.OfType<ICacheLayer>()) cache.Invalidate();
        }
    }
    private void UpdateCachesAfterWrite(IReadOnlyList<ObjectChange> changes, ILayer target)
    {
        lock (_cacheUpdateGate)
        {
            Interlocked.Increment(ref _resolutionEpoch);
            int hit = _layers.IndexOf(target);
            // Only layers above the write target can hold stale data: they are resolved
            // first, so a write shadows whatever they cached. A cache below the target
            // holds the lower layers' own view, which the write does not change, and
            // invalidating it would throw away the entire read cache on every save.
            for (int i = 0; i < hit; i++)
            {
                var layer = _layers[i];
                var inner = layer is NamedLayer named ? named.Inner : layer;
                if (inner is IWriteAwareCache tracking) { tracking.UpdateAfterWrite(changes); continue; }
                // Read-ahead above a writable layer holds copies that the write just made stale;
                // it never accepts the write itself, so it only has to drop those paths.
                if (inner is IWriteInvalidation invalidating) { invalidating.InvalidateAfterWrite(changes); continue; }
                if (inner is ICacheLayer cache) cache.Invalidate();
            }
        }
    }
    public List<LayerProbe> Probe(string path)
    {
        using var request = RequestDiagnostics.Begin("VFS.Probe", path, Diagnostics);
        request?.Stage("resolving origins");
        path = VfsPath.Normalize(path);
        // Source inspection must not populate caches or change the origin it observes - nor
        // count as a read: a peek is not a hit, and it must not keep an entry warm.
        bool visible = Visible(path, false);
        var answers = new (bool Exists, VfsEntry? Header)[_layers.Count];
        int effective = _layers.Count;
        for (int index = 0; index < _layers.Count; index++)
        {
            using var layerRequest = RequestDiagnostics.Begin(_probe[index], path);
            var layer = _layers[index];
            bool exists = layer is IQuietProbe quiet ? quiet.TryPeekEntry(path, out var header) : layer.TryGetEntry(path, out header);
            answers[index] = (exists, exists ? header : null);
            if (exists && effective == _layers.Count) effective = index;
        }
        return _layers.Select((layer, index) =>
        {
            var (exists, header) = answers[index];
            return new LayerProbe { Name = layer.Name, Writable = layer.Writable, Exists = exists, IsDir = exists && header!.IsDir,
                Size = exists ? header!.Size : 0, Effective = visible && exists && index == effective,
                Shadowed = exists && (!visible || index != effective), Generation = layer.Generation };
        }).ToList();
    }
}

/// <summary>Seekable staging handle; flush persists the complete file object and parent metadata.</summary>
internal sealed class SharedObjectFile(string path, Stream data, string layerName, Action<long> recordWrite, Action<Stream> commit, Action closed,
    Func<Stream>? createSnapshot = null)
{
    private static readonly SemaphoreSlim MemorySnapshots = new(32, 32);
    private const int SmallSnapshotBytes = 64 * 1024;
    public string LayerName => layerName;
    private readonly object _gate = new();
    private readonly object _commitGate = new();
    private int _references;
    private bool _closed;
    private bool _detached;
    private string _path = path;
    private DateTime _lastWriteTimeUtc = DateTime.UtcNow;
    public DateTime LastWriteTimeUtc { get { lock (_gate) return _lastWriteTimeUtc; } }
    /// <summary>The name this file commits to. A rename moves it; set under the resolver's
    /// mutation gate, which is also where every commit reads it.</summary>
    public string Path { get { lock (_gate) return _path; } set { lock (_gate) _path = value; } }
    public void Detach() { lock (_gate) _detached = true; }
    public bool Detached { get { lock (_gate) return _detached; } }
    private long _version, _persisted;
    public bool TryAcquire(bool writable, out Stream handle)
    {
        lock (_gate) { handle = null!; if (_closed) return false; _references++; handle = new SharedHandle(this, writable); return true; }
    }
    public long Length { get { lock (_gate) return data.Length; } }
    public bool TryGetHeader(string name, out VfsEntry entry)
    {
        lock (_gate)
        {
            entry = null!;
            if (_closed) return false;
            entry = new VfsEntry { Name = name, Size = data.Length, TimeUtc = _lastWriteTimeUtc };
            return true;
        }
    }
    public int Read(long position, byte[] buffer, int offset, int count)
    { lock (_gate) { data.Position = position; return data.Read(buffer, offset, count); } }
    public void Write(long position, byte[] buffer, int offset, int count)
    { if (count == 0) return; lock (_gate) { data.Position = position; data.Write(buffer, offset, count); _version++; _lastWriteTimeUtc = DateTime.UtcNow; } recordWrite(count); }
    public void SetLength(long length) { lock (_gate) { if (data.Length == length) return; data.SetLength(length); _version++; _lastWriteTimeUtc = DateTime.UtcNow; } recordWrite(0); }
    public void Flush()
    {
        lock (_commitGate)
        {
            long version;
            lock (_gate)
            {
                if (_version == _persisted || _detached) return;
            }
            bool memorySnapshot;
            lock (_gate)
            {
                memorySnapshot = createSnapshot == null && data.Length <= SmallSnapshotBytes && MemorySnapshots.Wait(0);
            }
            Stream? snapshot = null;
            try
            {
                // Small file snapshots avoid a second temporary disk file. Admission
                // is nonblocking and globally bounded to 32 x 64 KiB of payload.
                snapshot = createSnapshot?.Invoke() ?? (memorySnapshot
                    ? new MemoryStream(SmallSnapshotBytes) : new PolyMount.Layers.CommitSnapshot());
                lock (_gate)
                {
                    version = _version;
                    if (version == _persisted || _detached) return;
                    if (memorySnapshot && data.Length > SmallSnapshotBytes)
                    {
                        snapshot.Dispose();
                        snapshot = new PolyMount.Layers.CommitSnapshot();
                        MemorySnapshots.Release(); memorySnapshot = false;
                    }
                    data.Flush();
                    long position = data.Position;
                    try { data.Position = 0; data.CopyTo(snapshot); }
                    finally { data.Position = position; }
                }
                snapshot.Position = 0;
                commit(snapshot);
                lock (_gate) _persisted = version;
            }
            finally
            {
                snapshot?.Dispose();
                if (memorySnapshot) MemorySnapshots.Release();
            }
        }
    }
    public void Release()
    {
        try { Flush(); }
        finally
        {
            bool final;
            lock (_gate)
            {
                final = --_references == 0;
                if (final) _closed = true;
            }
            if (final)
            {
                try { Flush(); }
                finally { try { data.Dispose(); } finally { closed(); } }
            }
        }
    }
}

internal sealed class SharedHandle(SharedObjectFile file, bool writable) : Stream
{
    private long _position;
    private bool _disposed;
    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => !_disposed && writable;
    public override long Length => file.Length;
    public override long Position { get => _position; set { ArgumentOutOfRangeException.ThrowIfNegative(value); _position = value; } }
    public override int Read(byte[] buffer, int offset, int count) { int read = file.Read(_position, buffer, offset, count); _position += read; return read; }
    public override long Seek(long offset, SeekOrigin origin)
    { Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, SeekOrigin.End => Length + offset, _ => throw new ArgumentException("Invalid seek origin") }; return _position; }
    public override void Write(byte[] buffer, int offset, int count)
    { if (!CanWrite) throw new UnauthorizedAccessException(); file.Write(_position, buffer, offset, count); _position += count; }
    public override void SetLength(long value) { if (!CanWrite) throw new UnauthorizedAccessException(); file.SetLength(value); }
    public override void Flush() => file.Flush();
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing) { file.Release(); _disposed = true; }
        base.Dispose(disposing);
    }
}
