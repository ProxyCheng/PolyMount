using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PolyMount.Core;
using PolyMount.Core.Ipc;

namespace PolyMount.Service;

public sealed class MountManager
{
    private readonly List<MountInstance> _instances = new();
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly IHostApplicationLifetime? _lifetime;
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
        BuildInstances();
    }

    public AppConfig Config { get; private set; }
    public string ConfigPath { get; }

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
        var response = new ControlResponse { SourceTypes = SourceRegistry.Types.ToList(), LayerDescriptors = LayerRegistry.Descriptors.ToList(), Presets = LayerPresetRegistry.All.ToList() };
        try
        {
            switch (request.Cmd)
            {
                case ControlCommands.Status:
                    break;
                case ControlCommands.SetPin:
                    Find(request.Name).SetPin(request.Rev, request.Layer);
                    break;
                case ControlCommands.RefreshHead:
                    Find(request.Name).RefreshHead(request.Layer);
                    break;
                case ControlCommands.ClearCache:
                    Find(request.Name).ClearCache(request.Layer);
                    break;
                case ControlCommands.ClearLocalChanges:
                    Find(request.Name).ClearLocalChanges(request.Layer ?? "", Config.WinFspBin);
                    break;
                case ControlCommands.Mount:
                    Find(request.Name).Mount(Config.WinFspBin);
                    break;
                case ControlCommands.Unmount:
                    Find(request.Name).Unmount();
                    break;
                case ControlCommands.Probe:
                    response.Probe = ProbePath(request.Path);
                    break;
                case ControlCommands.LayerAction:
                {
                    var (inst, rel) = ResolvePath(request.Path);
                    response.Probe = inst.ApplyLayerAction(request.Layer ?? "", rel, request.Action ?? "");
                    break;
                }
                case ControlCommands.ReleaseAbove:
                {
                    var (instance, rel) = ResolvePath(request.Path);
                    response.Probe = instance.ReleaseAbove(rel, request.Boundary, request.ExpectedLayers);
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

    private MountInstance Find(string? name)
    {
        var instance = name == null
            ? _instances.FirstOrDefault()
            : _instances.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
        return instance ?? throw new InvalidOperationException("no such mount: " + name);
    }

    // Maps a full local path (e.g. D:\L10\sub\file.txt) to the owning mount instance and the
    // path relative to its mount point, then probes the layer stack.
    private ProbeResult ProbePath(string? fullPath)
    {
        var (inst, rel) = ResolvePath(fullPath);
        return inst.Probe(rel);
    }

    private (MountInstance Instance, string Rel) ResolvePath(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            throw new InvalidOperationException("a path is required");

        var full = Path.GetFullPath(fullPath).TrimEnd('\\');

        lock (_gate)
        {
            foreach (var inst in _instances)
            {
                var mp = Path.GetFullPath(inst.MountPoint).TrimEnd('\\');
                if (full.Equals(mp, StringComparison.OrdinalIgnoreCase))
                    return (inst, string.Empty);

                var prefix = mp + "\\";
                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return (inst, VfsPath.Normalize(full.Substring(prefix.Length)));
            }
        }

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
