using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace PolyMount.Plugins.Svn;

/// <summary>
/// Whose Subversion cache to read, and how to read it when it belongs to somebody else.
///
/// The client protects the password it saves with DPAPI, keyed to the Windows account that typed
/// it, so the bytes are unreadable to every other account — including the LocalSystem a service
/// runs as. Two things are needed to open them: the account's profile directory, which the client
/// keeps its cache under, and the account's own identity for the length of the read. The identity
/// comes from the account's logon session (<c>WTSQueryUserToken</c>), and is borrowed only around
/// the file read and the decrypt, through <see cref="WindowsIdentity.RunImpersonated"/> — no
/// process is started, and nothing else runs under it.
///
/// Two conditions come with that, and both are said plainly rather than reported as a bad password:
/// the process has to be LocalSystem (the session token needs SeTcbPrivilege), and the account has
/// to be signed in (a profile that is not loaded has no master key to open the blob with).
/// </summary>
internal static class WindowsUser
{
    /// <summary>Whether <paramref name="account"/> is the account this process runs as, so the
    /// cache can be read without borrowing anything.</summary>
    internal static bool IsCurrent(string account)
    {
        var sid = SidOf(account);
        return sid != null && WindowsIdentity.GetCurrent().User is { } current && current.Equals(sid);
    }

    /// <summary>The Subversion credential directory of an account: the client's
    /// <c>Subversion\auth\svn.simple</c> below the account's roaming application data.</summary>
    internal static string SubversionDirectory(string account)
    {
        var sid = SidOf(account) ?? throw new AuthRequiredException(
            "'" + account + "' is not a Windows account on this machine; a Subversion client cache "
            + "belongs to an account, so this credential has to name one (leave it empty for the account this service runs as)");

        string roaming = RoamingAppData(sid) ?? throw new AuthRequiredException(
            "the Windows account '" + account + "' has no profile on this machine, so it has no TortoiseSVN cache to read");

        return Path.Combine(roaming, "Subversion", "auth", "svn.simple");
    }

    /// <summary>Runs <paramref name="work"/> as <paramref name="account"/>: in this process when it
    /// is already the right account, and otherwise under that account's logon session.</summary>
    internal static T RunAs<T>(string account, Func<T> work)
    {
        var sid = SidOf(account) ?? throw new AuthRequiredException(
            "'" + account + "' is not a Windows account on this machine; a Subversion client cache "
            + "belongs to an account, so this credential has to name one (leave it empty for the account this service runs as)");

        if (WindowsIdentity.GetCurrent().User is { } current && current.Equals(sid)) return work();

        using var identity = SessionIdentity(sid) ?? throw new AuthRequiredException(
            "the Windows account '" + account + "' is not signed in on this machine, so the credentials it "
            + "saved cannot be opened: Windows protects them with a key that only that account's own logon has. "
            + "Sign in as '" + account + "' (or leave it signed in), or choose an account credential on this layer. "
            + "Reading another account's cache also needs this service to run as LocalSystem; it runs as '"
            + CurrentAccount() + "'.");

        T result = default!;
        WindowsIdentity.RunImpersonated(identity.AccessToken, () => result = work());
        return result;
    }

    private static string CurrentAccount()
    {
        try { return WindowsIdentity.GetCurrent().Name; }
        catch (Exception) { return "this process"; }
    }

    /// <summary>The logged-on identity of an account, taken from whichever session it is signed in
    /// to — an RDP session left disconnected still has one, which is the common case for the person
    /// whose cache a service is asked to read.</summary>
    private static WindowsIdentity? SessionIdentity(SecurityIdentifier account)
    {
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out IntPtr buffer, out int count))
            throw new AuthRequiredException("the logon sessions on this machine could not be listed (error "
                + Marshal.GetLastWin32Error() + "); reading another account's Subversion cache needs this service to run as LocalSystem");

        try
        {
            int size = Marshal.SizeOf<WtsSessionInfo>();
            for (int i = 0; i < count; i++)
            {
                var session = Marshal.PtrToStructure<WtsSessionInfo>(buffer + i * size);
                // Only a session somebody signed in to has a user token; the rest (session 0, the
                // logon screen) answer nothing and are passed over.
                if (session.State is not (WtsConnectState.Active or WtsConnectState.Disconnected)) continue;
                if (!WTSQueryUserToken(session.SessionId, out IntPtr token)) continue;
                try
                {
                    var identity = new WindowsIdentity(token);
                    if (identity.User is { } user && user.Equals(account)) return identity;
                    identity.Dispose();
                }
                catch (Exception)
                {
                    // A token this process cannot read says nothing about the account asked for.
                }
            }
            return null;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>The SID of a user account, or null when the name names nothing this machine knows.
    /// Groups and other non-user accounts are refused: they have no profile and no cache.</summary>
    private static SecurityIdentifier? SidOf(string account)
    {
        if (string.IsNullOrWhiteSpace(account)) return null;

        int sidSize = 68;            // SECURITY_MAX_SID_SIZE
        int nameSize = 256;          // the domain name Windows fills in beside it
        byte[] sid = new byte[sidSize];
        var domain = new StringBuilder(nameSize);
        if (!LookupAccountNameW(null, account.Trim(), sid, ref sidSize, domain, ref nameSize, out var use))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ErrorInsufficientBuffer) return null;
            sid = new byte[sidSize];
            domain = new StringBuilder(nameSize);
            if (!LookupAccountNameW(null, account.Trim(), sid, ref sidSize, domain, ref nameSize, out use)) return null;
        }
        if (use != SidNameUse.User) return null;
        try { return new SecurityIdentifier(sid, 0); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Where an account's roaming application data lives. The profile's own record is
    /// asked first: a redirected folder is the reason not to assume <c>&lt;profile&gt;\AppData</c>.</summary>
    private static string? RoamingAppData(SecurityIdentifier sid)
    {
        try
        {
            using (var shell = Registry.Users.OpenSubKey(
                sid.Value + @"\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders"))
                if (shell?.GetValue("AppData") is string redirected && redirected.Length > 0)
                    return Environment.ExpandEnvironmentVariables(redirected);

            using var profile = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid.Value);
            return profile?.GetValue("ProfileImagePath") is string directory && directory.Length > 0
                ? Path.Combine(directory, "AppData", "Roaming")
                : null;
        }
        catch (Exception)
        {
            // An unreadable registry says nothing about the account; the caller reports it as a
            // profile that is not there rather than as the hive name it could not open.
            return null;
        }
    }

    private enum WtsConnectState
    {
        Active, Connected, ConnectQuery, Shadow, Disconnected, Idle, Listen, Reset, Down, Init
    }

    private enum SidNameUse
    {
        User = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr StationName;
        public WtsConnectState State;
    }

    private const int ErrorInsufficientBuffer = 122;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupAccountNameW(string? system, string account, byte[] sid,
        ref int sidSize, StringBuilder domain, ref int domainSize, out SidNameUse use);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version,
        out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
