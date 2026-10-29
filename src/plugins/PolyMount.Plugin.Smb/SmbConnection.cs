using System.ComponentModel;
using System.Runtime.InteropServices;
using PolyMount.Plugins;

namespace PolyMount.Plugins.Smb;

/// <summary>
/// The SMB session this process uses to reach one share.
///
/// A session is per process, and the service has none of its own: it runs in session 0, where
/// neither the signed-in user's mapped drives nor the connections <c>net use</c> made in their
/// own session are visible. Left alone, Windows would authenticate as whatever account the
/// service runs under — and <c>LocalSystem</c> reaches another machine as the *machine* account
/// (<c>PC$</c>), which is almost never the account somebody meant when they picked a credential.
/// So a configured account is presented here, once, when the instance is built; the redirector
/// keeps the session for this process, and serving a request never touches this type again.
/// </summary>
internal sealed class SmbConnection : IDisposable
{
    private const int ResourceTypeDisk = 1;
    /// <summary>Do not remember the connection across logons: this process is its only user,
    /// and a remembered one would outlive the credential that made it.</summary>
    private const int ConnectTemporary = 0x00000004;

    private const int ErrorAccessDenied = 5;
    private const int ErrorBadNetPath = 53;
    private const int ErrorUnexpectedNetError = 59;
    private const int ErrorNetNameDeleted = 64;
    private const int ErrorBadNetName = 67;
    private const int ErrorAlreadyAssigned = 85;
    private const int ErrorConnectionLost = 1203;
    private const int ErrorSessionCredentialConflict = 1219;
    private const int ErrorNetworkUnreachable = 1231;
    private const int ErrorHostUnreachable = 1232;
    private const int ErrorLogonFailure = 1326;
    private const int ErrorLogonTypeNotGranted = 1385;

    private readonly object _gate = new();
    private readonly string _share;
    private readonly string _username;
    private readonly string _password;
    private bool _connected;
    private long _connections;

    public SmbConnection(string share, string username = "", string password = "")
    {
        _share = share;
        _username = username ?? string.Empty;
        _password = password ?? string.Empty;
    }

    public string Share => _share;

    /// <summary>The account presented, or empty when the process's own identity is used. Only
    /// ever a name: the password stays in this object.</summary>
    public string Account => _username;

    /// <summary>Whether a session this layer established is in place. A layer with no account is
    /// served by whatever the process already holds, which this object cannot see.</summary>
    public bool Established { get { lock (_gate) return _connected; } }

    /// <summary>Sessions established after the first one: reconnections, plus the first.</summary>
    public long Sessions => Interlocked.Read(ref _connections);

    public void Ensure()
    {
        lock (_gate) EnsureLocked();
    }

    /// <summary>Drops the session and establishes it again, for a operation that failed the way a
    /// dropped session fails. Nothing happens for a layer with no account: the process's own
    /// connection is not this object's to cancel.</summary>
    public void Reconnect()
    {
        lock (_gate)
        {
            if (_username.Length == 0) return;
            Cancel();
            EnsureLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate) Cancel();
    }

    private void EnsureLocked()
    {
        // No account configured: the layer reads as the service account. That works when the
        // share grants that account, and is the visitor the error message names when it does not.
        if (_connected || _username.Length == 0) return;

        var resource = new NetResource { Type = ResourceTypeDisk, RemoteName = _share };
        int error = WNetAddConnection2(ref resource, _password, _username, ConnectTemporary);
        if (error == ErrorAlreadyAssigned) return;   // a drive letter already reaches this share
        if (error == ErrorSessionCredentialConflict)
        {
            // One process can hold one account per server. Rebuilding a layer after the
            // credential changed is exactly that case, so the old session goes first.
            Cancel();
            error = WNetAddConnection2(ref resource, _password, _username, ConnectTemporary);
        }
        if (error != 0 && error != ErrorAlreadyAssigned) throw Failure(error);

        _connected = true;
        Interlocked.Increment(ref _connections);
    }

    private void Cancel()
    {
        if (!_connected) return;
        // The result is not actionable: a session that would not go away shows up as a
        // credential conflict on the next connect, which is handled there.
        WNetCancelConnection2(_share, 0, force: true);
        _connected = false;
    }

    /// <summary>Turns a Win32 error into what the mount can say about it. Credentials and
    /// reachability are told apart because they are fixed in different places: one in the layer's
    /// settings, the other on the network or on the machine that holds the share.</summary>
    private Exception Failure(int error)
    {
        string detail = new Win32Exception(error).Message;
        string who = _username.Length == 0 ? "the account this service runs as" : "the account '" + _username + "'";
        return error switch
        {
            ErrorLogonFailure or ErrorLogonTypeNotGranted => new AuthRequiredException(
                "'" + _share + "' did not accept " + who + " (" + detail + "); choose a credential this share grants in the layer's settings"),
            ErrorAccessDenied => new AuthRequiredException(
                "'" + _share + "' refused " + who + " (" + detail + "); the account needs read access to the share"),
            ErrorSessionCredentialConflict => new IOException(
                "this process already reaches '" + _share + "' as another account (" + detail + "); the instance has to be remounted for the new credential to apply"),
            ErrorBadNetPath => new IOException(
                "'" + _share + "' could not be reached (" + detail + "); the machine that holds it is offline, or the network refuses the connection (port 445)"),
            ErrorBadNetName => new IOException(
                "'" + _share + "' does not exist on this machine (" + detail + "); check the share name"),
            _ => new IOException("'" + _share + "' could not be reached (" + detail + ")")
        };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    /// <summary>The connection is deviceless (no local name), so it is scoped to this process
    /// rather than to a drive letter somebody might later want.</summary>
    [DllImport("mpr.dll", EntryPoint = "WNetAddConnection2W", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetAddConnection2(ref NetResource resource, string? password, string? username, int flags);

    [DllImport("mpr.dll", EntryPoint = "WNetCancelConnection2W", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetCancelConnection2(string name, int flags, [MarshalAs(UnmanagedType.Bool)] bool force);

    /// <summary>The Win32 codes that mean the session, not the request, is the problem. A caller
    /// that retries once after this list is reconnecting; anything else is the request's own
    /// answer and must not be retried.</summary>
    internal static bool IsConnectionFailure(int win32) => win32 is
        ErrorBadNetPath or ErrorBadNetName or ErrorUnexpectedNetError or ErrorNetNameDeleted
        or ErrorConnectionLost or ErrorNetworkUnreachable or ErrorHostUnreachable;
}
