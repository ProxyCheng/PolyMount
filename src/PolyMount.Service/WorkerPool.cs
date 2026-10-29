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
public sealed record WorkerState(string Id, string Name, bool Running, int Pid, string Pipe, string StartedUtc, int Restarts, string? Error, string? ErrorKind = null, bool Failed = false);

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

    /// <summary>How many times in a row the same startup failure ends a worker before it is left
    /// down. A configuration that is refused — a credential bound elsewhere, a layer type this
    /// installation does not have, a store it may not open — is refused again the next time, and
    /// starting a process every 30 seconds forever says so nowhere: the reason is in a file nobody
    /// opens, and a mount that keeps flickering reads as "still starting".</summary>
    private const int IdenticalFailuresBeforeGivingUp = 3;

    /// <summary>And how long the same failure has to have gone on before it counts as a condition.
    /// With the backoff above the attempts land at 0, 1, 3, 7, 15, 31 and 61 seconds, so a minute is
    /// what it takes to stop: long enough that a store a killed worker is still letting go of, or a
    /// share that is briefly unreachable, is ridden out by the retry loop the way it always was —
    /// and short enough that nobody waits six minutes for a sentence that never comes.</summary>
    private static readonly TimeSpan SameFailureWindow = TimeSpan.FromSeconds(60);

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

        /// <summary>What kind of failure the error is, when the worker said.</summary>
        public string? ErrorKind;

        /// <summary>The last failure, as something two failures can be equal by: the kind and the
        /// message. A message that changes between attempts (a port, a path, a timestamp) is a
        /// different failure each time, and is left to the retry loop.</summary>
        public string? FailureKey;

        /// <summary>How many times in a row <see cref="FailureKey"/> has ended this worker.</summary>
        public int SameFailure;

        /// <summary>When that run of identical failures started.</summary>
        public DateTime FirstFailureUtc;

        /// <summary>Gave up restarting it: the same failure has ended it
        /// <see cref="IdenticalFailuresBeforeGivingUp"/> times. A mount command, or a reload of the
        /// configuration, clears this — those are acts that say the reason may be gone.</summary>
        public bool GivenUp;
    }

    private readonly List<Worker> _workers = new();
    private readonly object _gate = new();
    private readonly Timer _timer;
    private readonly string _configPath;
    private readonly string _executable;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<string> _arguments;

    /// <param name="arguments">Extra command-line arguments every worker is started with — a
    /// development host's <c>--dev</c> and <c>--plugin</c> paths, which are facts about how this
    /// process was started rather than what the configuration says.</param>
    public WorkerPool(string configPath, ILogger logger, IReadOnlyList<string>? arguments = null)
    {
        _configPath = configPath;
        _arguments = arguments ?? Array.Empty<string>();
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
                PidOf(worker), worker.Pipe, worker.StartedUtc, worker.Restarts, worker.Error, worker.ErrorKind,
                worker.GivenUp)).ToList();
    }

    /// <summary>Brings the running set in line with the configuration: an instance that was added
    /// gets a process, one whose settings changed gets a new one, and one that was removed or
    /// switched off loses its process.</summary>
    public void Apply(AppConfig config)
    {
        // Plugins are part of what a worker is: each one loads the enabled set when it starts, and
        // a worker that started before a plugin was enabled does not know its layer types. A
        // change to the set therefore restarts every worker — the instances themselves did not
        // change, but the code they run with did.
        string global = JsonSerializer.Serialize(new
        {
            config.WinFspBin,
            config.ControlPipe,
            Plugins = config.Plugins
                .Where(plugin => plugin.Enabled)
                .Select(plugin => plugin.AssemblyPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        });
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
                if (signature == worker.Signature)
                {
                    // A configuration that was written is a statement that something changed, so a
                    // worker that gave up is given another go even when this instance's own settings
                    // are the same ones: the credential it names may have been edited, and the
                    // plugin set is part of the signature above.
                    Retry(worker);
                    continue;
                }
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
            // Asking for it is an answer to whatever it said last time: the reason may have been
            // repaired in a window this process does not watch.
            Retry(worker);
            StartWorker(worker);
        }
    }

    /// <summary>Lets every worker that gave up try again. For a change that is outside the
    /// configuration a worker is built from but is exactly what such a failure was about — a
    /// credential whose origins were edited, which the mount that names it cannot see.</summary>
    public void RetryFailed()
    {
        lock (_gate)
            foreach (var worker in _workers.Where(worker => worker.GivenUp))
            {
                _logger.LogInformation("The worker for {Instance} failed for the same reason {Count} times; " +
                    "trying again because something it depends on has changed", worker.Name, worker.SameFailure);
                Retry(worker);
            }
        Reconcile();
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
    /// one a second — until it dies the same way often enough to be a condition rather than an
    /// accident, which is where the loop stops and the reason is left in place.</summary>
    private void Reconcile()
    {
        lock (_gate)
            foreach (var worker in _workers)
            {
                if (worker.Stopping) continue;
                // Left down on purpose, with its reason still on the row. Nothing here can clear
                // that: only an act that says the reason may have changed does (see Retry).
                if (worker.GivenUp) continue;
                if (IsRunning(worker))
                {
                    if (worker.Error != null && DateTime.UtcNow - worker.StartedAtUtc >= HealthyUptime)
                    {
                        worker.Error = null;
                        worker.ErrorKind = null;
                        worker.FailureKey = null;
                        worker.SameFailure = 0;
                        worker.FirstFailureUtc = default;
                    }
                    continue;
                }

                if (worker.Process != null)
                {
                    var process = worker.Process;
                    int exitCode = ExitCode(process);
                    bool healthy = DateTime.UtcNow - worker.StartedAtUtc >= HealthyUptime;
                    worker.Restarts = healthy ? 1 : worker.Restarts + 1;
                    // The worker leaves its own reason behind when it can; the exit code is what is
                    // left when it could not.
                    var failure = InstanceFailure.Take(worker.Id);
                    worker.Error = failure?.Message ?? "the worker exited with code " + exitCode;
                    worker.ErrorKind = failure?.Kind;
                    CountFailure(worker, failure, exitCode);
                    worker.Job?.Dispose();
                    worker.Job = null;
                    process.Dispose();
                    worker.Process = null;
                    worker.NextStartUtc = DateTime.UtcNow.AddSeconds(BackoffSeconds(worker.Restarts));
                }

                if (DateTime.UtcNow >= worker.NextStartUtc) StartWorker(worker);
            }
    }

    /// <summary>Counts a failure against the one before it, and decides whether the loop is worth
    /// continuing. The reason is in hand here — the worker wrote it down before it exited — so this
    /// is also where it reaches the log a reader actually opens: the per-instance log it came from
    /// is a second file, and "exited with code 2" on its own has never told anybody anything.
    ///
    /// Only a failure the worker described can be given up on. A process that dies without a reason
    /// is a crash, and a crash is what the retry loop is for.</summary>
    private void CountFailure(Worker worker, InstanceFailure.Detail? failure, int exitCode)
    {
        string key = (failure?.Kind ?? "") + "\n" + (failure?.Message ?? "");
        bool same = string.Equals(key, worker.FailureKey, StringComparison.Ordinal);
        worker.SameFailure = same ? worker.SameFailure + 1 : 1;
        if (!same) worker.FirstFailureUtc = DateTime.UtcNow;
        worker.FailureKey = key;

        if (failure == null)
        {
            _logger.LogWarning("The worker for {Instance} exited with code {Code}; restarting it",
                worker.Name, exitCode);
            return;
        }

        if (worker.SameFailure < IdenticalFailuresBeforeGivingUp ||
            DateTime.UtcNow - worker.FirstFailureUtc < SameFailureWindow)
        {
            _logger.LogWarning("The worker for {Instance} exited with code {Code}: {Reason}; restarting it",
                worker.Name, exitCode, worker.Error);
            return;
        }

        worker.GivenUp = true;
        _logger.LogError("The worker for {Instance} has stopped for the same reason {Count} times over " +
            "{Seconds:0} seconds and is not being restarted: {Reason}. Start the instance again, or reload " +
            "the configuration, to retry", worker.Name, worker.SameFailure,
            (DateTime.UtcNow - worker.FirstFailureUtc).TotalSeconds, worker.Error);
    }

    /// <summary>Forgets that a worker gave up, and the failures behind it. Something said the reason
    /// may be gone — the configuration was written, the instance was asked for — and a retry is
    /// cheaper than the reader working out which of the two does it.</summary>
    private static void Retry(Worker worker)
    {
        worker.GivenUp = false;
        worker.FailureKey = null;
        worker.SameFailure = 0;
        worker.FirstFailureUtc = default;
        worker.NextStartUtc = DateTime.MinValue;
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
            foreach (string argument in _arguments) start.ArgumentList.Add(argument);

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
            // The previous failure stays on the row while this attempt is young: a supervisor row
            // that forgets why an instance keeps dying, and shows only a fresh process, is how the
            // reader ends up never seeing the reason. It is cleared once the worker stays up.
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
            worker.ErrorKind = null;
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
