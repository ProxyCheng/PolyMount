using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PolyMount.Core.Ipc;

namespace PolyMount.Service;

public sealed class PipeServer
{
    private readonly MountManager _manager;
    private readonly string _pipeName;

    public PipeServer(MountManager manager, string pipeName)
    {
        _manager = manager;
        _pipeName = pipeName;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }

            _ = Task.Run(() => Handle(pipe));
        }
    }

    private void Handle(NamedPipeServerStream pipe)
    {
        try
        {
            using (pipe)
            using (var reader = new StreamReader(pipe, Encoding.UTF8))
            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true })
            {
                var line = reader.ReadLine();
                if (line == null) return;

                var request = JsonSerializer.Deserialize<ControlRequest>(line) ?? new ControlRequest();
                var response = _manager.Handle(request);
                writer.WriteLine(JsonSerializer.Serialize(response));
            }
        }
        catch { }
    }
}
