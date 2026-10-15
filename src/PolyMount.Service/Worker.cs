using Microsoft.Extensions.Hosting;

namespace PolyMount.Service;

public sealed class Worker : BackgroundService
{
    private readonly MountManager _manager;

    public Worker(MountManager manager) => _manager = manager;

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
