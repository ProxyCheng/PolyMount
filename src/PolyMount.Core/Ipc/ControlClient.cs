using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PolyMount.Core.Ipc;

/// <summary>
/// Named-pipe client for the PolyMount control protocol (line-delimited JSON), shared by every
/// front-end (tray, shell extension).
///
/// One client is bound to one pipe, because there is a pipe per instance: a request sent to the
/// wrong instance is one that quietly answers about another mount, so the address belongs to the
/// client rather than to an argument each call could forget. The two kinds of process answer
/// different questions — the supervisor is the only one that knows the instance list and owns the
/// configuration, a worker only knows its own instance — and that difference is the two
/// subclasses, so a call that a process cannot answer is not one a front-end can write.
///
/// The commands both answer live here: every process reports its status, its own share of the
/// metrics and the logs it holds.
/// </summary>
public abstract class ControlClient
{
    /// <summary>The supervisor's pipe: the only address a front-end knows without being told.
    /// Instances that live in the supervisor's own process are answered there too, which is what an
    /// instance with no worker of its own looks like from here.</summary>
    public const string SupervisorPipe = "PolyMount.Control";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    protected ControlClient(string? pipeName = null)
        => PipeName = string.IsNullOrEmpty(pipeName) ? SupervisorPipe : pipeName;

    public string PipeName { get; }
    public int ConnectTimeoutMs { get; set; } = 3000;
    public int ResponseTimeoutMs { get; set; } = 5000;

    /// <summary>Whether the process on the other end is checked before a request is sent to it.
    ///
    /// A pipe name is a first-come claim: a process that creates <c>PolyMount.Control</c> before
    /// the service does shares the name with it, and the connection may then land on either. The
    /// service refuses to be impersonated from the other side (the pipe's access rules name the
    /// accounts that may drive it), but a client also has to know it is talking to the product
    /// rather than to whatever else holds the name — the tray sends credentials over this pipe,
    /// and an answer that was forged is exactly as damaging as one that leaked.
    ///
    /// The check is what can be known without an installed secret: the server's image is
    /// PolyMount.Service.exe, or the server is this very process (an in-process pipe, which is
    /// what the tests and the single-process development runs use). A server whose image cannot
    /// be read is accepted — the alternative is a client that cannot distinguish a hardened
    /// service from an unreadable one — and that limit is written down with the rest of the
    /// design in <c>SECURITY.md</c>.</summary>
    public bool VerifyServer { get; set; } = true;

    /// <summary>The image name a control pipe is served from: the one executable that hosts an
    /// instance, whether as supervisor or worker.</summary>
    private const string ServerImage = "PolyMount.Service.exe";

    /// <summary>The process preparing a request (see <see cref="Prepare"/>).</summary>
    protected virtual ControlRequest Prepare(ControlRequest request) => request;

    public ControlResponse Send(ControlRequest request)
    {
        request = Prepare(request);
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(ConnectTimeoutMs);
        if (VerifyServer) VerifyServerIdentity(pipe);

        // leaveOpen: true on both so disposing the reader/writer does NOT close the pipe out from
        // under the other (the reader closes first, then the writer's flush-on-dispose would hit a
        // closed pipe -> "Cannot access a closed pipe"). Only the pipe's own using closes it.
        using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true))
        {
            using var timeout = new CancellationTokenSource(request.Cmd is ControlCommands.Status or ControlCommands.Metrics or ControlCommands.GetConfig or ControlCommands.Probe
                or ControlCommands.RecordingStart or ControlCommands.RecordingStatus or ControlCommands.RecordingStop or ControlCommands.Instances
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

    /// <summary>Refuses a connection to something that is not PolyMount. Every caller inherits
    /// this, so a front-end cannot forget it: the failure says which process answered, which is
    /// the only way a squatted name is ever noticed.</summary>
    private void VerifyServerIdentity(NamedPipeClientStream pipe)
    {
        var server = PipeCaller.Server(pipe);
        if (server == null) return;
        if (server.ProcessId == Environment.ProcessId) return;
        if (string.Equals(Path.GetFileName(server.ImagePath), ServerImage, StringComparison.OrdinalIgnoreCase)) return;
        throw new IOException(
            $"the control pipe '{PipeName}' is served by {server.Account} running "
            + (server.ImagePath.Length > 0 ? "'" + server.ImagePath + "'" : "an unreadable image")
            + $" (pid {server.ProcessId}), not PolyMount; refusing to send it a request");
    }

    /// <summary>How the instance this client answers for is doing. The supervisor reports every
    /// instance it hosts; a worker reports its own.</summary>
    public ControlResponse Status()
        => Send(new ControlRequest { Cmd = ControlCommands.Status });

    /// <summary>Read analytics over a recent window. The supervisor spreads it over every instance
    /// unless one is named; a worker only ever has its own, so naming one there is redundant.</summary>
    public ControlResponse Metrics(double windowSeconds = 300, string? name = null)
        => Send(new ControlRequest { Cmd = ControlCommands.Metrics, WindowSeconds = windowSeconds, Name = name });

    /// <summary>What an export can carry: each instance's request diagnostics, plus the log of the
    /// process that answered.</summary>
    public ControlResponse Logs()
        => Send(new ControlRequest { Cmd = ControlCommands.Logs });
}
