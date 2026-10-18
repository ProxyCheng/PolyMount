using System.IO;

namespace PolyMount.Core.Ipc;

/// <summary>
/// Where each instance is served, as the supervisor reports it, turned into a client a front-end
/// can talk to. Nothing is forwarded through the supervisor, which only knows the addresses; the
/// list is kept briefly rather than asked for on every call, because a page polling once a second
/// should not pay a second round-trip to ask where to send each one, and anything that changes the
/// set of instances says so through <see cref="Invalidate"/>.
///
/// Every answer is an instance's client. An instance the supervisor hosts itself — every one of
/// them, until each has a worker — and a name nothing is running under both come back addressed to
/// the supervisor's pipe: the first is where that instance is served, and the second is the
/// process that knows there is no such instance and says so.
/// </summary>
public static class InstanceDirectory
{
    /// <summary>How long one question may take. A local pipe answers in microseconds when it is
    /// there, and a window asking where its instance is lives on the thread that draws it.</summary>
    private const int AskTimeoutMs = 1000;

    private static readonly object Gate = new();
    private static List<InstanceEndpoint> _known = new();
    private static DateTime _fetchedUtc = DateTime.MinValue;
    private static DateTime _askedUtc = DateTime.MinValue;

    /// <summary>How long the list is believed. Long enough that polling does not re-ask where to
    /// send every poll, short enough that an instance started or stopped elsewhere is noticed on
    /// its own.</summary>
    public static TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often a name the list does not have may be asked about. A front-end that names
    /// something no instance answers to must not turn each poll into a question.</summary>
    public static TimeSpan MissTtl { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The client for the named instance, or one addressed to the supervisor when no
    /// instance answers to it. An instance is named by <c>MountProfile.Name</c> everywhere in the
    /// protocol, so that is what this takes.</summary>
    public static InstanceClient ClientFor(string? instance) => string.IsNullOrEmpty(instance)
        ? Unnamed()
        : Bound(instance, Search(list => list.FirstOrDefault(entry => string.Equals(entry.Name, instance, StringComparison.OrdinalIgnoreCase))));

    /// <summary>The client of the instance a local path belongs to, so a window opened on a file
    /// asks the process that owns it. A path under no mount point comes back addressed to the
    /// supervisor, which is where such a path's "not under any PolyMount mount point" belongs.</summary>
    public static InstanceClient ClientForPath(string? fullPath)
    {
        var endpoint = EndpointForPath(fullPath);
        return endpoint is null ? Unnamed() : Bound(endpoint.Name, endpoint);
    }

    /// <summary>The instance whose mount point contains the path, for a caller that needs to know
    /// whose it is rather than only how to reach it. The longest mount point wins: one mount can
    /// sit inside another, and the innermost one is the process that actually serves the file.
    ///
    /// It is the same rule the Explorer overlay applies to the same question, so a probe routed
    /// from a window and one routed from the shell cannot disagree about who owns a path.</summary>
    public static InstanceEndpoint? EndpointForPath(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return null;
        return Search(list => list
            .Where(entry => entry.MountPoint.Length > 0 && IsUnder(fullPath, entry.MountPoint))
            .OrderByDescending(entry => entry.MountPoint.TrimEnd('\\').Length)
            .FirstOrDefault());
    }

    /// <summary>Every instance the supervisor knows about, as it reports them. Empty when the
    /// service cannot be reached: a front-end showing nothing and an unreachable service is
    /// telling the truth, and this is not the place that reports why.</summary>
    public static List<InstanceEndpoint> All()
    {
        lock (Gate)
        {
            Refresh();
            return _known.ToList();
        }
    }

    /// <summary>Forgets what it knows so the next question is asked again. Called when something
    /// has just changed the set of instances: a saved configuration, a mount, an unmount.</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _fetchedUtc = DateTime.MinValue;
            _askedUtc = DateTime.MinValue;
        }
    }

    /// <summary>A client for one instance: the name it goes by and the pipe it is served on, which
    /// is the supervisor's while instances still live in the supervisor's process.</summary>
    private static InstanceClient Bound(string? instance, InstanceEndpoint? endpoint) => new(
        string.IsNullOrEmpty(instance) ? null : instance,
        endpoint is { Pipe.Length: > 0 } ? endpoint.Pipe : SupervisorClient.Default.PipeName);

    /// <summary>A client that names no instance: for the questions that are about a path rather
    /// than about an instance, which the supervisor answers for a path under none of its mounts.
    /// Nothing that names an instance may be sent through it.</summary>
    private static InstanceClient Unnamed() => Bound(null, null);

    private static InstanceEndpoint? Search(Func<List<InstanceEndpoint>, InstanceEndpoint?> find)
    {
        lock (Gate)
        {
            bool fresh = DateTime.UtcNow - _fetchedUtc < Ttl;
            var found = find(_known);
            if (!fresh && DateTime.UtcNow - _askedUtc >= MissTtl)
            {
                Refresh();
                // A miss is what a new instance looks like before the list is re-read; one that is
                // still nowhere to be found keeps the address it had, which is either still right
                // (a pipe is named for an identity that never changes) or a connection that fails
                // where the instance used to be.
                found = find(_known) ?? found;
            }
            return found;
        }
    }

    /// <summary>Re-reads the list. A service that cannot be reached keeps the last one: a
    /// front-end then fails against the addresses it has rather than quietly sending an instance's
    /// request to a process that does not host it.
    ///
    /// Asking is bounded tightly — inside a second — because a window resolves its instance while
    /// it is being put together, on the thread that draws it: a pipe that is there accepts at once,
    /// so waiting seconds only delays the window that asked. A service too busy to answer in that
    /// time is asked again a moment later, which is what <see cref="MissTtl"/> is for.</summary>
    private static void Refresh()
    {
        _askedUtc = DateTime.UtcNow;
        try
        {
            var response = new SupervisorClient(SupervisorClient.Default.PipeName)
            {
                ConnectTimeoutMs = AskTimeoutMs,
                ResponseTimeoutMs = AskTimeoutMs
            }.Instances();
            _known = response.Instances;
            _fetchedUtc = DateTime.UtcNow;
        }
        catch (Exception)
        {
            // Keep what is known; the call that needed the answer reports the failure itself.
        }
    }

    /// <summary>Whether a local path is a mount point or inside it. Lexical only: nothing here
    /// touches the file system, so a path that does not exist yet is still under the mount that
    /// would hold it.</summary>
    public static bool IsUnder(string fullPath, string mountPoint)
    {
        string full = Path.GetFullPath(fullPath).TrimEnd('\\');
        string root = Path.GetFullPath(mountPoint).TrimEnd('\\');
        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
