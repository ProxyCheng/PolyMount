namespace PolyMount.Core.Ipc;

public sealed class PipeControlLink : IControlLink
{
    public static PipeControlLink Default { get; } = new();

    public ControlResponse Supervisor(ControlRequest request) => SupervisorClient.Default.Send(request);

    public ControlResponse Instance(string? name, ControlRequest request) =>
        AgainAfterStaleDirectory(() => InstanceDirectory.ClientFor(name), request);

    public ControlResponse ForPath(string fullPath, ControlRequest request) =>
        AgainAfterStaleDirectory(() => InstanceDirectory.ClientForPath(fullPath), request);

    /// <summary>Sends a command to the process that should serve it, and once more if the
    /// supervisor was the one that answered "that is an instance's question".
    ///
    /// That answer means the request went to the supervisor because the directory had not yet
    /// heard of the instance — its list is believed for five seconds, and an instance that was
    /// enabled, mounted or restarted a moment ago is not in it. A caller that enables an instance
    /// and immediately asks about a path under it is the ordinary case, not an exotic one: it
    /// would be told "not running" about something that is running. Nothing executed the request
    /// (the supervisor does not answer for an instance), so asking again after forgetting the list
    /// is safe for a write as well as for a read.</summary>
    private static ControlResponse AgainAfterStaleDirectory(Func<InstanceClient> client, ControlRequest request)
    {
        var response = client().Send(request);
        if (response.Ok || response.Error is not { } error ||
            !ControlOperations.IsNotRunning(error)) return response;
        InstanceDirectory.Invalidate();
        return client().Send(request);
    }

    public string? OwnerOf(string fullPath)
    {
        // Asked of the supervisor, not the directory: the directory knows only the instances that
        // are running, so a path under an instance that is stopped would be reported as belonging
        // to nobody. The supervisor knows every configured instance and its mount point, which is
        // the question here — whether the path is under a mount, not whether it can be served
        // right now. Serving it is the next call's problem, and it has its own error to say.
        var response = SupervisorClient.Default.Status();
        if (!response.Ok) throw new IOException(response.Error ?? "the control service did not answer");

        return response.Mounts
            .Where(mount => mount.MountPoint.Length > 0 && InstanceDirectory.IsUnder(fullPath, mount.MountPoint))
            .OrderByDescending(mount => mount.MountPoint.TrimEnd('\\').Length)
            .Select(mount => mount.Name)
            .FirstOrDefault();
    }

    public ControlResponse Status()
    {
        var response = InstanceView.Status();

        // The merged view turns an unreachable supervisor into an error string rather than an
        // exception, because its other callers draw a window that can say so. A caller of this
        // interface is owed the failure itself, so the operation layer can say which pipe to
        // check instead of repeating "The operation has timed out".
        if (!response.Ok) throw new IOException(response.Error ?? "the control service did not answer");
        return response;
    }

    public List<MountMetrics> Metrics(double windowSeconds, string? name)
    {
        if (!string.IsNullOrEmpty(name))
            return InstanceDirectory.ClientFor(name).Metrics(windowSeconds, name).Metrics;

        // No instance list can mean either "nothing is running" or "the service could not be
        // asked", and an empty metrics array is only an answer to the first. Ask the supervisor
        // when the list is empty so the second case surfaces as the failure it is.
        if (InstanceDirectory.All().Count == 0)
        {
            var response = SupervisorClient.Default.Status();
            if (!response.Ok) throw new IOException(response.Error ?? "the control service did not answer");
            return new List<MountMetrics>();
        }

        return InstanceView.Metrics(windowSeconds);
    }
}
