using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PolyMount.Core;
using PolyMount.Core.Ipc;

namespace PolyMount.Service;

/// <summary>What one instance's process is doing, as the supervisor sees it: the process-level
/// facts, and nothing else. Whether the mount came up, and what its layers are serving, is the
/// worker's own answer — the supervisor does not ask it and does not answer for it, so a status
/// call here costs no round trips at all.</summary>
public sealed record WorkerState(string Id, string Name, bool Running, int Pid, string Pipe, string StartedUtc, int Restarts, string? Error);

/// <summary>
/// The worker processes the supervisor is running: one per enabled instance, each in a job object
/// of its own, each reachable on the pipe named for the instance's id.
///
/// The set is *reconciled* rather than driven — <see cref="Apply"/> makes the running set match the
/// configuration, and a timer calls <see cref="Reconcile"/> to restart what died — so a worker that
/// crashes is replaced without any state machine to get wrong, and a configuration change touches
/// only the instance it is about. That last part is the point of the whole split: changing one
/// instance's settings must not disturb another's mount, its cache or its warm-up.
/// </summary>
public sealed class WorkerPool : IDisposable
{
    /// <summary>How long a worker may take to unmount and exit when it is asked to stop.</summary>
    private const int StopSeconds = 10;

    /// <summary>A worker that stayed up this long before dying was not in a crash loop; its next
    /// failure starts counting from scratch.</summary>
    private static readonly TimeSpan HealthyUptime = TimeSpan.FromSeconds(60);

    private sealed class Worker
    {
        public string Id = "", Name = "", Pipe = "", MountPoint = "", Signature = "";
        public Process? Process;
        public JobObject? Job;
        public string StartedUtc = "";
        public DateTime StartedAtUtc;
        public DateTime NextStartUtc = DateTime.MinValue;
        /// <summary>Asked to stop: it stays stopped however the reconciliation feels about it.</summary>
        public bool Stopping;
        public int Restarts;
        public string? Error;
    }

    private readonly List<Worker> _workers = new();
    private readonly object _gate = new();
    private readonly Timer _timer;
    private readonly string _configPath;
    private readonly string _executable;
    private readonly ILogger _logger;

    public WorkerPool(string configPath, ILogger logger)
    {
        _configPath = configPath;
        _logger = logger;
        _executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("the supervisor has no executable path to start workers from");
        _timer = new Timer(_ => Guarded(), null, 1000, 1000);
    }

    /// <summary>Where each running instance is served, for a front-end that is about to connect to
    /// one. An instance whose worker is down is not here: it is in the configuration and in the
    /// status, and a front-end asking for it is answered by whoever is listening.</summary>
    public List<InstanceEndpoint> Endpoints()
    {
        lock (_gate)
            return _workers.Where(IsRunning).Select(worker => new InstanceEndpoint
            {
                Id = worker.Id,
                Name = worker.Name,
                MountPoint = worker.MountPoint,
                Pipe = worker.Pipe,
                WorkerPid = PidOf(worker),
                StartedUtc = worker.StartedUtc
            }).ToList();
    }

    public List<WorkerState> States()
    {
        lock (_gate)
            return _workers.Select(worker => new WorkerState(worker.Id, worker.Name, IsRunning(worker),
                PidOf(worker), worker.Pipe, worker.StartedUtc, worker.Restarts, worker.Error)).ToList();
    }

    /// <summary>Brings the running set in line with the configuration: an instance that was added
    /// gets a process, one whose settings changed gets a new one, and one that was removed or
    /// switched off loses its process.</summary>
    public void Apply(AppConfig config)
    {
        string global = JsonSerializer.Serialize(new { config.WinFspBin, config.ControlPipe });
        lock (_gate)
        {
            foreach (var worker in _workers.ToList())
            {
                var profile = config.Mounts.FirstOrDefault(m => string.Equals(m.Id, worker.Id, StringComparison.OrdinalIgnoreCase));
                if (profile == null || !profile.Enabled)
                {
                    _logger.LogInformation("Stopping the worker for {Instance}: it is no longer configured", worker.Name);
                    Stop(worker);
                    _workers.Remove(worker);
                    continue;
                }

                string signature = Signature(global, profile);
                if (signature == worker.Signature) continue;
                _logger.LogInformation("Restarting the worker for {Instance}: its configuration changed", worker.Name);
                Stop(worker);
                _workers.Remove(worker);
            }

            foreach (var profile in config.Mounts.Where(m => m.Enabled))
                if (!_workers.Any(w => string.Equals(w.Id, profile.Id, StringComparison.OrdinalIgnoreCase)))
                    _workers.Add(new Worker
                    {
                        Id = profile.Id,
                        Name = profile.Name,
                        MountPoint = profile.MountPoint,
                        Pipe = InstancePipes.For(config.ControlPipe, profile.Id),
                        Signature = Signature(global, profile)
                    });
        }
        Reconcile();
    }

    /// <summary>Starts the named instance's worker, or does nothing when it is already running.
    /// This is what the "mount" command comes to.</summary>
    public void Start(string? instance)
    {
        lock (_gate)
        {
            var worker = ByName(instance);
            if (IsRunning(worker)) return;
            worker.Stopping = false;
            worker.NextStartUtc = DateTime.MinValue;
            StartWorker(worker);
        }
    }

    /// <summary>Stops the named instance's worker, and leaves it stopped: this is what the
    /// "unmount" command comes to, and a reconciliation will not undo it.</summary>
    public void Stop(string? instance)
    {
        lock (_gate) Stop(ByName(instance));
    }

    /// <summary>Stops every worker. Called on the way out of the process: a supervisor that is
    /// going away on purpose takes its mounts down in order rather than leaving them to the job.</summary>
    public void StopAll()
    {
        lock (_gate)
            foreach (var worker in _workers) Stop(worker);
    }

    private Worker ByName(string? instance)
    {
        if (string.IsNullOrWhiteSpace(instance))
            throw new InvalidOperationException("this command needs the instance it is about");
        return _workers.FirstOrDefault(w => string.Equals(w.Name, instance, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("no such mount: " + instance);
    }

    private void Guarded()
    {
        try { Reconcile(); }
        catch (Exception ex) { _logger.LogError(ex, "Reconciling workers failed"); }
    }

    /// <summary>Restarts whatever should be running and is not. A worker that keeps dying is
    /// started again behind a growing delay, so a crash loop costs one process a minute rather than
    /// one a second.</summary>
    private void Reconcile()
    {
        lock (_gate)
            foreach (var worker in _workers)
            {
                if (worker.Stopping) continue;
                if (IsRunning(worker)) continue;

                if (worker.Process != null)
                {
                    var process = worker.Process;
                    int exitCode = ExitCode(process);
                    bool healthy = DateTime.UtcNow - worker.StartedAtUtc >= HealthyUptime;
                    worker.Restarts = healthy ? 1 : worker.Restarts + 1;
                    worker.Error = "the worker exited with code " + exitCode;
                    _logger.LogWarning("The worker for {Instance} exited with code {Code}; restarting it", worker.Name, exitCode);
                    worker.Job?.Dispose();
                    worker.Job = null;
                    process.Dispose();
                    worker.Process = null;
                    worker.NextStartUtc = DateTime.UtcNow.AddSeconds(BackoffSeconds(worker.Restarts));
                }

                if (DateTime.UtcNow >= worker.NextStartUtc) StartWorker(worker);
            }
    }

    private void StartWorker(Worker worker)
    {
        worker.Job?.Dispose();
        worker.Process?.Dispose();
        try
        {
            var start = new ProcessStartInfo(_executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            // The worker logs where this process logs. It cannot work that out for itself: it is
            // started by the supervisor, not by the service control manager, so the question "am I
            // a service?" would answer no and it would try to write beside an executable that its
            // account cannot write beside.
            start.Environment["POLYMOUNT_LOG_DIR"] = ServiceLogs.Directory;
            start.ArgumentList.Add("--instance");
            start.ArgumentList.Add(worker.Id);
            start.ArgumentList.Add("--config");
            start.ArgumentList.Add(_configPath);

            var process = Process.Start(start) ?? throw new IOException("the worker process did not start");
            worker.Process = process;
            worker.Job = JobObject.Create();
            if (worker.Job == null || !worker.Job.Add(process))
            {
                // Not fatal, but it is the difference between a supervisor that crashes taking its
                // workers with it and one that leaves them running with nothing to stop them.
                worker.Job?.Dispose();
                worker.Job = null;
                _logger.LogWarning("The worker for {Instance} could not be put in a job object: it will not be " +
                    "taken down automatically if this process dies", worker.Name);
            }

            worker.StartedAtUtc = DateTime.UtcNow;
            worker.StartedUtc = ProcessStartedUtc(process);
            worker.NextStartUtc = DateTime.MinValue;
            worker.Error = null;
            _logger.LogInformation("Started the worker for {Instance} ({Id}) as pid {Pid} on {Pipe}",
                worker.Name, worker.Id, process.Id, worker.Pipe);
        }
        catch (Exception ex)
        {
            worker.Process = null;
            worker.Error = ex.Message;
            worker.Restarts++;
            worker.NextStartUtc = DateTime.UtcNow.AddSeconds(BackoffSeconds(worker.Restarts));
            _logger.LogError(ex, "Starting the worker for {Instance} failed", worker.Name);
        }
    }

    /// <summary>Asks a worker to stop, and takes it down if it will not. Asked politely first: a
    /// worker that is told to stop unmounts and closes its store, which is the difference between a
    /// cache that is there next time and one that has to be rebuilt.</summary>
    private void Stop(Worker worker)
    {
        worker.Stopping = true;
        try
        {
            if (IsRunning(worker))
            {
                try { new InstanceClient(worker.Name, worker.Pipe).Shutdown(); }
                catch (Exception ex) { _logger.LogWarning(ex, "The worker for {Instance} did not accept a stop", worker.Name); }

                if (!worker.Process!.WaitForExit(StopSeconds * 1000))
                {
                    _logger.LogWarning("The worker for {Instance} did not stop within {Seconds}s; killing it", worker.Name, StopSeconds);
                    try { worker.Process.Kill(entireProcessTree: true); } catch (Exception) { }
                    worker.Process.WaitForExit(5000);
                }
            }
        }
        finally
        {
            // Closing the job is the belt to that braces: whatever the process left behind in it
            // goes now, including anything it started itself.
            worker.Job?.Dispose();
            worker.Job = null;
            worker.Process?.Dispose();
            worker.Process = null;
            worker.StartedUtc = "";
            worker.Error = null;
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        StopAll();
    }

    private static bool IsRunning(Worker worker)
    {
        if (worker.Process == null) return false;
        try { return !worker.Process.HasExited; }
        catch (Exception) { return false; }
    }

    private static int PidOf(Worker worker)
    {
        try { return worker.Process is { } process && !process.HasExited ? process.Id : 0; }
        catch (Exception) { return 0; }
    }

    private static int ExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (Exception) { return -1; }
    }

    /// <summary>One build of an instance's configuration. Two workers are the same worker when
    /// this matches, which is what makes "restart only what changed" a comparison rather than a
    /// field-by-field walk that would grow with every setting added.</summary>
    private static string Signature(string global, MountProfile profile) => global + JsonSerializer.Serialize(profile);

    private static double BackoffSeconds(int restarts) => Math.Min(30, Math.Pow(2, Math.Min(restarts - 1, 5)));

    private static string ProcessStartedUtc(Process process)
    {
        try { return process.StartTime.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception) { return DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture); }
    }
}
