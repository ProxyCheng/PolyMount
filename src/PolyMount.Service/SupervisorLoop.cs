using Microsoft.Extensions.Hosting;

namespace PolyMount.Service;

/// <summary>The supervisor's own loop: it mounts what it hosts and serves the one pipe a
/// front-end knows the name of. Each instance's worker runs the same protocol on a pipe of its
/// own, from the same executable with <c>--instance</c> (see <see cref="InstanceHost"/>).</summary>
public sealed class SupervisorLoop : BackgroundService
{
    private readonly MountManager _manager;

    public SupervisorLoop(MountManager manager) => _manager = manager;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _manager.MountAll();
        // What this process answers on. The instances it hosts are reached here, which is what the
        // "instances" command reports to a front-end that is about to connect to one of them.
        _manager.LocalPipe = _manager.Config.ControlPipe;

        var server = new PipeServer(_manager, _manager.LocalPipe);
        try
        {
            await server.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _manager.UnmountAll();
        }
    }
}
