using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PolyMount.Core;
using PolyMount.Core.Ipc;

namespace PolyMount.Service;

/// <summary>The supervisor: what exists, and which process is running it.
///
/// It owns the configuration — the one copy, and the file — decides which instances are running,
/// and answers for the service as a whole. It does not host a mount itself: each instance is a
/// worker process of its own (<see cref="InstanceHost"/>), and everything about one instance's own
/// state is asked of that process directly rather than routed through here.
/// </summary>
public sealed class MountManager : IControlHost, IDisposable
{
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly WorkerPool _workers;
    private FileSystemWatcher? _watcher;
    private DateTime _suppressReloadUntil = DateTime.MinValue;

    private readonly ILogger<MountManager> _logger;
    public MountManager(IHostApplicationLifetime? lifetime = null, string? configPath = null, ILogger<MountManager>? logger = null)
    {
        _logger = logger ?? NullLogger<MountManager>.Instance;
        _lifetime = lifetime;
        ConfigPath = configPath ?? Path.Combine(AppContext.BaseDirectory, "config.json");
        Config = AppConfig.LoadOrCreate(ConfigPath);
        LoadPlugins(Config);
        _workers = new WorkerPool(ConfigPath, _logger);
    }

    public AppConfig Config { get; private set; }
    public string ConfigPath { get; }

    /// <summary>The pipe this process serves on, set by the host before it starts listening: the
    /// one well-known name, where a front-end asks what exists and which process runs it.</summary>
    public string LocalPipe { get; set; } = "";

    /// <summary>The service's own log, written next to the executable by the host.</summary>
    public const string ServiceLogName = "polymount-service.log";

    // What an export should carry: each instance's request diagnostics, plus this process's
    // own log. Both are rolled on size, so the family is listed rather than a single name.
    private List<LogFile> CollectLogs()
    {
        var logs = new List<LogFile>();
        // Read off the configuration rather than collected from the workers: an instance whose
        // worker is down still has the log of what it did, and that is exactly when it is wanted.
        foreach (var profile in Config.Mounts)
        {
            foreach (string file in LogInventory.Family(StateDirectory(profile), "requests.log"))
                logs.Add(LogInventory.Describe(file, profile.Name));
            // What that instance's own process wrote, under the instance rather than under the
            // service: a recording of one instance carries its own process's log and no other
            // instance's, and the worker's file name does not have to say whose it is twice.
            foreach (string file in LogInventory.Family(AppContext.BaseDirectory, WorkerLogName(profile.Id)))
                logs.Add(LogInventory.Describe(file, profile.Name, WorkerLogFile(file, profile)));
        }
        // This process's own log: the same name shape, without the instance identity a worker's
        // has. Without the filter, every worker's log would be listed here as well.
        foreach (string file in LogInventory.Family(AppContext.BaseDirectory, ServiceLogName))
            if (!Path.GetFileName(file).StartsWith(WorkerLogPrefix, StringComparison.OrdinalIgnoreCase))
                logs.Add(LogInventory.Describe(file, "service"));
        return logs;
    }

    /// <summary>A worker's log file, as the worker named it: the service log's name with the
    /// instance's identity appended, so two workers never write into one file.</summary>
    private const string WorkerLogPrefix = "polymount-service-";

    internal static string WorkerLogName(string instanceId) => WorkerLogPrefix + instanceId + ".log";

    /// <summary>What a worker's log is called inside a bundle, with the identity taken back out:
    /// the folder it is copied into is the instance's.</summary>
    private static string WorkerLogFile(string path, MountProfile profile) =>
        Path.GetFileName(path).Replace("-" + profile.Id, "");

    private static string StateDirectory(MountProfile profile) => Path.Combine(profile.GetStoreRoot(), "state");

    private static void LoadPlugins(AppConfig config)
    {
        var errors = PluginLoader.LoadConfigured(config.Plugins, AppContext.BaseDirectory);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
    }

    /// <summary>Starts a worker for every enabled instance, and watches the configuration from
    /// here on: this process is the only one that reads it, so it is the only one that reloads.</summary>
    public void StartAll()
    {
        _workers.Apply(Config);
        StartConfigWatcher();
    }

    /// <summary>Stops every worker, in order, so their mounts come down and their stores are
    /// closed rather than being taken away with the process.</summary>
    public void StopAll() => _workers.StopAll();

    /// <summary>Re-reads the configuration and brings the workers in line with it. Only the
    /// instances whose settings actually changed are restarted, which is the point of a process per
    /// instance: editing one mount no longer interrupts another's cache or its warm-up.</summary>
    public void Reload()
    {
        var next = AppConfig.LoadOrCreate(ConfigPath);
        LoadPlugins(next);
        next.Validate();
        Config = next;
        _workers.Apply(next);
    }

    private void StartConfigWatcher()
    {
        if (_watcher != null) return;
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath)!;
            var file = Path.GetFileName(ConfigPath);
            _watcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnConfigFileChanged;
        }
        catch (Exception ex) { Log("config watcher", ex); }
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        // Ignore the write we triggered ourselves via saveConfig.
        if (DateTime.UtcNow < _suppressReloadUntil) return;
        // Debounce: editors often fire several events in quick succession.
        System.Threading.Thread.Sleep(300);
        try { Reload(); }
        catch (Exception ex) { Log("hot reload", ex); }
    }

    public ControlResponse Handle(ControlRequest request)
    {
        var response = ControlEnvelope.New();
        try
        {
            switch (request.Cmd)
            {
                case ControlCommands.Status:
                    break;
                case ControlCommands.Instances:
                    response.Instances = _workers.Endpoints();
                    break;
                case ControlCommands.Mount:
                    _workers.Start(request.Name);
                    break;
                case ControlCommands.Unmount:
                    _workers.Stop(request.Name);
                    break;
                case ControlCommands.GetConfig:
                    response.Config = Config.ToJson();
                    // The one answer that is asked for once, when an editor opens, and the only
                    // place these belong: everything else here is polled.
                    ControlEnvelope.WithRegistry(response);
                    break;
                case ControlCommands.SaveConfig:
                {
                    if (string.IsNullOrWhiteSpace(request.Config))
                        throw new InvalidOperationException("saveConfig requires a Config payload");
                    var parsed = AppConfig.FromJson(request.Config);
                    LoadPlugins(parsed);
                    parsed.Validate();
                    _suppressReloadUntil = DateTime.UtcNow.AddSeconds(2); // avoid double reload from watcher
                    AppConfig.Save(ConfigPath, parsed);
                    Reload();
                    response.Config = Config.ToJson();
                    break;
                }
                case ControlCommands.Reload:
                    Reload();
                    break;
                case ControlCommands.Logs:
                    response.Logs = CollectLogs();
                    break;
                case ControlCommands.Shutdown:
                    // Respond first, then stop the workers and the host shortly after so the reply
                    // flushes: a worker that is told to stop unmounts in order.
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(300);
                        try { _workers.StopAll(); } catch { }
                        _lifetime?.StopApplication();
                        if (_lifetime == null) Environment.Exit(0);
                    });
                    break;
                default:
                    // Everything about one instance's own state is answered by that instance's
                    // process. This one knows where that is, so it says so rather than forwarding:
                    // a front-end that asks here needs the address, not a second-hand copy of the
                    // state it was after.
                    if (InstanceCommands.Handles(request.Cmd))
                        throw new InvalidOperationException(request.Cmd + " is answered by the instance's own " +
                            "process, at " + WorkerPipe(request.Name));
                    throw new InvalidOperationException("unknown command: " + request.Cmd);
            }
        }
        catch (Exception ex)
        {
            response.Ok = false;
            response.Error = ex.Message;
        }

        response.Mounts = Statuses();
        return response;
    }

    /// <summary>What the supervisor knows of each instance: what it is configured to be, and which
    /// process is running it. Everything the instance itself knows — whether the mount came up,
    /// what its layers are serving, how much its cache holds — belongs to that process, and a
    /// front-end that shows both asks the worker for itself and puts the two together. What this
    /// adds is the part only the supervisor has: that an instance exists at all when nothing is
    /// running it, and why nothing is.</summary>
    private List<MountStatus> Statuses()
    {
        var states = _workers.States();
        var rows = new List<MountStatus>();
        foreach (var profile in Config.Mounts)
        {
            var layers = profile.GetLayers();
            var state = states.FirstOrDefault(s => string.Equals(s.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
            rows.Add(new MountStatus
            {
                Name = profile.Name,
                Enabled = profile.Enabled,
                Mounted = state?.Running == true,
                MountPoint = profile.MountPoint,
                SourceType = string.Join(" + ", layers.Where(l => SourceRegistry.Contains(l.Type)).Select(l => l.Type).Distinct()),
                Url = string.Join("\n", layers.Where(l => SourceRegistry.Contains(l.Type)).Select(l => l.Type == "dir" ? l.Source.Path : l.Source.Url)),
                Providers = layers.Where(l => l.Enabled).Select(l => new ProviderInfo
                {
                    Name = l.Id, DisplayName = l.Name, Type = l.Type, Color = l.Color,
                    Writable = l.Type == "overlay" || l.Type == "dir" && l.Source.Writable
                }).ToList(),
                Pipe = state?.Running == true ? state.Pipe : "",
                WorkerPid = state?.Pid ?? 0,
                Restarts = state?.Restarts ?? 0,
                Error = state?.Error
            });
        }
        return rows;
    }

    private string WorkerPipe(string? name) => _workers.States()
        .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))?.Pipe ?? "its own pipe";

    // The instance a command names. A command about one instance that names none is refused
    // rather than answered about whichever instance happened to be first: every front-end names
    // one, and a caller that does not is asking a question this cannot answer correctly.
    private void Log(string message, Exception ex)
    {
        try
        {
            _logger.LogError(ex, "Operation failed: {Operation}", message);
        }
        catch { }
    }

    /// <summary>Stops the workers on the way out of the process. The jobs would take them down
    /// anyway, but an ordered stop is what unmounts and closes the stores.</summary>
    public void Dispose() => _workers.Dispose();
}
