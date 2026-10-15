namespace PolyMount.Core;

/// <summary>
/// Walks directory listings in the background after a mount, so the read cache holds them for
/// the version being mounted.
/// <para>
/// A listing is the one thing every other request needs: it carries its children's headers, so
/// one cached listing answers a whole directory's stat traffic, and it survives a restart at
/// the same version. Fetching them on demand means the first walk through a tree — an engine
/// starting, or someone opening a folder — pays a source round trip per directory, and after a
/// version change there is nothing in the cache to answer with. Doing it here moves that
/// traffic to when the mount is not being read.
/// </para>
/// <para>
/// The walk yields to the client: it steps only while nothing has read through the mount for a
/// moment, it takes one directory at a time, and it stops at its budget. A mount that is busy
/// from its first second therefore warms nothing, which is the intent.
/// </para>
/// </summary>
public sealed class ListingWarmup : IDisposable
{
    private const int QuietPollMs = 50;

    private readonly VirtualFileSystem _vfs;
    private readonly MetricsCollector _metrics;
    private readonly int _limit;
    private readonly int _quietMs;
    private readonly long _deadlineMs;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private long _warmed;

    public ListingWarmup(VirtualFileSystem vfs, MetricsCollector metrics, int limit, int quietMs = 250, int seconds = 60)
    {
        _vfs = vfs;
        _metrics = metrics;
        _limit = Math.Max(limit, 0);
        _quietMs = Math.Max(quietMs, 0);
        // A budget in directories only bounds the work if the source is fast; on a large tree
        // one pass could otherwise keep fetching for minutes. Whichever runs out first stops it.
        _deadlineMs = Environment.TickCount64 + Math.Max(seconds, 0) * 1000L;
        _worker = Task.Factory.StartNew(Run, _stop.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>Directories whose listing this pass fetched and stored.</summary>
    public long WarmedDirectories => Interlocked.Read(ref _warmed);

    /// <summary>Waits for the walk to finish, hit its budget, or be stopped.</summary>
    public bool WaitForCompletion(TimeSpan timeout) => _worker.Wait(timeout);

    private void Run()
    {
        var pending = new Queue<string>();
        pending.Enqueue("");
        while (!_stop.IsCancellationRequested && pending.Count > 0 && Interlocked.Read(ref _warmed) < _limit &&
            Environment.TickCount64 < _deadlineMs)
        {
            if (_metrics.IdleFor.TotalMilliseconds < _quietMs)
            {
                if (_stop.Token.WaitHandle.WaitOne(QuietPollMs)) return;
                continue;
            }
            string directory = pending.Dequeue();
            IReadOnlyList<VfsEntry> children;
            try
            {
                // Not a client read: it must not count as activity, and it must not steer the
                // read predictions that belong to whoever is actually using the mount.
                children = _vfs.List(directory, recordAccess: false);
            }
            catch (Exception)
            {
                // Read-ahead is best effort. A directory the source refuses is dropped rather
                // than retried, so a broken subtree cannot stall the walk.
                continue;
            }
            Interlocked.Increment(ref _warmed);
            foreach (var child in children)
                if (child.IsDir && pending.Count < _limit) pending.Enqueue(VfsPath.Child(directory, child.Name));
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        _stop.Dispose();
    }
}
