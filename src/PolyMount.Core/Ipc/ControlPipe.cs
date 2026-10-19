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
/// The pipe is the trust boundary (there is no other authentication), and the callers are the
/// machine's interactive sessions, so the descriptor below says exactly that: the account that
/// listens can create further instances (one per connection, which is an access check of its
/// own), and the interactive session can read and write. Nothing else is added — a network logon
/// does not carry the INTERACTIVE SID, so granting it does not hand the control plane to an SMB
/// client on another machine.</summary>
public static class ControlPipe
{
    /// <summary>The rules every server instance of a control pipe is created with.</summary>
    public static PipeSecurity Security()
    {
        var security = new PipeSecurity();
        security.SetAccessRule(Rule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl));
        security.SetAccessRule(Rule(Sid(WellKnownSidType.LocalSystemSid), PipeAccessRights.FullControl));
        security.SetAccessRule(Rule(Sid(WellKnownSidType.BuiltinAdministratorsSid), PipeAccessRights.FullControl));
        // The interactive session is what lets a Windows service be reached by the tray and the
        // Explorer overlay. An elevated server running inside an interactive session already
        // grants the account it runs under (above) and the interactive rule would only add the
        // other users logged on to this machine — the control plane can point a layer at any
        // path, which would hand them what they cannot read themselves. A service in session 0
        // has no interactive account of its own, so it keeps the rule: that caller is the one
        // the product is built around.
        bool serviceSession = Process.GetCurrentProcess().SessionId == 0;
        if (!Elevation.IsElevated || serviceSession)
            security.SetAccessRule(Rule(Sid(WellKnownSidType.InteractiveSid), PipeAccessRights.ReadWrite));
        return security;
    }

    /// <summary>A listening pipe under the rules above. Details are defaulted: the transmission
    /// mode and options are the ones <see cref="NamedPipeServerStream"/> would have used.</summary>
    public static NamedPipeServerStream CreateServer(string pipeName) =>
        NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            Security());

    private static PipeAccessRule Rule(IdentityReference? identity, PipeAccessRights rights) =>
        new(identity ?? throw new InvalidOperationException("The control pipe cannot be secured without knowing who runs it"),
            rights, AccessControlType.Allow);

    private static SecurityIdentifier Sid(WellKnownSidType type) => new(type, null);
}
