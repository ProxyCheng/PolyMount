using System.Security.AccessControl;
using System.Security.Principal;

namespace PolyMount.Core;

/// <summary>Who may read a store directory.
///
/// The stores hold the cached content of whatever the service can read and the overlay of
/// everything written through the mounts, and they live under <c>%PROGRAMDATA%</c> unless the
/// configuration puts them elsewhere. That folder hands its children
/// <c>BUILTIN\Users:(OI)(CI)(RX)</c> and lets users create entries in it, so a store inherits read
/// access for every local account — the same accounts the control plane was opened to, reading a
/// repository they cannot open themselves.
///
/// The fix is at the directory rather than at each file: inheritance is broken once, and the
/// accounts that actually use a store are named explicitly — the service account that writes it,
/// SYSTEM, and administrators. Every file and folder created under it afterwards inherits that,
/// including the ones a store database creates for its blobs.
///
/// A directory this cannot be applied to — a store on a share, on a volume without ACLs, or one
/// somebody else already owns and this account cannot rewrite — is left as it is. That is a
/// weaker store, not a broken mount, and the alternative is a service that will not start because
/// its cache volume is exFAT.</summary>
internal static class StoreAcl
{
    public static void Harden(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            if (!info.Exists) return;
            var security = new DirectorySecurity();
            // Inheritance off, and the inherited entries dropped: keeping them is what would keep
            // the Users:(RX) rule that this exists to remove.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            Add(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
            Add(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            using (var identity = WindowsIdentity.GetCurrent())
                if (identity.User is { } user) Add(security, user);

            info.SetAccessControl(security);
        }
        catch (Exception)
        {
            // Left alone on purpose; see the summary above.
        }
    }

    private static void Add(DirectorySecurity security, IdentityReference identity) =>
        security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
}
