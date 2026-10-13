using System.Text.Json;
using PolyMount.Core;
using PolyMount.Core.Ipc;

namespace PolyMount.Service;

public sealed class MountManager
{
    private readonly List<MountInstance> _instances = new();
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public MountManager()
    {
        ConfigPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        Config = AppConfig.LoadOrCreate(ConfigPath);

        foreach (var profile in Config.Mounts)
        {
            if (!profile.Enabled) continue;
            try
            {
                _instances.Add(new MountInstance(profile));
            }
            catch (Exception ex)
            {
                _errors[profile.Name] = ex.Message;
            }
        }
    }

    public AppConfig Config { get; }
    public string ConfigPath { get; }

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
    }

    public void UnmountAll()
    {
        foreach (var instance in _instances)
        {
            try { instance.Unmount(); } catch { }
        }
    }

    public ControlResponse Handle(ControlRequest request)
    {
        var response = new ControlResponse { SourceTypes = SourceRegistry.Types.ToList() };
        try
        {
            switch (request.Cmd)
            {
                case ControlCommands.Status:
                    break;
                case ControlCommands.SetPin:
                    Find(request.Name).SetPin(request.Rev);
                    break;
                case ControlCommands.RefreshHead:
                    Find(request.Name).RefreshHead();
                    break;
                case ControlCommands.ClearCache:
                    Find(request.Name).ClearCache();
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

    private static void Log(string message, Exception ex)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "polymount-service.log");
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}: {ex}\n");
        }
        catch { }
    }
}
