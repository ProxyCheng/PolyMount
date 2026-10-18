using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PolyMount.Core.Ipc;

namespace PolyMount.Mcp;

/// <summary>
/// The MCP front-end: a stdio server an agent starts, which drives the running PolyMount service
/// over the control pipes.
///
/// It is a separate process on purpose. The service is a Windows service in session 0 and cannot
/// host a conversation with a user; the tray is a window and a Qt runtime that a command-line
/// agent has no use for. What an agent needs is the one thing both of those already speak — the
/// control protocol — and the smallest possible process around it.
///
/// Nothing is written to stdout except the protocol itself: a client parses that stream, and one
/// stray log line corrupts it. Failures travel back as tool errors, which is where the reader is.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? pipe = null;
        bool readOnly = false;
        bool describe = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pipe" when i + 1 < args.Length:
                    pipe = args[++i];
                    break;
                case "--read-only":
                    readOnly = true;
                    break;
                case "--describe":
                    describe = true;
                    break;
                case "-h" or "--help":
                    Console.Error.WriteLine(Usage);
                    return 0;
                default:
                    Console.Error.WriteLine($"PolyMount.Mcp: unknown argument '{args[i]}'");
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }

        // The service's pipe is a setting; a renamed control pipe reaches this process the same
        // way the tray reaches it.
        pipe ??= Environment.GetEnvironmentVariable("POLYMOUNT_PIPE");
        pipe = string.IsNullOrWhiteSpace(pipe) ? ControlClient.SupervisorPipe : pipe;
        SupervisorClient.Default = new SupervisorClient(pipe);

        var surface = new ToolSurface(PipeControlLink.Default, pipe, readOnly);

        // Writes the catalog and exits. It needs no service and no handshake, which is what makes
        // it the fastest way to check what this build offers.
        if (describe)
        {
            Console.WriteLine(surface.Describe());
            return 0;
        }

        var builder = Host.CreateApplicationBuilder([]);

        // stdout is the protocol channel. The host's default console logger writes there, and a
        // single log line is a parse error for every client; nothing this process has to say is
        // for a person anyway.
        builder.Logging.ClearProviders();

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "polymount", Version = Version() };
                options.ServerInstructions = Instructions;
            })
            .WithStdioServerTransport()
            .WithListToolsHandler((context, cancellationToken) =>
                ValueTask.FromResult(new ListToolsResult { Tools = surface.Tools() }))
            .WithCallToolHandler((context, cancellationToken) =>
                surface.CallAsync(context.Params, cancellationToken));

        await builder.Build().RunAsync();
        return 0;
    }

    private static string Version() =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private const string Usage = """
        PolyMount.Mcp - the PolyMount control plane as an MCP server (stdio).

          --pipe <name>   Control pipe to talk to. Default: PolyMount.Control,
                          or POLYMOUNT_PIPE when the variable is set.
          --read-only     Offer only the operations that change nothing.
          --describe      Print the tool catalog as JSON and exit.
        """;

    private const string Instructions = """
        PolyMount exposes remote data sources as local drives. Start with list_instances: each row
        gives a mount point and says whether that instance is mounted. Files under a mount point
        are read and written with ordinary file tools; these tools control the mounts themselves.

        Read-only tools are always safe. The tools marked destructive — clear_cache,
        clear_local_changes, apply_layer_action, release_above and shutdown_service — discard data
        or take mounts down; use them only when the user asked for that.
        """;
}
