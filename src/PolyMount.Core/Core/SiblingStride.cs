namespace PolyMount.Core;

/// <summary>One position in a directory walk: the sibling the reader is expected to read next,
/// and where in the directory's listing it sits, so the walk can be continued from it.</summary>
internal readonly record struct SiblingStep(string Path, string Parent, int Index);

/// <summary>
/// Stride read-ahead for a directory walk, beside the transition table that learns successors.
/// <para>
/// A loader that reads the files of one directory in order is not producing an arbitrary
/// sequence of successors: it is traversing a listing. A path-to-successor table learns that one
/// entry at a time — each sibling costs a key of its own, none of them generalises to the next,
/// and on a directory of thousands the table's whole budget is spent describing a pattern that
/// one position in the listing already describes. A CPU pairs a stride prefetcher with its
/// history-based one for the same reason.
/// </para>
/// <para>
/// So this component keeps, per directory, how far into that directory's own listing the reader
/// has got, and nominates the file after it. The listing is one the reader already resolved: it
/// is read back from a cache in the layer chain — a cache answers from what it holds, while a
/// source would answer by enumerating — and a directory whose listing is not held nominates
/// nothing rather than asking anyone for one. Nothing here lists a directory, so naming the next
/// sibling costs one lookup in what is already in memory, not one lookup per child.
/// </para>
/// <para>
/// A walk continues only while it is ascending the listing: the reader must have arrived at a
/// file from the file before it in that order, and the next position must not already have been
/// read or speculated. That single condition is what keeps a stride from firing on scattered
/// access, and the watermark behind it is what keeps an already-read sibling from being
/// nominated again. It is deliberately not a second confidence model: what a stride produces
/// is one more nomination into the same queue, bounded by the same in-flight limit and the same
/// throttle the owning layer lowers when guesses stop paying off.
/// </para>
/// </summary>
internal sealed class SiblingStride
{
    /// <summary>How many directories are followed at once. A walk is a couple of positions per
    /// directory, so this bounds memory by the number of directories in flight rather than by
    /// the number of files in them.</summary>
    private const int MaxWalks = 32;

    /// <summary>Largest listing indexed. Past this a directory is a container rather than a
    /// walk, and indexing it would be the per-child work this exists to avoid.</summary>
    private const int MaxChildren = 65536;

    private sealed class Walk(VirtualObject listing, string[] files)
    {
        /// <summary>The listing this position was taken from. A different one rebuilds the walk
        /// instead of advancing through names that no longer describe the directory.</summary>
        public readonly VirtualObject Listing = listing;
        /// <summary>Child files in the order the reader is handed them, so a sibling is named
        /// the way the reader will name it.</summary>
        public readonly string[] Files = files;
        /// <summary>Index of the last sibling demanded, -1 before this walk has a position.</summary>
        public int Last = -1;
        /// <summary>Highest index this walk has covered — read or speculated — so the next
        /// nomination is a position nobody has taken yet.</summary>
        public int Covered = -1;
        /// <summary>Recency, so the least recently walked directory is the one dropped.</summary>
        public long Used;
    }

    private readonly VirtualFileSystem _vfs;
    private readonly object _gate = new();
    private readonly Dictionary<string, Walk> _walks = new(StringComparer.OrdinalIgnoreCase);
    private long _used;

    public SiblingStride(VirtualFileSystem vfs) => _vfs = vfs;

    /// <summary>How many directories are currently being followed.</summary>
    public int TrackedDirectories { get { lock (_gate) return _walks.Count; } }

    /// <summary>The sibling after <paramref name="path"/>, when the reader is walking the
    /// directory that holds it and the next position is worth reading; null otherwise.</summary>
    public SiblingStep? Nominate(string path)
    {
        string name = VfsPath.Name(path);
        if (name.Length == 0) return null;
        string parent = VfsPath.Parent(path);
        lock (_gate)
        {
            if (WalkOf(parent) is not { } walk) return null;
            int index = Array.BinarySearch(walk.Files, name, StringComparer.OrdinalIgnoreCase);
            // A path that is not one of this directory's files — a subdirectory, or a name the
            // listing does not have — leaves no position to continue from.
            if (index < 0) { walk.Last = -1; return null; }
            bool ascending = walk.Last >= 0 && walk.Last == index - 1;
            walk.Last = index;
            walk.Covered = Math.Max(walk.Covered, index);
            int next = index + 1;
            if (!ascending || next >= walk.Files.Length || next <= walk.Covered) return null;
            return Step(parent, walk, next);
        }
    }

    /// <summary>The file after one already fetched, so the read-ahead chain can carry the walk
    /// further than the reader has got. False when the walk has no next position.</summary>
    public bool TryNext(SiblingStep step, out SiblingStep next)
    {
        next = default;
        lock (_gate)
        {
            if (WalkOf(step.Parent) is not { } walk || step.Index + 1 >= walk.Files.Length) return false;
            // The position must still describe the file the step was taken from: a listing that
            // was rebuilt underneath it has moved every index.
            if (!string.Equals(walk.Files[step.Index], VfsPath.Name(step.Path), StringComparison.OrdinalIgnoreCase)) return false;
            next = Step(step.Parent, walk, step.Index + 1);
            return true;
        }
    }

    /// <summary>Marks a position as speculated, so the same sibling is not nominated twice while
    /// the read-ahead that took it is still in flight.</summary>
    public void Note(SiblingStep step)
    {
        lock (_gate)
            if (_walks.TryGetValue(step.Parent, out var walk)) walk.Covered = Math.Max(walk.Covered, step.Index);
    }

    /// <summary>Forgets every position; the next nomination starts a fresh walk.</summary>
    public void Reset() { lock (_gate) _walks.Clear(); }

    /// <summary>A position carrying the path at <paramref name="index"/>, so marking it covered
    /// or continuing from it means the same thing at both ends of the chain.</summary>
    private static SiblingStep Step(string parent, Walk walk, int index) =>
        new(VfsPath.Child(parent, walk.Files[index]), parent, index);

    /// <summary>The walk for a directory, rebuilt when the listing it was built from is no longer
    /// the one the layers hold — a revision change, an eviction and re-read, or a different
    /// generation all arrive that way. Null when no cache holds a listing for it.</summary>
    private Walk? WalkOf(string parent)
    {
        if (HeldListing(parent) is not { } listing) { _walks.Remove(parent); return null; }
        if (_walks.TryGetValue(parent, out var walk) && ReferenceEquals(walk.Listing, listing))
        {
            walk.Used = ++_used;
            return walk;
        }
        var children = listing.Children!;
        if (children.Count > MaxChildren) { _walks.Remove(parent); return null; }
        var files = children.Where(child => !child.IsDir).Select(child => child.Name).ToArray();
        // The order the reader is handed: a listing is enumerated by name, so walking it in any
        // other order would name siblings the reader is not about to ask for.
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        walk = new Walk(listing, files) { Used = ++_used };
        _walks[parent] = walk;
        if (_walks.Count > MaxWalks) _walks.Remove(_walks.MinBy(w => w.Value.Used).Key);
        return walk;
    }

    /// <summary>
    /// The listing a layer already holds for <paramref name="parent"/>, or null when none does.
    /// Only caches are asked: a cache answers from what it holds, while a source would answer by
    /// listing the directory, and naming the next sibling must never cost one of those.
    /// </summary>
    private VirtualObject? HeldListing(string parent)
    {
        foreach (var layer in _vfs.Layers)
        {
            if (layer is not IObjectCache) continue;
            if (((IObjectLayer)layer).TryReadObject(parent, out var value) && value.Header.IsDir && value.Children != null)
                return value;
        }
        return null;
    }
}
