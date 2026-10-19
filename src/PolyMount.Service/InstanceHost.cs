using Microsoft.Extensions.Logging;
using PolyMount.Core;
using PolyMount.Core.Ipc;

namespace PolyMount.Service;

/// <summary>
/// One instance, in a process of its own: its mount, its pipe, and nothing else.
///
/// The supervisor starts it with <c>--instance &lt;id&gt;</c> and reaches it on the pipe named for
/// that id. Everything a front-end asks about this instance is answered here, including the fact
/// that it did not come up: a mount that failed is reported in the status rather than taking the
/// process down, because the supervisor has to be able to say what went wrong and restart it.
///
/// This process never writes the configuration — the supervisor owns the file — and never answers
/// for a second instance: a request that names one is refused rather than answered about this one,
/// which is what makes a front-end that reached the wrong pipe fail loudly instead of quietly.
/// </summary>
public sealed class InstanceHost : IControlHost
{
    private readonly CancellationTokenSource _stop = new();
    private readonly ILogger _logger;
    private string? _error;
    private string? _errorKind;

    private InstanceHost(AppConfig config, MountProfile profile, ILogger logger)
    {
        Config = config;
        Instance = new MountInstance(profile) { ColorOverrides = config.LayerColors };
        Pipe = InstancePipes.For(config.ControlPipe, profile.Id);
        _logger = logger;
    }

    public AppConfig Config { get; }
    public MountInstance Instance { get; }
    public string Pipe { get; }

    /// <summary>Runs the worker for one instance until it is told to stop, and returns the exit
    /// code: 2 when the instance could not even be built, 0 once it has been served.</summary>
    public static async Task<int> Run(string configPath, string instanceId, ILogger logger)
    {
        try
        {
            // Read, never created or rewritten: the supervisor is the one that writes this file,
            // and a second writer would be a second opinion about what the service is.
            var config = AppConfig.FromJson(File.ReadAllText(configPath));
            var profile = config.Mounts.FirstOrDefault(m => string.Equals(m.Id, instanceId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("the configuration has no instance with id '" + instanceId + "'");
            var pluginErrors = PluginLoader.LoadConfigured(config.Plugins, AppContext.BaseDirectory);
            if (pluginErrors.Count > 0) throw new InvalidOperationException(string.Join("; ", pluginErrors));

            return await new InstanceHost(config, profile, logger).ServeAsync();
        }
        catch (Exception ex)
        {
            // Left where the supervisor can read it: without this, every startup failure reaches
            // the reader as an exit code, including the one that means "sign in".
            InstanceFailure.Record(instanceId, InstanceFailure.KindOf(ex), ex.Message);
            logger.LogError(ex, "Instance {Instance} could not start", instanceId);
            return 2;
        }
    }

    private async Task<int> ServeAsync()
    {
        try { Instance.Mount(Config.WinFspBin); }
        catch (Exception ex)
        {
            _error = ex.Message;
            _errorKind = InstanceFailure.KindOf(ex);
            _logger.LogError(ex, "Mounting {Instance} failed", Instance.Name);
        }

        Console.CancelKeyPress += (_, args) => { args.Cancel = true; _stop.Cancel(); };
        _logger.LogInformation("Serving {Instance} ({Id}) on {Pipe}", Instance.Name, Instance.Profile.Id, Pipe);
        try
        {
            await new PipeServer(this, Pipe).RunAsync(_stop.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            // A stop is a durability point, not just an unmount: the stores coalesce their
            // writes into short transactions, and a batch still waiting when the process goes
            // is gone. Disposing the instance unmounts and lets every layer flush and close
            // what it accepted — the overlay's database commits there.
            try { Instance.Dispose(); }
            catch (Exception ex) { _logger.LogError(ex, "Stopping {Instance} failed", Instance.Name); }
        }
        return 0;
    }

    public ControlResponse Handle(ControlRequest request)
    {
        var response = ControlEnvelope.New();
        try
        {
            if (request.Name is { Length: > 0 } name && !string.Equals(name, Instance.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"this process serves '{Instance.Name}', not '{name}'");

            switch (request.Cmd)
            {
                case ControlCommands.Status:
                    break;
                case ControlCommands.Metrics:
                    response.Metrics.Add(Instance.GetMetrics(request.WindowSeconds));
                    break;
                case ControlCommands.Logs:
                    response.Logs = Logs();
                    break;
                case ControlCommands.Shutdown:
                    // Answer first, then unmount and stop serving: the reply has to reach whoever
                    // asked, or a supervisor stopping this instance cannot tell why it went.
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(300);
                        _stop.Cancel();
                    });
                    break;
                default:
                    string rel = InstanceCommands.ByPath(request.Cmd) ? InstanceCommands.RelativeTo(Instance, request.Path) : string.Empty;
                    InstanceCommands.Apply(Instance, rel, request, Config.WinFspBin, response);
                    break;
            }
        }
        catch (Exception ex)
        {
            response.Ok = false;
            response.Error = ex.Message;
        }

        // A metrics answer carries metrics. The status is a per-layer survey - a provider entry
        // each, a storage enumeration, the read-lane counters - and it costs more than everything
        // else in this reply put together, while every caller of `metrics` reads only what it
        // asked for: the window it wanted. Whoever needs the state of the instance asks `status`,
        // which is what the Dashboard, the editor and an export all do.
        if (!string.Equals(request.Cmd, ControlCommands.Metrics, StringComparison.Ordinal))
        {
            var status = Instance.GetStatus();
            status.Error ??= _error;
            status.ErrorKind ??= _errorKind;
            response.Mounts = new List<MountStatus> { status };
        }
        // The layer registries go to the commands that open a window on a path - a probe is a
        // click - and not to the ones that poll. They are the largest thing this service can say,
        // and a window asking for a metrics window ten times a second has no use for them.
        if (InstanceCommands.ByPath(request.Cmd)) ControlEnvelope.WithRegistry(response);
        return response;
    }

    /// <summary>This instance's request diagnostics, plus this process's own log: what an export
    /// or a recording would take from here.</summary>
    private List<LogFile> Logs()
    {
        var logs = Instance.GetLogFiles().Select(file => LogInventory.Describe(file, Instance.Name)).ToList();
        foreach (string file in LogInventory.Family(ServiceLogs.Directory, MountManager.WorkerLogName(Instance.Profile.Id)))
            logs.Add(LogInventory.Describe(file, "service"));
        return logs;
    }
}
