namespace PolyMount.Core.Ipc;

/// <summary>
/// The control plane, addressed by what the caller wants to do rather than by which process
/// answers it.
///
/// The pipe protocol has two kinds of endpoint — the supervisor, which owns the configuration and
/// the instance list, and each instance's worker, which knows only itself — and a caller that
/// names an instance has no business knowing which one will answer. Everything about the instance
/// goes through <see cref="Instance"/>, which resolves the address the instance is served on; the
/// questions only the supervisor can answer go through <see cref="Supervisor"/>; and a path is
/// routed to the instance whose mount point contains it by <see cref="ForPath"/>.
///
/// It exists as an interface so the operations built on it can be tested without pipes, and so a
/// second front-end (an HTTP endpoint, a CLI) can share the same operation catalog without
/// sharing a transport.
/// </summary>
public interface IControlLink
{
    /// <summary>The process that owns the configuration and the instance list.</summary>
    ControlResponse Supervisor(ControlRequest request);

    /// <summary>The instance's own process, or the supervisor when that instance is not running —
    /// which then answers "not running" rather than being unreachable.</summary>
    ControlResponse Instance(string? name, ControlRequest request);

    /// <summary>The instance whose mount point contains the path.</summary>
    ControlResponse ForPath(string fullPath, ControlRequest request);

    /// <summary>Which instance owns a local path, or null when no mount point contains it. Asked
    /// before a path operation so the caller gets "no instance owns this path" rather than the
    /// supervisor's report that the command belongs to some other process.</summary>
    string? OwnerOf(string fullPath);

    /// <summary>Every instance, with the supervisor's and the worker's halves put together.</summary>
    ControlResponse Status();

    /// <summary>Read analytics for one instance, or for all of them when no name is given.</summary>
    List<MountMetrics> Metrics(double windowSeconds, string? name);
}

/// <summary>The control plane over the real pipes: the existing clients, put behind one interface.</summary>
public sealed class PipeControlLink : IControlLink
{
    public static PipeControlLink Default { get; } = new();

    public ControlResponse Supervisor(ControlRequest request) => SupervisorClient.Default.Send(request);

    public ControlResponse Instance(string? name, ControlRequest request) =>
        InstanceDirectory.ClientFor(name).Send(request);

    public ControlResponse ForPath(string fullPath, ControlRequest request) =>
        InstanceDirectory.ClientForPath(fullPath).Send(request);

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
