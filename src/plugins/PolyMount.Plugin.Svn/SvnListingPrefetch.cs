using System.Collections.Concurrent;
using PolyMount.Core;

namespace PolyMount.Plugins.Svn;

/// <summary>
/// Fetches the listings of a directory's child directories ahead of demand.
/// <para>
/// Work stays one level ahead of what was actually asked for: a prefetched listing never
/// queues children of its own, so browsing can not turn into a whole-tree download. The
/// queue is bounded and drops when full, the pinned revision is re-checked when an item is
/// dequeued, and repeated failures stop the read-ahead until a demand listing succeeds
/// again — speculative work must never cost the caller's own request anything.
/// </para>
/// </summary>
internal sealed class SvnListingPrefetch : IDisposable
{
    private readonly record struct Request(long Revision, string Path, RequestDiagnostics? Diagnostics);

    private const int FailureLimit = 3;

    private readonly BlockingCollection<Request> _queue;
    private readonly Task[] _workers;
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<long> _revision;
    private readonly Func<string, RequestDiagnostics?, bool> _fetch;
    private long _started, _fetched, _skipped, _stale, _failures;
    private int _outstanding, _tripped;
    // Set before anything is released so a caller that is still handing work over
    // during unmount never touches a disposed queue or cancellation source.
    private volatile bool _closed;

    public SvnListingPrefetch(int concurrency, int queueDepth, Func<long> revision,
        Func<string, RequestDiagnostics?, bool> fetch)
    {
        _queue = new BlockingCollection<Request>(queueDepth);
        _revision = revision;
        _fetch = fetch;
        _workers = new Task[concurrency];
        for (int i = 0; i < concurrency; i++)
            _workers[i] = Task.Factory.StartNew(Worker, TaskCreationOptions.LongRunning);
    }

    public long Started => Interlocked.Read(ref _started);
    public long Fetched => Interlocked.Read(ref _fetched);
    public long Skipped => Interlocked.Read(ref _skipped);
    public long Stale => Interlocked.Read(ref _stale);
    public long Failures => Interlocked.Read(ref _failures);

    /// <summary>Queues directories to fetch. Items beyond the queue depth are dropped rather
    /// than queued: read-ahead that has fallen behind is worth less than the memory. The
    /// caller's diagnostics travel with the item, since the worker has no scope of its own.</summary>
    public void Enqueue(long revision, IReadOnlyList<string> directories, RequestDiagnostics? diagnostics)
    {
        if (_closed || Volatile.Read(ref _tripped) != 0) return;
        foreach (string path in directories)
        {
            if (_closed || !_queue.TryAdd(new Request(revision, path, diagnostics))) return;
            Interlocked.Increment(ref _outstanding);
        }
    }

    /// <summary>A served demand listing clears a trip: the source is answering again.</summary>
    public void DemandServed()
    {
        Interlocked.Exchange(ref _failures, 0);
        Volatile.Write(ref _tripped, 0);
    }

    /// <summary>Waits until nothing is queued or in flight.</summary>
    public bool WaitForIdle(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Volatile.Read(ref _outstanding) != 0 && Environment.TickCount64 < deadline) Thread.Sleep(2);
        return Volatile.Read(ref _outstanding) == 0;
    }

    private void Worker()
    {
        try
        {
            foreach (var request in _queue.GetConsumingEnumerable(_stop.Token))
            {
                // Draining an abandoned queue must not cost an unmount its wait.
                if (_closed) { Interlocked.Decrement(ref _outstanding); continue; }
                try
                {
                    Interlocked.Increment(ref _started);
                    // The mount left this revision while the item waited, so the answer
                    // would be discarded on arrival; drop it instead of spending a request.
                    if (request.Revision != _revision()) Interlocked.Increment(ref _stale);
                    else if (_fetch(request.Path, request.Diagnostics))
                    {
                        Interlocked.Increment(ref _fetched);
                        Interlocked.Exchange(ref _failures, 0);
                    }
                    else Interlocked.Increment(ref _skipped);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _skipped);
                    if (Interlocked.Increment(ref _failures) >= FailureLimit) Volatile.Write(ref _tripped, 1);
                }
                finally
                {
                    Interlocked.Decrement(ref _outstanding);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _closed = true;
        _stop.Cancel();
        _queue.CompleteAdding();
        try { Task.WaitAll(_workers, TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        _queue.Dispose();
        _stop.Dispose();
    }
}
