namespace PolyMount.Core.Ipc;

/// <summary>
/// One instance: its own pipe, its own process, and the questions only it can answer — what its
/// layers make of a path, what its recording is counting, what its source is pinned to.
///
/// The instance's name travels with the client and is filled into every request, because the
/// process that answers may serve more than one: the supervisor does, for as long as instances
/// live in its process. A client that named no instance could otherwise be answered about
/// whichever one happened to be first.
/// </summary>
public sealed class InstanceClient : ControlClient
{
    public InstanceClient(string? instance, string? pipeName = null) : base(pipeName)
        => Instance = instance;

    /// <summary>The instance this client is for, by the name the protocol uses everywhere.</summary>
    public string? Instance { get; }

    protected override ControlRequest Prepare(ControlRequest request)
    {
        request.Name ??= Instance;
        return request;
    }

    /// <summary>Pins the source to a revision. A pinned instance keeps answering from that
    /// revision until it is refreshed, whatever the repository does.</summary>
    public ControlResponse SetPin(long revision, string? layer = null)
        => Send(new ControlRequest { Cmd = ControlCommands.SetPin, Rev = revision, Layer = layer });

    /// <summary>Pins the source to whatever its head is now.</summary>
    public ControlResponse RefreshHead(string? layer = null)
        => Send(new ControlRequest { Cmd = ControlCommands.RefreshHead, Layer = layer });

    /// <summary>Drops what the cacheable layers hold, so the next read goes to the source.</summary>
    public ControlResponse ClearCache(string? layer = null)
        => Send(new ControlRequest { Cmd = ControlCommands.ClearCache, Layer = layer });

    /// <summary>Discards what this instance holds over the source — the layer's own records, not
    /// the source: what the source holds is not this program's to delete.</summary>
    public ControlResponse ClearLocalChanges(string layer)
        => Send(new ControlRequest { Cmd = ControlCommands.ClearLocalChanges, Layer = layer });

    /// <summary>What each layer of this instance makes of one local path: whether it holds it,
    /// whether it hides it, and which one wins.</summary>
    public ControlResponse Probe(string fullPath)
        => Send(new ControlRequest { Cmd = ControlCommands.Probe, Path = fullPath });

    /// <summary>Changes what one layer of this instance says about a path, and answers with the
    /// probe as it stands after the change.</summary>
    public ControlResponse LayerAction(string fullPath, string layer, string action)
        => Send(new ControlRequest { Cmd = ControlCommands.LayerAction, Path = fullPath, Layer = layer, Action = action });

    /// <summary>Lets every manageable layer above the boundary stop answering for a path, so the
    /// ones below it serve it again. The expected layer names are checked first: a chain that
    /// changed since it was looked at is not one to cut through.</summary>
    public ControlResponse ReleaseAbove(string fullPath, int boundary, IReadOnlyList<string> expectedLayers)
        => Send(new ControlRequest
        {
            Cmd = ControlCommands.ReleaseAbove,
            Path = fullPath,
            Boundary = boundary,
            ExpectedLayers = expectedLayers.ToList()
        });

    /// <summary>Attaches the recording channel. Idempotent: an instance already recording returns
    /// the session it is running rather than starting a second one.</summary>
    public ControlResponse RecordingStart(string? label = null)
        => Send(new ControlRequest { Cmd = ControlCommands.RecordingStart, Label = label });

    /// <summary>How the recording stands, and what it has counted.</summary>
    public ControlResponse RecordingStatus()
        => Send(new ControlRequest { Cmd = ControlCommands.RecordingStatus });

    /// <summary>Takes the recording down, keeping what it counted.</summary>
    public ControlResponse RecordingStop(string reason = "stopped")
        => Send(new ControlRequest { Cmd = ControlCommands.RecordingStop, Reason = reason });
}
