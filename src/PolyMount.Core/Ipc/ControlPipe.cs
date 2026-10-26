using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PolyMount.Core.Ipc;

/// <summary>How every PolyMount control pipe is created.
///
/// A named pipe created without a security descriptor gets the Win32 default: full control for
/// SYSTEM, administrators and the creator owner, and read-only for everyone else. That default is
/// written for a pipe whose client is the same account as its server, and PolyMount is not that
/// shape once it is installed: the service runs under an account of its own (LocalService unless
/// the setup was told to use a Windows user account), while the tray, an agent's MCP server and
/// the Explorer overlay run under the signed-in user. The default gives that user read access and
/// refuses the duplex open a control client asks for, which reaches the caller as "Access to the
/// path is denied." — a service that is running and answering, that nothing can talk to.
///
/// So the descriptor names the accounts that may drive the control plane — the ones the setup
/// wrote down (<see cref="ControlAccess"/>) — instead of the whole interactive session. The
/// listening account keeps full control because creating the next instance per connection is an
/// access check of its own; administrators and SYSTEM keep the platform default; and a network
/// logon carries neither SID, so an SMB client on another machine was never in it.
///
/// A policy with no accounts configured keeps the old interactive rule, and the supervisor says so
/// in its log and in its status (see <see cref="ControlAccess.Warning"/>). Turning a working
/// installation unreachable on upgrade is a worse failure than the hole it closes, and the
/// installer writes a real list the next time it runs.</summary>
public static class ControlPipe
{
    /// <summary>The rules every server instance of a control pipe is created with.</summary>
    public static PipeSecurity Security(ControlAccess access)
    {
        var security = new PipeSecurity();
        security.SetAccessRule(Rule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl));
        security.SetAccessRule(Rule(Sid(WellKnownSidType.LocalSystemSid), PipeAccessRights.FullControl));
        security.SetAccessRule(Rule(Sid(WellKnownSidType.BuiltinAdministratorsSid), PipeAccessRights.FullControl));

        if (!access.IsOpen)
        {
            // A SID that does not parse is left off the pipe rather than failing it: control over
            // who may connect must not be lost because one entry in the list is a typo. The
            // client that entry was meant for is refused, which shows up immediately and names
            // the account in the log.
            foreach (string sid in access.Allowed)
                if (Parse(sid) is { } parsed)
                    security.SetAccessRule(new PipeAccessRule(parsed, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            return security;
        }

        // The interactive session is what lets a Windows service be reached by the tray and the
        // Explorer overlay when no account list exists. An elevated server running inside an
        // interactive session already grants the account it runs under (above) and the rule would
        // only add the other users logged on to this machine — the control plane can point a layer
        // at any path, which would hand them what they cannot read themselves. A service in
        // session 0 has no interactive account of its own, so it keeps the rule: that caller is
        // the one the product is built around.
        bool serviceSession = Process.GetCurrentProcess().SessionId == 0;
        if (!Elevation.IsElevated || serviceSession)
            security.SetAccessRule(Rule(Sid(WellKnownSidType.InteractiveSid), PipeAccessRights.ReadWrite));
        return security;
    }

    /// <summary>A listening pipe under the rules above. Details are defaulted: the transmission
    /// mode and options are the ones <see cref="NamedPipeServerStream"/> would have used.</summary>
    public static NamedPipeServerStream CreateServer(string pipeName, ControlAccess access) =>
        NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            Security(access));

    private static SecurityIdentifier? Parse(string sid)
    {
        try { return new SecurityIdentifier(sid); }
        catch (Exception) { return null; }
    }

    private static PipeAccessRule Rule(IdentityReference? identity, PipeAccessRights rights) =>
        new(identity ?? throw new InvalidOperationException("The control pipe cannot be secured without knowing who runs it"),
            rights, AccessControlType.Allow);

    private static SecurityIdentifier Sid(WellKnownSidType type) => new(type, null);
}
