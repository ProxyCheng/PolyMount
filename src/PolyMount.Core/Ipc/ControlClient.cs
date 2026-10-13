using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PolyMount.Core.Ipc;

/// <summary>
/// Named-pipe client for the PolyMount service control protocol (line-delimited JSON).
/// Shared by any front-end (WPF tray, Qt UI, shell extensions).
/// </summary>
public static class ControlClient
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string PipeName { get; set; } = "PolyMount.Control";
    public static int ConnectTimeoutMs { get; set; } = 3000;
    public static int ResponseTimeoutMs { get; set; } = 5000;

    public static ControlResponse Send(ControlRequest request)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(ConnectTimeoutMs);

        // leaveOpen: true on both so disposing the reader/writer does NOT close the pipe out from
        // under the other (the reader closes first, then the writer's flush-on-dispose would hit a
        // closed pipe -> "Cannot access a closed pipe"). Only the pipe's own using closes it.
        using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true))
        {
            using var timeout = new CancellationTokenSource(request.Cmd is ControlCommands.Status or ControlCommands.Metrics or ControlCommands.GetConfig or ControlCommands.Probe
                ? ResponseTimeoutMs : 120000);
            string? line;
            try
            {
                var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, Options) + "\n");
                pipe.WriteAsync(payload.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult();
                pipe.FlushAsync(timeout.Token).GetAwaiter().GetResult();
                line = reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                throw new TimeoutException("Control service did not respond: " + request.Cmd);
            }
            if (line == null) throw new IOException("empty control response");
            return JsonSerializer.Deserialize<ControlResponse>(line) ?? new ControlResponse();
        }
    }

    public static ControlResponse Status()
        => Send(new ControlRequest { Cmd = ControlCommands.Status });

    public static ControlResponse Mount(string? name)
        => Send(new ControlRequest { Cmd = ControlCommands.Mount, Name = name });

    public static ControlResponse Unmount(string? name)
        => Send(new ControlRequest { Cmd = ControlCommands.Unmount, Name = name });

    public static ControlResponse RefreshHead(string? name)
        => Send(new ControlRequest { Cmd = ControlCommands.RefreshHead, Name = name });

    public static ControlResponse SetPin(string? name, long rev)
        => Send(new ControlRequest { Cmd = ControlCommands.SetPin, Name = name, Rev = rev });

    public static ControlResponse ClearCache(string? name)
        => Send(new ControlRequest { Cmd = ControlCommands.ClearCache, Name = name });

    public static ControlResponse Probe(string fullPath)
        => Send(new ControlRequest { Cmd = ControlCommands.Probe, Path = fullPath });

    public static ControlResponse LayerAction(string fullPath, string layer, string action)
        => Send(new ControlRequest { Cmd = ControlCommands.LayerAction, Path = fullPath, Layer = layer, Action = action });

    public static ControlResponse GetConfig()
        => Send(new ControlRequest { Cmd = ControlCommands.GetConfig });

    public static ControlResponse SaveConfig(string json)
        => Send(new ControlRequest { Cmd = ControlCommands.SaveConfig, Config = json });

    public static ControlResponse Reload()
        => Send(new ControlRequest { Cmd = ControlCommands.Reload });

    public static ControlResponse Metrics(int windowSeconds = 300, string? name = null)
        => Send(new ControlRequest { Cmd = ControlCommands.Metrics, WindowSeconds = windowSeconds, Name = name });
}
