using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PolyMount.Core;

/// <summary>
/// The operating system's own account of what one process has done: the whole cost of an instance,
/// since an instance is a process. Nothing here is counted by PolyMount, which is the point — these
/// are the numbers the per-layer account has to reconcile with, and a number we produced ourselves
/// could not be used to check our own arithmetic.
///
/// Read by pid, because that is what the supervisor reports for an instance and what a front end
/// has. A process that has gone, or one this account may not open, answers null rather than zero:
/// zero is a reading, and "not there" is not.
/// </summary>
public readonly record struct ProcessCounters(double CpuSeconds, long WorkingSetBytes, long PrivateBytes,
    int Threads, int Handles, long ReadBytes, long WriteBytes, long ReadOperations, long WriteOperations,
    long OtherBytes, long OtherOperations)
{
    /// <summary>The CPU, memory and handles <see cref="Process"/> itself reports, and the transfer
    /// counts it does not: those need <c>GetProcessIoCounters</c>. Both are the same process, so
    /// they are read together and reported together.</summary>
    public static ProcessCounters? Of(int pid)
    {
        if (pid <= 0) return null;
        try
        {
            using var process = Process.GetProcessById(pid);
            var counters = new IoCounters();
            bool io = GetProcessIoCounters(process.Handle, out counters);
            return new ProcessCounters(
                process.TotalProcessorTime.TotalSeconds,
                process.WorkingSet64,
                process.PrivateMemorySize64,
                process.Threads.Count,
                process.HandleCount,
                io ? (long)counters.ReadTransferCount : 0,
                io ? (long)counters.WriteTransferCount : 0,
                io ? (long)counters.ReadOperationCount : 0,
                io ? (long)counters.WriteOperationCount : 0,
                io ? (long)counters.OtherTransferCount : 0,
                io ? (long)counters.OtherOperationCount : 0);
        }
        catch (Exception)
        {
            // Gone, or not ours to ask about. The caller reports a gap rather than a zero.
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
}
