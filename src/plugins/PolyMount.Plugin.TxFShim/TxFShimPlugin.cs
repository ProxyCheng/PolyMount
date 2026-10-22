using System.Text.Json;
using System.Text.Json.Nodes;
using PolyMount.Core;
using PolyMount.Core.Ipc;

namespace PolyMount.Plugins.TxFShim;

/// <summary>
/// Lets the programs that need it open files without a transaction, so they work on a volume that
/// has no transaction support.
///
/// The problem it answers is recorded in <c>docs/txf-shim.md</c>: a volume is allowed to accept a
/// transactional open only if its device object says it may, WinFsp's does not, and the two ways to
/// change that — a signed driver of our own, or a WinFsp change — were both refused. What is left
/// is to intercept the call inside the process that makes it, which is what the native half does;
/// this half decides which processes those are and tells Windows to start them through the shim.
///
/// Enabling the plugin installs nothing. It only reports, discovers and writes when asked: the
/// installer decides whether this plugin exists on a machine, but injecting into a program is a
/// second decision, and it is made per directory by <c>install_txf_shim</c>.
/// </summary>
public sealed class TxFShimPlugin : IPolyMountPlugin
{
    /// <summary>The command an instance's pipe answers. The operations below are the front-end side
    /// of the same answers, and both live here so they cannot drift.</summary>
    internal const string Command = "txf.shim";

    internal const int DefaultLogLines = 200;

    public void Register(IPluginContext context)
    {
        context.RegisterCommand(Command, command =>
        {
            // The line count travels in Boundary: the wire has one integer slot, and nothing else
            // in this plugin's commands needs it.
            object answer = (command.Request.Action ?? "status").ToLowerInvariant() switch
            {
                "status" => Service.Status(),
                "scan" => Service.Scan(command.Request.Path ?? throw new ArgumentException("scan needs a path")),
                "install" => Service.Install(command.Request.Path ?? throw new ArgumentException("install needs a path")),
                "remove" => Service.Remove(command.Request.Path, null),
                "log" => Service.Log(command.Request.Boundary <= 0 ? DefaultLogLines : command.Request.Boundary),
                var action => throw new ArgumentException($"unknown action '{action}'")
            };
            command.Response.Result = JsonSerializer.Serialize(answer, TxFShimService.SerializerOptions);
        });

        context.RegisterControlOperation(new ControlOperation(
            Name: "get_txf_shim",
            Title: "Get transaction shim",
            Description: "What the transactional-open shim has instrumented on this machine: which images are started through it, whether the native shim it points at is deployed, disagreements between the record and the registry, and the size of the hit log. Disagreements are reported, never repaired.",
            Effect: ControlEffect.Read,
            Idempotent: true,
            InputSchema: """{"type":"object","properties":{"instance":{"type":"string","description":"Instance name, as list_instances reports it. The command runs in that instance's process, but what it describes is machine-wide."}},"required":["instance"]}""",
            Invoke: (link, arguments) => link.Instance(arguments.String("instance"),
                new ControlRequest { Cmd = Command, Action = "status" }),
            Present: Project));

        context.RegisterControlOperation(new ControlOperation(
            Name: "list_txf_shim_targets",
            Title: "List transaction shim targets",
            Description: "Which executables under a directory would be started through the shim, and why: the ones that import a transactional file API themselves, and the ones that load a DLL beside them which does. Writes nothing — use it to see an install before making it.",
            Effect: ControlEffect.Read,
            Idempotent: true,
            InputSchema: """{"type":"object","properties":{"instance":{"type":"string","description":"Instance name, as list_instances reports it."},"root":{"type":"string","description":"Directory holding the program's executables, for example an engine's Binaries\\Win64. Only the top level is scanned."}},"required":["instance","root"]}""",
            Invoke: (link, arguments) => link.Instance(arguments.String("instance"),
                new ControlRequest { Cmd = Command, Action = "scan", Path = arguments.String("root") }),
            Present: Project));

        context.RegisterControlOperation(new ControlOperation(
            Name: "install_txf_shim",
            Title: "Install transaction shim",
            Description: "Starts every executable under a directory that reaches a transactional file API through the shim, by writing an Image File Execution Options Debugger value for it. Refuses when the native shim is not deployed, and refuses any image whose Debugger value already points at something else. Processes already running keep their behaviour until restarted.",
            Effect: ControlEffect.Write,
            Idempotent: true,
            InputSchema: """{"type":"object","properties":{"instance":{"type":"string","description":"Instance name, as list_instances reports it."},"root":{"type":"string","description":"Directory holding the program's executables. Only the top level is scanned."}},"required":["instance","root"]}""",
            Invoke: (link, arguments) => link.Instance(arguments.String("instance"),
                new ControlRequest { Cmd = Command, Action = "install", Path = arguments.String("root") }),
            Present: Project));

        context.RegisterControlOperation(new ControlOperation(
            Name: "remove_txf_shim",
            Title: "Remove transaction shim",
            Description: "Undoes install_txf_shim for one image, or for every image under one root: the Debugger value this plugin wrote is deleted and the record dropped. A value that no longer points at this plugin is left alone. Processes already running keep the shim until restarted.",
            Effect: ControlEffect.Write,
            Idempotent: true,
            InputSchema: """{"type":"object","properties":{"instance":{"type":"string","description":"Instance name, as list_instances reports it."},"root":{"type":"string","description":"Directory whose instrumented images should be removed, as it was given to install."},"image":{"type":"string","description":"One executable, by name or full path, as get_txf_shim lists it under installed[].imageName. Give this or root."}},"required":["instance"]}""",
            Invoke: (link, arguments) => link.Instance(arguments.String("instance"),
                new ControlRequest
                {
                    Cmd = Command,
                    Action = "remove",
                    // The image travels in Path: Name is the instance on this wire.
                    Path = arguments.OptionalString("root") ?? arguments.OptionalString("image")
                }),
            Present: Project));

        context.RegisterControlOperation(new ControlOperation(
            Name: "read_txf_shim_log",
            Title: "Read transaction shim log",
            Description: "The latest calls the shim intercepted: time, process id, executable, the API, the path and the access it asked for. An empty log after a failed save means the program was not started through the shim, not that the calls succeeded.",
            Effect: ControlEffect.Read,
            Idempotent: true,
            InputSchema: """{"type":"object","properties":{"instance":{"type":"string","description":"Instance name, as list_instances reports it."},"lines":{"type":"integer","description":"How many of the most recent lines to return. Default 200."}},"required":["instance"]}""",
            Invoke: (link, arguments) => link.Instance(arguments.String("instance"),
                new ControlRequest
                {
                    Cmd = Command,
                    Action = "log",
                    Boundary = arguments.Has("lines") ? (int)arguments.Long("lines") : 0
                }),
            Present: Project));
    }

    /// <summary>The state lives beside the machine's configuration, not inside it: it records what
    /// this plugin wrote, and the registry remains the answer to what is actually instrumented.</summary>
    private static TxFShimService Service { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PolyMount", "txf-shim"),
        new RegistryImageFileExecutionOptions());

    private static JsonNode Project(ControlResponse response) =>
        JsonNode.Parse(string.IsNullOrWhiteSpace(response.Result) ? "{}" : response.Result)!;
}
