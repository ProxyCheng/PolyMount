using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PolyMount.Core;
using PolyMount.Core.Ipc;

namespace PolyMount.Service;

public sealed class MountManager : IControlHost
{
    private readonly List<MountInstance> _instances = new();
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly IHostApplicationLifetime? _lifetime;
    private FileSystemWatcher? _watcher;
    private DateTime _suppressReloadUntil = DateTime.MinValue;

    /// <summary>When this process started, which is also when every instance it hosts started.</summary>
    private static readonly string StartedUtc = ProcessStartedAt();

    private readonly ILogger<MountManager> _logger;
    public MountManager(IHostApplicationLifetime? lifetime = null, string? configPath = null, ILogger<MountManager>? logger = null)
    {
        _logger = logger ?? NullLogger<MountManager>.Instance;
        _lifetime = lifetime;
        ConfigPath = configPath ?? Path.Combine(AppContext.BaseDirectory, "config.json");
        Config = AppConfig.LoadOrCreate(ConfigPath);
        LoadPlugins(Config);
        BuildInstances();
    }

    public AppConfig Config { get; private set; }
    public string ConfigPath { get; }

    /// <summary>The pipe this process serves on, set by the host before it starts listening. Every
    /// instance hosted here is answered there, so it is also each one's own address — one process
    /// and one pipe for all of them, for now. When each instance gets a process of its own, this
    /// becomes the supervisor's own pipe and every endpoint reports its worker's instead.</summary>
    public string LocalPipe { get; set; } = "";

    /// <summary>The service's own log, written next to the executable by the host.</summary>
    public const string ServiceLogName = "polymount-service.log";

    // What an export should carry: each instance's request diagnostics, plus this process's
    // own log. Both are rolled on size, so the family is listed rather than a single name.
    private List<LogFile> CollectLogs()
    {
        var logs = new List<LogFile>();
        lock (_gate)
            foreach (var instance in _instances)
                foreach (string file in instance.GetLogFiles())
                    logs.Add(LogInventory.Describe(file, instance.Name));
        foreach (string file in LogInventory.Family(AppContext.BaseDirectory, ServiceLogName))
            logs.Add(LogInventory.Describe(file, "service"));
        return logs;
    }

    private static void LoadPlugins(AppConfig config)
    {
        var errors = PluginLoader.LoadConfigured(config.Plugins, AppContext.BaseDirectory);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
    }

    private void BuildInstances()
    {
        lock (_gate)
        {
            foreach (var instance in _instances)
                try { instance.Dispose(); } catch { }
            _instances.Clear();
            _errors.Clear();

            foreach (var profile in Config.Mounts)
            {
                if (!profile.Enabled) continue;
                try
                {
                    var instance = new MountInstance(profile) { ColorOverrides = Config.LayerColors };
                    _instances.Add(instance);
                }
                catch (Exception ex)
                {
                    _errors[profile.Name] = ex.Message;
                }
            }
        }
    }

    public void MountAll()
    {
        foreach (var instance in _instances)
        {
            try
            {
                instance.Mount(Config.WinFspBin);
                _errors.Remove(instance.Name);
            }
            catch (Exception ex)
            {
                _errors[instance.Name] = ex.Message;
                Log("mount " + instance.Name, ex);
            }
        }

        StartConfigWatcher();
    }

    public void UnmountAll()
    {
        foreach (var instance in _instances)
        {
            try { instance.Unmount(); } catch { }
        }
    }

    // Rebuild every mount instance from the current on-disk config and remount.
    public void Reload()
    {
        var next = AppConfig.LoadOrCreate(ConfigPath);
        LoadPlugins(next);
        next.Validate();
        UnmountAll();
        Config = next;
        BuildInstances();
        MountAll();
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
                    response.Instances = Endpoints();
                    break;
                case ControlCommands.Mount:
                    try
                    {
                        Find(request.Name).Mount(Config.WinFspBin);
                        // A failure left a message here, and it outlived the failure: the status
                        // then reported a mount point problem for an instance that was up.
                        _errors.Remove(request.Name!);
                    }
                    catch (Exception ex)
                    {
                        _errors[request.Name!] = ex.Message;
                        Log("mount " + request.Name, ex);
                        throw;
                    }
                    break;
                case ControlCommands.Unmount:
                    Find(request.Name).Unmount();
                    break;
                case ControlCommands.Probe:
                case ControlCommands.LayerAction:
                case ControlCommands.ReleaseAbove:
                case ControlCommands.SetPin:
                case ControlCommands.RefreshHead:
                case ControlCommands.ClearCache:
                case ControlCommands.ClearLocalChanges:
                case ControlCommands.RecordingStart:
                case ControlCommands.RecordingStatus:
                case ControlCommands.RecordingStop:
                {
                    var (instance, rel) = Locate(request);
                    InstanceCommands.Apply(instance, rel, request, Config.WinFspBin, response);
                    break;
                }
                case ControlCommands.GetConfig:
                    response.Config = Config.ToJson();
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
                case ControlCommands.Metrics:
                    lock (_gate)
                        response.Metrics = _instances.Where(i => request.Name == null || string.Equals(i.Name, request.Name, StringComparison.OrdinalIgnoreCase))
                            .Select(i => i.GetMetrics(request.WindowSeconds)).ToList();
                    break;
                case ControlCommands.Logs:
                    response.Logs = CollectLogs();
                    break;
                case ControlCommands.Shutdown:
                    // Respond first, then unmount and stop the host shortly after so the reply flushes.
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(300);
                        try { UnmountAll(); } catch { }
                        _lifetime?.StopApplication();
                        if (_lifetime == null) Environment.Exit(0);
                    });
                    break;
                default:
                    throw new InvalidOperationException("unknown command: " + request.Cmd);
            }
        }
        catch (Exception ex)
        {
            response.Ok = false;
            response.Error = ex.Message;
        }

        lock (_gate)
        {
            response.Mounts = _instances.Select(i => i.GetStatus()).ToList();
            foreach (var profile in Config.Mounts.Where(p => response.Mounts.All(s => s.Name != p.Name)))
            {
                var layers = profile.GetLayers();
                response.Mounts.Add(new MountStatus
                {
                    Name = profile.Name, Enabled = profile.Enabled, MountPoint = profile.MountPoint,
                    SourceType = string.Join(" + ", layers.Where(l => SourceRegistry.Contains(l.Type)).Select(l => l.Type).Distinct()),
                    Url = string.Join("\n", layers.Where(l => SourceRegistry.Contains(l.Type)).Select(l => l.Type == "dir" ? l.Source.Path : l.Source.Url)),
                    Providers = layers.Where(l => l.Enabled).Select(l => new ProviderInfo { Name = l.Id, DisplayName = l.Name, Type = l.Type, Color = l.Color,
                        Writable = l.Type == "overlay" || l.Type == "dir" && l.Source.Writable }).ToList()
                });
            }
            foreach (var status in response.Mounts)
                if (_errors.TryGetValue(status.Name, out var err)) status.Error = err;
        }
        return response;
    }

    /// <summary>Where each instance is served, so a front-end can connect to it instead of asking
    /// this process about it. An instance that is switched off is not here: it has no process of
    /// its own, and it is in the configuration and in the status instead.</summary>
    private List<InstanceEndpoint> Endpoints()
    {
        // Whatever the host is actually listening on; the configured name is the fallback for the
        // window between loading the configuration and starting to serve.
        string pipe = LocalPipe.Length > 0 ? LocalPipe : Config.ControlPipe;
        lock (_gate)
            return _instances.Select(instance => new InstanceEndpoint
            {
                Id = instance.Profile.Id,
                Name = instance.Name,
                MountPoint = instance.MountPoint,
                Pipe = pipe,
                WorkerPid = Environment.ProcessId,
                StartedUtc = StartedUtc
            }).ToList();
    }

    private static string ProcessStartedAt()
    {
        try { return Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture); }
        catch (Exception) { return DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture); }
    }

    // The instance a command names. A command about one instance that names none is refused
    // rather than answered about whichever instance happened to be first: every front-end names
    // one, and a caller that does not is asking a question this cannot answer correctly.
    // Which instance a command is about: by its mount point for a command that carries a path,
    // by name for one that names an instance.
    private (MountInstance Instance, string Rel) Locate(ControlRequest request)
    {
        return InstanceCommands.ByPath(request.Cmd)
            ? ResolvePath(request.Path)
            : (Find(request.Name), string.Empty);
    }

    private MountInstance Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("this command needs the instance it is about");
        var instance = _instances.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
        return instance ?? throw new InvalidOperationException("no such mount: " + name);
    }

    // Maps a full local path (e.g. D:\L10\sub\file.txt) to the owning mount instance and the path
    // relative to its mount point.
    private (MountInstance Instance, string Rel) ResolvePath(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            throw new InvalidOperationException("a path is required");

        var full = Path.GetFullPath(fullPath).TrimEnd('\\');
        lock (_gate)
            foreach (var instance in _instances)
                if (InstanceCommands.Under(instance, full) is { } rel)
                    return (instance, rel);

        throw new InvalidOperationException("path is not under any PolyMount mount point: " + full);
    }

    private void Log(string message, Exception ex)
    {
        try
        {
            _logger.LogError(ex, "Operation failed: {Operation}", message);
        }
        catch { }
    }
}
