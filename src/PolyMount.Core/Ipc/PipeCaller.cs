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
/// server's decision to make (see PipeServer.Allowed), and a server that cannot be identified is
/// the client's.</summary>
public static class PipeCaller
{
    /// <summary>What the connection says about one side of it.</summary>
    public sealed record Identity(int ProcessId, string UserSid, string Account, bool IsAdministrator, string ImagePath);

    /// <summary>The process on the client side of a server connection.
    ///
    /// The account is read through the caller's process: <c>GetNamedPipeClientProcessId</c>, then
    /// that process's token. It needs PROCESS_QUERY_LIMITED_INFORMATION on a process owned by
    /// somebody else, and the account the installer actually creates — LocalService — has neither
    /// SeDebugPrivilege nor a grant in a user process's descriptor, so on an installation it cannot
    /// answer at all. The server serves a caller it cannot name rather than refusing it; see
    /// PipeServer.Allowed for why the descriptor makes that the same decision.
    ///
    /// The connection itself carries the answer and is not read (2026-10-27). A named pipe client
    /// can ask for identification (<c>NamedPipeClientStream</c> takes a TokenImpersonationLevel),
    /// and `ImpersonateNamedPipeClient` then gives the server the caller's token without opening
    /// anything the caller owns — which is exactly the access LocalService lacks. It was
    /// implemented, measured and taken back out: the impersonation does not stay on the thread that
    /// asked. After an identified caller had been impersonated, a later anonymous one left the
    /// thread holding an identification-level token even with <c>RevertToSelf</c> on every path
    /// out — the next directory that thread created failed with ERROR_BAD_IMPERSONATION_LEVEL, and
    /// unrelated tests elsewhere in the process failed the same way. A request handler goes on to
    /// do the service's work on that thread, so a token left behind runs that work as the previous
    /// caller. The account in the log is not worth that.
    ///
    /// So the caller is identified when its process can be read, the reason is returned when it
    /// cannot, and the decision about what that means belongs to the server.</summary>
    public static Identity? Client(NamedPipeServerStream pipe) => Client(pipe, out _);

    /// <summary>The same question, with the reason it could not be answered. A refusal nobody can
    /// explain is what this cost the first time: the service said "the caller's token could not be
    /// read" every ten seconds for six hours and the install job hung instead of reporting it, so
    /// the message now carries which call failed and with what error.</summary>
    public static Identity? Client(NamedPipeServerStream pipe, out string reason) =>
        Identify(pipe.SafePipeHandle, GetNamedPipeClientProcessId, out reason);

    /// <summary>The process on the server side of a client connection.</summary>
    public static Identity? Server(NamedPipeClientStream pipe) =>
        Identify(pipe.SafePipeHandle, GetNamedPipeServerProcessId, out _);

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

    private static Identity? Identify(SafePipeHandle handle, PipeIdQuery which, out string reason)
    {
        reason = "";
        if (!which(handle, out uint pid) || pid == 0)
        {
            reason = "the connection does not name a process";
            return null;
        }
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero)
        {
            reason = $"process {pid} cannot be opened (error {Marshal.GetLastWin32Error()})";
            return null;
        }
        try
        {
            string image = ImagePath(process);
            // The token is opened on the process, not on the pipe: a pipe handle carries no
            // identity of its own, and the process it connects to is the one whose account the
            // request will run as.
            if (!OpenProcessToken(process, TokenQuery, out IntPtr token) || token == IntPtr.Zero)
            {
                // Not an identity with an empty SID: a caller whose account could not be read is
                // "unidentified" like any other, and saying so is what keeps the server's decision
                // in one place instead of refusing this half of the same failure.
                reason = $"the token of process {pid} cannot be opened (error {Marshal.GetLastWin32Error()})";
                return null;
            }
            using var safe = new SafeAccessTokenHandle(token);
            using var identity = new WindowsIdentity(safe.DangerousGetHandle());
            string sid = identity.User?.Value ?? "";
            bool admin;
            try { admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
            catch (Exception) { admin = false; }
            return new Identity((int)pid, sid, AccountOf(identity, sid), admin, image);
        }
        catch (Exception ex) { reason = "the caller's process could not be read: " + ex.Message; return null; }
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
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);
}
