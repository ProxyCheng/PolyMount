using System.Collections.Concurrent;

namespace PolyMount.Core.Ipc;

/// <summary>
/// Every instance as a front-end sees it: what the supervisor knows it is, and what the instance's
/// own process says about itself, put together.
///
/// The two processes answer different halves and neither has a copy of the other's. The supervisor
/// knows which instances exist, which process is running each, and why none is; the worker knows
/// whether its mount came up, what its layers are serving and what its cache holds. A window that
/// shows an instance — the dashboard's list, a recording's summary — needs both, so putting them
/// together happens here rather than in each of the windows, and it happens this way round: the
/// worker's answer is the instance's state, and the supervisor adds the process.
/// </summary>
public static class InstanceView
{
    /// <summary>The status of every instance, shaped as one response so a window can use the path
    /// it already has for asking the service once. The supervisor is asked first — it is the one
    /// that knows what exists — and then every running instance is asked about itself, all at once:
    /// one worker that will not answer must not be paid for by the others. A worker that does not
    /// answer leaves the supervisor's row in place, which has no mount state but does say the
    /// process is gone.</summary>
    public static ControlResponse Status()
    {
        ControlResponse supervisor;
        try { supervisor = SupervisorClient.Default.Status(); }
        catch (Exception ex) { return new ControlResponse { Ok = false, Error = ex.Message }; }

        var live = new ConcurrentDictionary<string, MountStatus>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(supervisor.Mounts.Where(row => row.Pipe.Length > 0), row =>
        {
            try
            {
                var status = new InstanceClient(row.Name, row.Pipe).Status();
                if (status.Mounts.FirstOrDefault(m => string.Equals(m.Name, row.Name, StringComparison.OrdinalIgnoreCase)) is { } own)
                    live[row.Name] = own;
            }
            catch (Exception)
            {
                // Nothing running there after all. The supervisor's row already says the instance
                // exists and is not mounted, which is the truth this one has to show.
            }
        });

        return new ControlResponse
        {
            Ok = supervisor.Ok,
            Error = supervisor.Error,
            Mounts = supervisor.Mounts.Select(row => live.TryGetValue(row.Name, out var own) ? Combine(row, own) : row).ToList()
        };
    }

    /// <summary>The read analytics of every instance, one entry per mount. Each instance's process
    /// reports its own window and there is no process that could report them all, so the "all
    /// instances" view is this: every one of them asked, and the answers put together.</summary>
    public static List<MountMetrics> Metrics(double windowSeconds)
    {
        var endpoints = InstanceDirectory.All();
        var collected = new ConcurrentBag<MountMetrics>();
        Parallel.ForEach(endpoints, endpoint =>
        {
            try
            {
                foreach (var metrics in new InstanceClient(endpoint.Name, endpoint.Pipe).Metrics(windowSeconds).Metrics)
                    collected.Add(metrics);
            }
            catch (Exception)
            {
                // An instance that is not answering contributes nothing rather than failing the
                // whole window: the others are still measuring something.
            }
        });
        return collected.ToList();
    }

    /// <summary>The worker's answer, plus what only the supervisor knows about the process running
    /// it: which one that is, how often it has had to be started again, and whether the instance is
    /// switched on at all.</summary>
    private static MountStatus Combine(MountStatus supervisor, MountStatus worker)
    {
        worker.Enabled = supervisor.Enabled;
        worker.Pipe = supervisor.Pipe;
        worker.WorkerPid = supervisor.WorkerPid;
        worker.Restarts = supervisor.Restarts;
        worker.Error ??= supervisor.Error;
        return worker;
    }
}
