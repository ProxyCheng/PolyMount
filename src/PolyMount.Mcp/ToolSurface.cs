using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using PolyMount.Core.Ipc;

namespace PolyMount.Mcp;

/// <summary>
/// The operation catalog, turned into something an MCP client can list and call.
///
/// It is deliberately thin: names, descriptions, schemas and annotations all come from
/// <see cref="ControlOperations"/>, and execution is one delegate call. A tool surface that grew
/// its own copy of what each operation means would be a fourth place to keep in step with the
/// service, which is the thing this design exists to avoid.
/// </summary>
public sealed class ToolSurface
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>How many control calls may be in flight at once. The pipe client blocks a thread
    /// for the whole round trip, and several operations fan out to every instance on top of that;
    /// a burst of tool calls must not turn into thread-pool starvation for the protocol loop.</summary>
    private static readonly SemaphoreSlim Calls = new(8);

    private readonly IControlLink _link;
    private readonly string _pipeName;
    private readonly List<ToolEntry> _tools;

    public ToolSurface(IControlLink link, string pipeName, bool readOnly)
    {
        _link = link;
        _pipeName = pipeName;
        _tools = ControlOperations.All
            .Where(operation => !readOnly || operation.Effect == ControlEffect.Read)
            // Clone: the document is disposed, and the schema outlives it.
            .Select(operation => new ToolEntry(operation, JsonDocument.Parse(operation.InputSchema).RootElement.Clone()))
            .ToList();
    }

    /// <summary>The tools this process offers, as the protocol wants them.</summary>
    public IList<Tool> Tools() => _tools.Select(entry => new Tool
    {
        Name = entry.Operation.Name,
        Title = entry.Operation.Title,
        Description = entry.Operation.Description,
        InputSchema = entry.Schema,
        Annotations = new ToolAnnotations
        {
            Title = entry.Operation.Title,
            ReadOnlyHint = entry.Operation.Effect == ControlEffect.Read,
            DestructiveHint = entry.Operation.Effect == ControlEffect.Destructive,
            IdempotentHint = entry.Operation.Idempotent,
            // Nothing here reaches a system this process does not already know: the mounts are
            // local, and every endpoint is a pipe on this machine.
            OpenWorldHint = false
        }
    }).ToList();

    /// <summary>Runs one call. Every failure — an unknown tool, a bad argument, an unreachable
    /// service — comes back as a tool error rather than an exception: a model can read a sentence
    /// about what to check next, and cannot read a stack trace.</summary>
    public async ValueTask<CallToolResult> CallAsync(CallToolRequestParams parameters, CancellationToken cancellationToken)
    {
        string name = parameters.Name ?? "";
        var tool = _tools.FirstOrDefault(entry => string.Equals(entry.Operation.Name, name, StringComparison.Ordinal));
        if (tool is null) return Error($"unknown tool '{name}'");

        ToolArguments arguments;
        try
        {
            arguments = new ToolArguments(JsonSerializer.SerializeToElement(
                parameters.Arguments ?? new Dictionary<string, JsonElement>()));
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }

        try
        {
            // The control client blocks on a named pipe. Off the protocol loop, so one slow
            // request (a mount, a cache clear) does not hold up the others a client may be making.
            await Calls.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var response = await Task.Run(() => tool.Operation.Invoke(_link, arguments), cancellationToken)
                    .ConfigureAwait(false);

                if (!response.Ok)
                    return Error(ControlOperations.Explain(response, InstanceName(arguments)));

                return Text(JsonSerializer.Serialize(tool.Operation.Present(response), Json));
            }
            finally
            {
                Calls.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message);
        }
        catch (Exception ex)
        {
            return Error(ControlOperations.Explain(ex, _pipeName));
        }
    }

    /// <summary>The catalog as a document, for `--describe` and for documentation generators.</summary>
    public string Describe() => JsonSerializer.Serialize(new
    {
        server = "polymount",
        tools = _tools.Select(entry => new
        {
            name = entry.Operation.Name,
            title = entry.Operation.Title,
            description = entry.Operation.Description,
            effect = entry.Operation.Effect.ToString().ToLowerInvariant(),
            idempotent = entry.Operation.Idempotent,
            inputSchema = JsonNode.Parse(entry.Schema.GetRawText())
        })
    }, new JsonSerializerOptions { WriteIndented = true });

    private static string? InstanceName(ToolArguments arguments)
    {
        try { return arguments.Has("instance") ? arguments.OptionalString("instance") : null; }
        catch (ArgumentException) { return null; }
    }

    private static CallToolResult Text(string text) => new()
    {
        Content = new List<ContentBlock> { new TextContentBlock { Text = text } }
    };

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = new List<ContentBlock> { new TextContentBlock { Text = message } }
    };

    private sealed record ToolEntry(ControlOperation Operation, JsonElement Schema);
}
