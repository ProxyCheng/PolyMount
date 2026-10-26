using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PolyMount.Core.Ipc;

namespace PolyMount.Service;

/// <summary>The pipe a process answers on, one connection per request.
///
/// What this class has to survive is the fact that anybody who can create a pipe name can also
/// connect to one: a request line of unbounded length, many connections that never send anything,
/// a caller that is not on the control plane's list, and a name that a different process already
/// owns. None of those may take the supervisor down — the process that keeps the mounts up is the
/// one being poked at — so each has a bound here, and a failure reopens the pipe rather than
/// faulting the host.
///
/// The caller is identified from the connection itself (see <see cref="PipeCaller"/>). The pipe's
/// access rules are the authorization; this is the same decision made again on the request, so an
/// installation whose policy is still open says in the log who used it.</summary>
public sealed class PipeServer
{
    /// <summary>How many requests one process serves at once. A control client opens a connection
    /// per request and is over in milliseconds, so this is far above what any front-end uses; it
    /// exists so a caller cannot turn connections into memory and threads without a ceiling.</summary>
    private const int MaxConnections = 32;

    /// <summary>The longest request line accepted. The largest thing that crosses this pipe is a
    /// whole configuration document, which is a few kilobytes for every installation this product
    /// has.</summary>
    private const int MaxRequestBytes = 4 * 1024 * 1024;

    /// <summary>How long a connected caller has to send its line. A client that connects and then
    /// says nothing is holding one of the slots above.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    private readonly IControlHost _host;
    private readonly string _pipeName;
    private readonly ControlAccess _access;
    private readonly ILogger _logger;

    public PipeServer(IControlHost host, string pipeName, ControlAccess access, ILogger? logger = null)
    {
        _host = host;
        _pipeName = pipeName;
        _access = access;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        WarnAboutThePolicy();
        CheckNameOwnership();

        var slots = new SemaphoreSlim(MaxConnections, MaxConnections);
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                // The descriptor matters as much as the name: a service running under an account of
                // its own and a tray running under the signed-in user share nothing by default, and
                // the default pipe descriptor gives that user read access but not the write a duplex
                // client needs. See ControlPipe for the rules and why they are those.
                pipe = ControlPipe.CreateServer(_pipeName, _access);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A name somebody else holds with a descriptor that does not let us create another
                // instance, or a pipe table that is momentarily full. Retrying is what this loop is
                // for; letting it fault the host would take the mounts down with it.
                _logger.LogError(ex, "Could not create the control pipe '{Pipe}'; retrying", _pipeName);
                await Delay(TimeSpan.FromSeconds(1), cancellationToken);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Waiting for a connection on '{Pipe}' failed; reopening it", _pipeName);
                pipe.Dispose();
                await Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                continue;
            }

            if (!slots.Wait(0))
            {
                // Answer and let go rather than queue: a caller that would have to wait behind a
                // full pipe is better told so than left hanging on a connection nobody reads.
                _ = Task.Run(async () =>
                {
                    using (pipe) await Refuse(pipe, "the control pipe is serving its " + MaxConnections + " connections; try again");
                }, CancellationToken.None);
                continue;
            }

            _ = Task.Run(async () =>
            {
                try { await HandleAsync(pipe); }
                finally { slots.Release(); }
            }, CancellationToken.None);
        }
    }

    private void WarnAboutThePolicy()
    {
        if (_access.Warning is { } warning) _logger.LogWarning("{Warning}", warning);
        else _logger.LogInformation("Control pipe '{Pipe}' accepts: {Accounts}", _pipeName,
            string.Join(", ", _access.Allowed.Select(ControlAccess.Describe)));
    }

    /// <summary>Whether this pipe name already belongs to another process. A second creator shares
    /// the name rather than failing, and connections are then handed to either — so the service
    /// says so once, instead of leaving a caller to wonder why its answers changed.</summary>
    private void CheckNameOwnership()
    {
        if (!PipeCaller.NameIsTaken(_pipeName)) return;
        _logger.LogError("Another process already serves the control pipe '{Pipe}'. Clients may reach that process "
            + "instead of this one; the account it runs under and its image are logged as each request arrives.", _pipeName);
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            try
            {
                var caller = PipeCaller.Client(pipe);
                if (!Allowed(caller, out string reason))
                {
                    _logger.LogWarning("Refused a control request on '{Pipe}' from {Caller}: {Reason}",
                        _pipeName, Describe(caller), reason);
                    await Refuse(pipe, "this account may not drive PolyMount: " + reason);
                    return;
                }
                if (caller is { } who)
                    _logger.LogDebug("Control request from {Account} ({Sid}) in {Image}",
                        who.Account, who.UserSid, who.ImagePath);

                string? line;
                using (var timeout = new CancellationTokenSource(IdleTimeout))
                {
                    try { line = await ReadLineAsync(pipe, timeout.Token); }
                    catch (InvalidDataException)
                    {
                        await Refuse(pipe, "the request is larger than " + MaxRequestBytes + " bytes");
                        return;
                    }
                    catch (OperationCanceledException) { return; }
                }
                if (line == null) return;

                ControlRequest request;
                try { request = JsonSerializer.Deserialize<ControlRequest>(line) ?? new ControlRequest(); }
                catch (JsonException)
                {
                    await Refuse(pipe, "the request is not a control request");
                    return;
                }

                string answer = JsonSerializer.Serialize(_host.Handle(request));
                await pipe.WriteAsync(Encoding.UTF8.GetBytes(answer + "\n"));
                await pipe.FlushAsync();
            }
            catch (Exception ex)
            {
                // One broken connection is not the service's problem; it is logged so a caller
                // that fails repeatedly is visible.
                _logger.LogWarning(ex, "A control request on '{Pipe}' failed", _pipeName);
            }
        }
    }

    /// <summary>Whether the connected caller may drive this service.
    ///
    /// An open policy accepts anybody — that is what open means, and the rules on the pipe already
    /// limited it to the local interactive session. A configured policy accepts the accounts it
    /// names, administrators, and the account this process runs as; anything else is refused,
    /// including a caller whose token cannot be read, because a policy that cannot be evaluated is
    /// not one that can be satisfied.</summary>
    private bool Allowed(PipeCaller.Identity? caller, out string reason)
    {
        reason = "";
        if (_access.IsOpen) return true;
        if (caller == null)
        {
            reason = "the caller's token could not be read";
            return false;
        }
        if (_access.Allows(caller.UserSid) || caller.IsAdministrator) return true;
        if (string.Equals(caller.UserSid, ControlAccess.CurrentSid(), StringComparison.OrdinalIgnoreCase)) return true;
        reason = "it is not one of the accounts this installation names in ControlUsers";
        return false;
    }

    private static string Describe(PipeCaller.Identity? caller) => caller == null
        ? "an unidentified process"
        : $"{caller.Account} ({caller.UserSid}) in {caller.ImagePath}";

    /// <summary>Reads one request line, bounded in length and in time. The reader is written out
    /// here rather than taken from <see cref="StreamReader"/> because that one grows to whatever
    /// the caller sends, and the length is exactly what a hostile caller controls.</summary>
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[4096];
        using var line = new MemoryStream();
        while (true)
        {
            int read = await stream.ReadAsync(buffer, token);
            if (read == 0)
                return line.Length == 0 ? null : Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
            int newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            int take = newline >= 0 ? newline : read;
            if (line.Length + take > MaxRequestBytes) throw new InvalidDataException("request too large");
            line.Write(buffer, 0, take);
            if (newline >= 0) return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
        }
    }

    /// <summary>A control response for a request that will not be served. Same shape as an error
    /// from a command, so a client reports it rather than failing to parse the answer.</summary>
    private async Task Refuse(NamedPipeServerStream pipe, string reason)
    {
        try
        {
            var payload = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new ControlResponse { Ok = false, Error = reason }) + "\n");
            await pipe.WriteAsync(payload);
            await pipe.FlushAsync();
        }
        catch (Exception) { }
    }

    private static async Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        try { await Task.Delay(delay, cancellationToken); }
        catch (OperationCanceledException) { }
    }
}
