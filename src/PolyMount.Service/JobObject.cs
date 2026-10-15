using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PolyMount.Service;

/// <summary>A job object: the operating system's own answer to "these processes go away together".
///
/// Each instance's worker is put in one of its own, with the limit that closes its members when the
/// last handle to the job closes. The supervisor holds that handle for as long as it lives, so a
/// supervisor that *crashes* — not merely one that exits — takes its workers with it, and no
/// cleanup code has to survive to do it. Letting each worker watch its parent instead would not do:
/// a worker that is wedged is exactly the one that will not notice.
///
/// A job per instance is also where a per-instance CPU or memory cap goes later, which is the shape
/// those limits want and the reason this is not one job for all of them.
/// </summary>
internal sealed class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref ExtendedLimitInformation information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private IntPtr _handle;

    private JobObject(IntPtr handle) => _handle = handle;

    /// <summary>Creates a job whose members die with it, or null when the system refuses. Refusing
    /// is not fatal: the worker still runs and is still stopped on the way out, and what is lost is
    /// only the guarantee that a crash takes it along — which the caller logs.</summary>
    public static JobObject? Create()
    {
        IntPtr handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) return null;

        var information = new ExtendedLimitInformation();
        information.BasicLimitInformation.LimitFlags = KillOnJobClose;
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref information,
                (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            CloseHandle(handle);
            return null;
        }
        return new JobObject(handle);
    }

    /// <summary>Puts a process in this job. Closing the job afterwards kills it and everything it
    /// started, however it went.</summary>
    public bool Add(Process process)
    {
        try { return _handle != IntPtr.Zero && AssignProcessToJobObject(_handle, process.Handle); }
        catch (Exception) { return false; }
    }

    /// <summary>Closes the handle. Anything still in the job is killed as this returns, which is
    /// what makes stopping a worker that will not stop a matter of letting go.</summary>
    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }
}
