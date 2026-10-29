namespace PolyMount.Plugins.Ipc;

public enum ControlEffect
{
    Read,
    Write,
    Destructive
}

/// <summary>
/// One operation of the control plane, described once and offered to every front-end.
///
/// The pipe protocol is a bag of fields keyed by a command string, which is right for a transport
/// and wrong for an API: it says nothing about which arguments a command needs, which arguments
/// are meaningful together, or what the caller will find on the other side. This record is the
/// answer to those questions — a stable name, a description written for someone who has never
/// read the service, a JSON Schema for its arguments, what kind of change it makes, and the
/// projection of the reply (a <see cref="ControlResponse"/> is a union of every command's result;
/// an operation narrows it to the one field it produced).
///
/// <see cref="ControlOperations"/> is the catalog. A front-end that offers all of it (the MCP
/// server does) needs to know nothing about the commands themselves.
/// </summary>
public sealed record ControlOperation(
    string Name,
    string Title,
    string Description,
    ControlEffect Effect,
    bool Idempotent,
    string InputSchema,
    Func<IControlLink, ToolArguments, ControlResponse> Invoke,
    Func<ControlResponse, object?> Present);
