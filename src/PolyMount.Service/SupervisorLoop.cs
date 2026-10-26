using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PolyMount.Service;

/// <summary>The supervisor's own loop: it starts a worker per instance and serves the one pipe a
/// front-end knows the name of. Each worker runs the same protocol on a pipe of its own, from the
/// same executable with <c>--instance</c> (see <see cref="InstanceHost"/>).</summary>
public sealed class SupervisorLoop : BackgroundService
{
    private readonly MountManager _manager;
    private readonly ILogger<SupervisorLoop> _logger;

    public SupervisorLoop(MountManager manager, ILogger<SupervisorLoop>? logger = null)
    {
        _manager = manager;
        _logger = logger ?? NullLogger<SupervisorLoop>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _manager.StartAll();
        // What this process answers on: the well-known name, where a front-end asks what exists and
        // which process is running it before connecting to that process itself.
        _manager.LocalPipe = _manager.Config.ControlPipe;

        var server = new PipeServer(_manager, _manager.LocalPipe, _manager.Access, _logger);
        try
        {
            await server.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _manager.StopAll();
        }
    }
}
