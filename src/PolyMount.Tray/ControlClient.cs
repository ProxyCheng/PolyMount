using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PolyMount.Core.Ipc;

namespace PolyMount.Tray;

public static class ControlClient
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string PipeName { get; set; } = "PolyMount.Control";

    public static ControlResponse Send(ControlRequest request)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
        pipe.Connect(3000);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8);

        writer.WriteLine(JsonSerializer.Serialize(request, Options));
        var line = reader.ReadLine();
        if (line == null) throw new IOException("empty control response");
        return JsonSerializer.Deserialize<ControlResponse>(line) ?? new ControlResponse();
    }

    public static ControlResponse Status()
        => Send(new ControlRequest { Cmd = ControlCommands.Status });

    public static ControlResponse Probe(string fullPath)
        => Send(new ControlRequest { Cmd = ControlCommands.Probe, Path = fullPath });

    public static ControlResponse LayerAction(string fullPath, string layer, string action)
        => Send(new ControlRequest
        {
            Cmd = ControlCommands.LayerAction,
            Path = fullPath,
            Layer = layer,
            Action = action
        });

    public static ControlResponse GetConfig()
        => Send(new ControlRequest { Cmd = ControlCommands.GetConfig });

    public static ControlResponse SaveConfig(string json)
        => Send(new ControlRequest { Cmd = ControlCommands.SaveConfig, Config = json });

    public static ControlResponse Reload()
        => Send(new ControlRequest { Cmd = ControlCommands.Reload });

    public static ControlResponse Metrics(int windowSeconds = 300)
        => Send(new ControlRequest { Cmd = ControlCommands.Metrics, WindowSeconds = windowSeconds });
}
