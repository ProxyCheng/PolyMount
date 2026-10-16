using System.Collections.Concurrent;
using System.Diagnostics;

namespace PolyMount.Core;

/// <summary>What a process id is called.
///
/// Asking is expensive — opening a process and reading its name is tens of microseconds — so it is
/// never asked on the request path: the file system records the id, and a metrics sample names the
/// handful of processes that were in its window, about once a second.
///
/// An answer is kept for a minute rather than for ever, because Windows hands a pid out again once
/// its process has ended: the process' own start time travels with the name, so an id that is now
/// something else is not answered with the old name. A process that ended while it was being
/// counted keeps the name it was last seen under — the window is a minute wide and a reader is owed
/// the name of who did the work — and one that was never caught alive reads as its own id.
/// </summary>
internal static class ProcessNames
{
    private sealed record Entry(string Name, long StartTicks, long ResolvedAtMs);

    private static readonly ConcurrentDictionary<int, Entry> Known = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    /// <summary>How many names are remembered before the stale ones are swept. A service that runs
    /// for days sees thousands of pids; what a window can name is a handful.</summary>
    private const int Capacity = 256;

    /// <summary>The name of the process with this id, or null if it has never been caught alive.
    /// Never throws: a process that ended between being counted and being named is a case, not an
    /// error.</summary>
    public static string? Of(int processId)
    {
        if (processId <= 0) return null;
        long now = Environment.TickCount64;
        Known.TryGetValue(processId, out var known);
        if (known != null && now - known.ResolvedAtMs < Ttl.TotalMilliseconds) return known.Name;
        try
        {
            using var process = Process.GetProcessById(processId);
            string name = process.ProcessName;
            long startTicks = process.StartTime.Ticks;
            Known[processId] = new Entry(name, startTicks, now);
            if (Known.Count > Capacity) Sweep(now);
            return name;
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        if (known != null)
        {
            // Readable once and not now: it keeps the name, and waits out another minute before
            // asking again.
            Known[processId] = known with { ResolvedAtMs = now };
            return known.Name;
        }
        return null;
    }

    /// <summary>Drops the entries that have gone stale, which is every entry a window can no longer
    /// name.</summary>
    private static void Sweep(long now)
    {
        foreach (var (pid, entry) in Known)
            if (now - entry.ResolvedAtMs >= Ttl.TotalMilliseconds) Known.TryRemove(pid, out _);
    }
}
