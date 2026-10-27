using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PolyMount.Core.Ipc;

/// <summary>Who is on the other end of a control-pipe connection, read from the connection
/// itself.
///
/// The pipe's access rules already refuse accounts that were not granted, so this answers a
/// different question: the rules say who may connect, this says who did. The server logs it,
/// refuses a caller the policy does not name, and the client uses the same two calls in the
/// other direction to check that the process it reached is PolyMount rather than a process that
/// created the pipe name first.
///
/// Everything here returns null instead of throwing: a caller that cannot be identified is the
/// server's decision to make (an open policy accepts it, a configured one does not), and a
/// server that cannot be identified is the client's.</summary>
public static class PipeCaller
{
    /// <summary>What the connection says about one side of it.</summary>
    public sealed record Identity(int ProcessId, string UserSid, string Account, bool IsAdministrator, string ImagePath);

    /// <summary>The process on the client side of a server connection.
    ///
    /// The account is taken by impersonating the connection, not by opening the caller's process.
    /// Opening it needs PROCESS_QUERY_LIMITED_INFORMATION on a process owned by somebody else, and
    /// the account the service runs under by default — LocalService, written by the installer —
    /// has neither SeDebugPrivilege nor a grant in that process's descriptor. Every request was
    /// therefore "unidentifiable", and a configuration that names its accounts (which the installer
    /// seeds, and which the security pass made the normal case) refused all of them: the tray, the
    /// MCP server and the install check each got "the caller's token could not be read" from a
    /// service that was running perfectly well.
    ///
    /// Impersonation is the kernel's own record of who connected. The pipe's descriptor has already
    /// decided that this account may, the token comes from the connection rather than from
    /// anything the caller says, and no access to its process is involved. The process route stays
    /// as the fallback for a pipe the server cannot impersonate on.</summary>
    public static Identity? Client(NamedPipeServerStream pipe)
    {
        uint pid = GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint id) ? id : 0;
        if (ImpersonateNamedPipeClient(pipe.SafePipeHandle))
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                string sid = identity.User?.Value ?? "";
                if (sid.Length > 0)
                {
                    bool admin;
                    try { admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
                    catch (Exception) { admin = false; }
                    // The image path is only ever logged, and reading it is exactly the process
                    // access this path exists to avoid.
                    return new Identity((int)pid, sid, AccountOf(identity, sid), admin, "");
                }
            }
            catch (Exception) { }
            finally { RevertToSelf(); }
        }
        return Identify(pipe.SafePipeHandle, GetNamedPipeClientProcessId);
    }

    /// <summary>The process on the server side of a client connection.</summary>
    public static Identity? Server(NamedPipeClientStream pipe) =>
        Identify(pipe.SafePipeHandle, GetNamedPipeServerProcessId);

    /// <summary>Whether some process already owns this pipe name. A server that finds its name
    /// taken is sharing it with something nobody configured: the caller then knows it may be
    /// talking to that process instead of this one.
    ///
    /// A connection that is refused for access still answers the question — the name exists and
    /// belongs to somebody who does not let this process in — so the two failures are told apart
    /// rather than both read as "free".</summary>
    public static bool NameIsTaken(string pipeName)
    {
        try
        {
            using var probe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
            probe.Connect(100);
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }
        catch (Exception) { return false; }
    }

    private delegate bool PipeIdQuery(SafePipeHandle pipe, out uint id);

    private static Identity? Identify(SafePipeHandle handle, PipeIdQuery which)
    {
        if (!which(handle, out uint pid) || pid == 0) return null;
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            string image = ImagePath(process);
            // The token is opened on the process, not on the pipe: a pipe handle carries no
            // identity of its own, and the process it connects to is the one whose account the
            // request will run as.
            if (!OpenProcessToken(process, TokenQuery, out IntPtr token) || token == IntPtr.Zero)
                return new Identity((int)pid, "", "", false, image);
            using var safe = new SafeAccessTokenHandle(token);
            using var identity = new WindowsIdentity(safe.DangerousGetHandle());
            string sid = identity.User?.Value ?? "";
            bool admin;
            try { admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
            catch (Exception) { admin = false; }
            return new Identity((int)pid, sid, AccountOf(identity, sid), admin, image);
        }
        catch (Exception) { return null; }
        finally { CloseHandle(process); }
    }

    private static string AccountOf(WindowsIdentity identity, string sid)
    {
        try { return identity.Name; }
        catch (Exception) { return sid; }
    }

    private static string ImagePath(IntPtr process)
    {
        var buffer = new StringBuilder(1024);
        int size = buffer.Capacity;
        return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString(0, size) : "";
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ImpersonateNamedPipeClient(SafePipeHandle pipe);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool RevertToSelf();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);
}
