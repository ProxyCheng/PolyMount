namespace PolyMount.Plugins.Ipc;

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
