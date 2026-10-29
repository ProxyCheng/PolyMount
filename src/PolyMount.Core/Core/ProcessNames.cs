using System.Collections.Concurrent;
using System.Diagnostics;

namespace PolyMount.Core;

/// <summary>What a process id is called, and where its image is.
///
/// Asking is expensive — opening a process and reading its name is tens of microseconds, and its
/// image path is another open — so it is never asked on the request path: the file system records
/// the id, and a metrics sample names the handful of processes that were in its window, about once
/// a second.
///
/// An answer is kept for a minute rather than for ever, because Windows hands a pid out again once
/// its process has ended: the process' own start time travels with the name, so an id that is now
/// something else is not answered with the old name. A process that ended while it was being
/// counted keeps the name it was last seen under — the window is a minute wide and a reader is owed
/// the name of who did the work — and one that was never caught alive reads as its own id.
/// </summary>
internal static class ProcessNames
{
    /// <summary>One process as it was last read: what it is called, where its image is, and when it
    /// started. <see cref="Path"/> is null when the image could not be read — a process of another
    /// account, or one that ended between the two questions — which is a case and not an error.</summary>
    internal sealed record Entry(string Name, string? Path, long StartTicks, long ResolvedAtMs, bool PathAsked);

    private static readonly ConcurrentDictionary<int, Entry> Known = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    /// <summary>How many names are remembered before the stale ones are swept. A service that runs
    /// for days sees thousands of pids; what a window can name is a handful.</summary>
    private const int Capacity = 256;

    /// <summary>The name of the process with this id, or null if it has never been caught alive.
    /// Never throws: a process that ended between being counted and being named is a case, not an
    /// error.</summary>
    public static string? Of(int processId) => Resolve(processId, wantPath: false)?.Name;

    /// <summary>The same answer with the image's own path, for the reader that has to look at the
    /// program rather than name it. Null when the process cannot be opened or has ended.</summary>
    internal static Entry? Describe(int processId) => Resolve(processId, wantPath: true);

    private static Entry? Resolve(int processId, bool wantPath)
    {
        if (processId <= 0) return null;
        long now = Environment.TickCount64;
        Known.TryGetValue(processId, out var known);
        bool fresh = known != null && now - known.ResolvedAtMs < Ttl.TotalMilliseconds;
        if (fresh && (!wantPath || known!.PathAsked)) return known;
        try
        {
            using var process = Process.GetProcessById(processId);
            string name = process.ProcessName;
            long startTicks = process.StartTime.Ticks;
            string? path = null;
            bool asked = false;
            if (wantPath || known is { Path: not null })
            {
                asked = true;
                try { path = process.MainModule?.FileName; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
            var entry = new Entry(name, path, startTicks, now, asked);
            Known[processId] = entry;
            if (Known.Count > Capacity) Sweep(now);
            return entry;
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        if (known != null)
        {
            // Readable once and not now: it keeps the name, and waits out another minute before
            // asking again.
            var stale = known with { ResolvedAtMs = now };
            Known[processId] = stale;
            return stale;
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
