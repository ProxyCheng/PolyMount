using System.Diagnostics;
using PolyMount.Core.Ipc;

namespace PolyMount.Tray;

/// <summary>What the processes behind one window's instances cost, at one moment. A window about
/// one instance reports that instance's own process; the "all instances" view adds up every
/// instance's, so the numbers are the whole service's work and nothing else is running in them.</summary>
internal readonly record struct ProcessReading(double CpuSeconds, double Cores, long WorkingSetBytes,
    long PrivateBytes, int Threads, int Handles, int Processes)
{
    public static readonly ProcessReading None = new(0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// The process behind an instance — or, for a window showing every instance at once, all of them.
///
/// Which process serves an instance is the supervisor's to say: a worker reports what its mount is
/// doing, not which process it is. The answer only changes when the instance is restarted, so it is
/// asked at most once every few seconds and remembered in between, and reading the process itself is
/// a local call — which is what the once-a-second sample does.
///
/// Nothing here attributes a thread to anything. An instance is a process, and the operating system
/// already knows what that process spent: that is the whole reason the instances were split apart.
/// </summary>
internal sealed class InstanceProcess
{
    private readonly string? _instance;
    private readonly TimeSpan _interval;
    private DateTime _next = DateTime.MinValue;
    private int[] _pids = Array.Empty<int>();
    private double _previousCpu;
    private DateTime _previousAt;

    /// <param name="instance">The instance to follow, or null for every instance at once.</param>
    /// <param name="refreshSeconds">How long a pid is believed. A worker keeps its pipe but not its
    /// pid across a restart, and that is the only thing that moves it.</param>
    public InstanceProcess(string? instance, int refreshSeconds = 10)
    {
        _instance = instance;
        _interval = TimeSpan.FromSeconds(refreshSeconds);
    }

    /// <summary>The processes the last reading covered: 0 until one is found, which is what the
    /// window shows while the instance it is about has no process yet.</summary>
    public int Processes => _pids.Length;

    /// <summary>Reads the processes now, asking the supervisor where they are if this is the moment
    /// to ask again. CPU is reported both as the total spent since the process started — which is
    /// what a recording logs, and what two recordings can be differenced over — and as the cores it
    /// was using since the previous reading, which is what a chart of it should draw.</summary>
    public ProcessReading Read()
    {
        if (DateTime.UtcNow >= _next) Refresh();

        double cpu = 0;
        long working = 0, priv = 0;
        int threads = 0, handles = 0, found = 0;
        foreach (int pid in _pids)
        {
            if (ServiceProcessReading.For(pid) is not { } reading) continue;
            found++;
            cpu += reading.CpuSeconds;
            working += reading.WorkingSetBytes;
            priv += reading.PrivateBytes;
            threads += reading.Threads;
            handles += reading.Handles;
        }

        var now = DateTime.UtcNow;
        double cores = 0;
        // A restart starts the count again, so a total that went backwards is a new process rather
        // than a negative rate: that second has no rate to report.
        if (found > 0 && _previousAt != default && cpu >= _previousCpu && (now - _previousAt).TotalSeconds > 0)
            cores = (cpu - _previousCpu) / (now - _previousAt).TotalSeconds;
        (_previousCpu, _previousAt) = (cpu, now);

        return new ProcessReading(cpu, cores, working, priv, threads, handles, found);
    }

    /// <summary>Asks where the processes are. An instance that has none yet is asked again a second
    /// later rather than ten: a window watching an instance come up should start charting it then,
    /// not on the next timeout.</summary>
    private void Refresh()
    {
        try
        {
            _pids = _instance == null
                ? InstanceDirectory.All().Select(endpoint => endpoint.WorkerPid).Where(pid => pid > 0).ToArray()
                : SupervisorClient.Default.Status().Mounts
                    .FirstOrDefault(mount => string.Equals(mount.Name, _instance, StringComparison.OrdinalIgnoreCase)) is { WorkerPid: > 0 } row
                        ? new[] { row.WorkerPid }
                        : Array.Empty<int>();
        }
        catch (Exception)
        {
            // A service that cannot be reached leaves the pids it had: the processes themselves are
            // still readable, and a reading of a process that is still running is still true.
        }
        _next = DateTime.UtcNow + (_pids.Length == 0 ? TimeSpan.FromSeconds(1) : _interval);
    }
}
