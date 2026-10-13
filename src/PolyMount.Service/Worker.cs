using Microsoft.Extensions.Hosting;

namespace PolyMount.Service;

public sealed class Worker : BackgroundService
{
    private readonly MountManager _manager;

    public Worker(MountManager manager) => _manager = manager;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _manager.MountAll();

        var server = new PipeServer(_manager, _manager.Config.ControlPipe);
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
