using System.Security.Principal;

namespace PolyMount.Core.Ipc;

/// <summary>Which accounts may drive this machine's control plane.
///
/// The control pipe is the one place where a caller can point a layer at any path, replace the
/// configuration, create and delete stored credentials and stop the service. It used to be
/// granted to everything in an interactive session, on the reasoning that the tray, the Explorer
/// overlay and an agent's MCP server all run in one — which is also every standard local user,
/// so "same machine" was standing in for "authorized".
///
/// This is the authorization instead: the accounts the setup wrote down, by SID rather than by
/// name (a name is resolved against whoever asks, a SID is the identity), granted on the pipe
/// itself so the operating system refuses everybody else before a request is read. The list is
/// seeded by the installer from the account that ran it, and by a service started from an
/// interactive session from the account that started it.
///
/// An empty list is not "nobody": a configuration written before this existed, or one created by
/// the service account in session 0 (which cannot know who the user is), keeps the old
/// interactive grant so a working installation does not become unreachable after an upgrade.
/// That state is reported by <see cref="Warning"/> rather than passed over in silence, and the
/// installer writes a real list on the next run.</summary>
public sealed class ControlAccess
{
    private readonly string[] _allowed;

    public ControlAccess(IEnumerable<string>? allowedSids)
    {
        _allowed = (allowedSids ?? Array.Empty<string>())
            .Where(sid => !string.IsNullOrWhiteSpace(sid))
            .Select(sid => sid.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>The accounts this configuration names, as SID strings.</summary>
    public IReadOnlyList<string> Allowed => _allowed;

    /// <summary>No list is configured, so the old interactive grant stands.</summary>
    public bool IsOpen => _allowed.Length == 0;

    /// <summary>Whether this SID is one of the named accounts. It does not answer for
    /// administrators or the service account: those two are granted by the pipe descriptor and
    /// checked by the caller separately, so this stays a plain membership test.</summary>
    public bool Allows(string? sid) =>
        IsOpen || (sid is not null && _allowed.Contains(sid, StringComparer.OrdinalIgnoreCase));

    /// <summary>Says what is wrong with this policy, or null when nothing is. It is what the
    /// supervisor logs at start and reports in its status, so an open control plane is something
    /// a reader can see rather than something they have to know to look for.</summary>
    public string? Warning => IsOpen
        ? "The control pipe is open to every interactive session: no ControlUsers list is configured, so any "
          + "standard local user can change this service's configuration and read the accounts it holds. Run the "
          + "installer, or add the accounts that may drive it to ControlUsers."
        : null;

    /// <summary>The account this process runs as, which the pipe will always accept: it is the
    /// one that creates the next server instance per connection.</summary>
    public static string CurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? "";
    }

    /// <summary>The account name behind a SID, for the installer, the log and the plugin-free
    /// view of the list. A SID that no longer resolves is shown as itself: an account that was
    /// deleted from the domain is still a SID the file names.</summary>
    public static string Describe(string sid)
    {
        try { return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value; }
        catch (Exception) { return sid; }
    }

    /// <summary>Whether a string is a SID this policy could hold. Configuration validation asks
    /// this so a typo is refused where it is written rather than becoming a pipe rule that
    /// silently never matches.</summary>
    public static bool IsWellFormed(string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid)) return false;
        try { _ = new SecurityIdentifier(sid.Trim()); return true; }
        catch (Exception) { return false; }
    }
}
