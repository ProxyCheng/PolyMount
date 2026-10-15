using System.Globalization;
using PolyMount.Core.Ipc;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace PolyMount.Core;

/// <summary>
/// A second sink the request tracer can be attached to. While it is attached, every event the
/// tracer emits is written here as well — including the work the sampling rule discards, because
/// a recording is meant to be complete rather than representative. Nothing about the sampled
/// <c>requests.log</c> changes: its rules and its lines are the same whether or not a recording
/// is running beside it.
///
/// The lines need no formatter of their own: this is a Serilog file logger with the sampled
/// channel's output template, so a recording parses with the same tools and the same reader.
/// </summary>
public sealed class RecordingChannel : IDisposable
{
    /// <summary>Rolled at this size. Every segment is kept — in a recording the beginning is the
    /// part that cannot be re-run — so disk is bounded by <see cref="SessionBytes"/> instead.</summary>
    public const long SegmentBytes = 32L * 1024 * 1024;

    /// <summary>What one session may write before it stops itself and says why.
    ///
    /// Measured on TX4Package, 2026-10-15: a recursive walk of the mounted tree produced
    /// **7.5–8.7 MB/s** of request events with the sampling filter off (288 MB in 40 seconds
    /// across nine rolled segments). So this ceiling is about four minutes of the heaviest
    /// traversal, and far longer of ordinary work. It is deliberately the same number the
    /// front-end keeps, so there is one limit to explain rather than two.</summary>
    public const long SessionBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>How many events may be queued before the sink writes them. Thirty-two times the
    /// sampled channel's. What a recording may not do is lose lines, and the first measurement
    /// dropped 1180 of 1.67 million at 4096 — then a third of a much larger one, until the lock on
    /// the write path came off. Still a queue rather than a block: backpressure would slow the very
    /// requests being measured, so drops remain possible and are counted and reported.</summary>
    public const int BufferSize = 131072;

    /// <summary>Identifies this service process, so a front-end can tell a service restart from
    /// an instance reload.</summary>
    public static readonly string ServiceInstanceId = Guid.NewGuid().ToString("N");

    /// <summary>Serializes the control plane — attach, detach, the capacity check — and nothing on
    /// the request path: see <see cref="Write"/> for why that distinction matters.</summary>
    private readonly object _gate = new();
    private readonly string _root;
    /// <summary>The handoff to the writing thread, and the thread itself.</summary>
    private LogRing? _ring;
    private Thread? _writer;
    private long _dropped;
    private volatile bool _attached;
    private bool _disposed;
    private string _sessionId = "", _directory = "", _label = "", _mount = "";
    private long _startedTicks;
    private DateTime _startedUtc;
    private DateTime? _stoppedUtc;
    private string? _stopReason;
    private string? _error;

    public RecordingChannel(string root) => _root = root;

    /// <summary>What this channel may write before it stops itself. An instance value rather than
    /// the constant alone, so a test can reach the ceiling without writing half a gigabyte.</summary>
    public long CapacityBytes { get; init; } = SessionBytes;

    /// <summary>Whether a recording is attached right now. Read on the request path, so it is a
    /// volatile flag rather than a lock.</summary>
    public bool Attached => _attached;

    /// <summary>When the attached session began, or null while none is attached. Answers a status
    /// question without taking the gate or touching the disk.</summary>
    public string? StartedUtc => Volatile.Read(ref _startedTicks) is var ticks && ticks != 0
        ? new DateTime(ticks, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture)
        : null;

    /// <summary>Starts a session. An attached channel returns the running session unchanged, so a
    /// front-end that lost track — a restarted tray — adopts the recording instead of clobbering
    /// the evidence already collected.</summary>
    public RecordingSessionInfo Start(string mount, string label)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_attached) return InfoLocked();
            Prune();
            _sessionId = NewSessionId();
            _directory = Path.Combine(_root, _sessionId);
            Directory.CreateDirectory(_directory);
            _mount = mount;
            _label = label;
            _startedUtc = DateTime.UtcNow;
            _stoppedUtc = null;
            _stopReason = null;
            _error = null;
            // The flag is set only once the logger is in place: a writer that sees Attached is
            // guaranteed to find a sink behind it.
            // The file is written by one thread of our own, synchronously: the request threads
            // only ever hand it values, so neither rendering a line nor touching the disk is paid
            // for on the thread that serves a request.
            var logger = AsyncFileLog.CreateSynchronous(Path.Combine(_directory, "events.log"), SegmentBytes, null);
            var ring = new LogRing(BufferSize);
            _ring = ring;
            _writer = new Thread(() => WriteLoop(ring, logger)) { IsBackground = true, Name = "PolyMount recording writer" };
            _writer.Start();
            Volatile.Write(ref _startedTicks, _startedUtc.Ticks);
            _attached = true;
            return InfoLocked();
        }
    }

    /// <summary>Detaches and closes the session. Idempotent, and safe to call while requests are
    /// still running. When it returns, everything written before it was called is on disk: the
    /// front-end copies the file the moment it gets this answer, so that has to be a fact rather
    /// than a hope.</summary>
    public RecordingSessionInfo Stop(string reason)
    {
        LogRing? ring;
        Thread? writer;
        lock (_gate)
        {
            if (!_attached) return Info();
            _attached = false;
            Volatile.Write(ref _startedTicks, 0);
            _stopReason = reason;
            _stoppedUtc = DateTime.UtcNow;
            ring = _ring;
            writer = _writer;
            _ring = null;
            _writer = null;
        }
        // Closing the ring is what makes the drain complete: no further entry may be claimed, and
        // the writing thread finishes every entry that was — including one a producer had claimed
        // but not yet published when the reader decided to stop.
        ring?.Close();
        if (writer != null && !writer.Join(TimeSpan.FromSeconds(10)))
            _error = "the recording writer did not finish in time; the last lines may be missing";
        return Info();
    }

    /// <summary>Hands one event to the writing thread. Called once per event per scope, so what it
    /// costs is charged to the request being measured: claim a slot, copy four values, publish it —
    /// no lock, no rendering, no disk. An earlier version took the control gate here and rendered
    /// the line, which cost twice over: the sink could not take events in as fast as they arrived
    /// and dropped a third of a recording, and because a scope emits its own BEGIN inside the
    /// window it is measuring, every request billed the wait to itself.</summary>
    internal void Write(LogEventLevel level, string template, object?[] args)
        => _ring?.TryWrite(level, template, args);

    /// <summary>The one thread that renders lines and writes them. It is the only thread that
    /// touches the file, so the sink needs no lock and the work it does is invisible to the
    /// requests being recorded.</summary>
    private void WriteLoop(LogRing ring, Logger logger)
    {
        int failures = 0;
        while (ring.TryRead(out var level, out var template, out var args))
        {
            try { logger.Write(level, template, args); failures = 0; }
            catch (Exception ex)
            {
                // A sink that cannot be written to stays unwritable: report it once, keep draining
                // so no producer ever waits, and count what the failure costs.
                _error = ex.Message;
                if (++failures > 100) { DrainAndCount(ring); break; }
            }
        }
        // The thread that writes owns the sink, so it is the one that closes it — and closing is
        // what flushes: the file is buffered, so without this the recording is empty on disk.
        try { logger.Dispose(); }
        catch (Exception ex) { _error = ex.Message; }
        Interlocked.Exchange(ref _dropped, ring.Dropped);
    }

    /// <summary>Empties the ring of a sink that failed, counting what it held.</summary>
    private static void DrainAndCount(LogRing ring)
    {
        while (ring.TryRead(out _, out _, out _)) Interlocked.Increment(ref ring.Dropped);
    }

    /// <summary>Called once a second by the tracer's flush. A recording that reached its ceiling
    /// stops itself and reports the reason; the oldest segments are never dropped to make room,
    /// because they are the part of a recording that cannot be re-run.</summary>
    internal void CheckCapacity()
    {
        if (_attached && DirectoryBytes() >= CapacityBytes) Stop("capacity");
    }

    public RecordingSessionInfo Info() { lock (_gate) return InfoLocked(); }

    /// <summary>What the channel itself refused, from either side of the handoff.</summary>
    private long Dropped => _ring is { } ring ? Volatile.Read(ref ring.Dropped) : Interlocked.Read(ref _dropped);

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        Stop("disposed");
    }

    private RecordingSessionInfo InfoLocked()
    {
        var info = new RecordingSessionInfo
        {
            Active = _attached,
            Mount = _mount,
            Label = _label,
            SessionId = _sessionId,
            Directory = _directory,
            StartedUtc = _startedUtc == default ? "" : _startedUtc.ToString("O", CultureInfo.InvariantCulture),
            StoppedUtc = _stoppedUtc?.ToString("O", CultureInfo.InvariantCulture),
            StoppedReason = _stopReason,
            CapacityBytes = CapacityBytes,
            DroppedEvents = Dropped,
            ServiceInstanceId = ServiceInstanceId,
            Error = _error
        };
        foreach (string file in Segments())
        {
            long bytes = new FileInfo(file).Length;
            info.Segments.Add(new LogFile { Name = Path.GetFileName(file), Source = file, Bytes = bytes });
            info.Bytes += bytes;
        }
        return info;
    }

    /// <summary>The session's segments, oldest first. Serilog names the rolled ones by sequence,
    /// so the family is matched rather than one expected name.</summary>
    private List<string> Segments()
    {
        if (_directory.Length == 0) return new List<string>();
        try { return Directory.GetFiles(_directory, "events*.log").Order(StringComparer.Ordinal).ToList(); }
        catch (DirectoryNotFoundException) { return new List<string>(); }
    }

    private long DirectoryBytes()
    {
        long total = 0;
        foreach (string file in Segments())
        {
            try { total += new FileInfo(file).Length; }
            catch (FileNotFoundException) { }
        }
        return total;
    }

    /// <summary>Session names sort chronologically, which is what makes pruning "keep the one
    /// before this" rather than a guess.</summary>
    private string NewSessionId()
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string id = stamp;
        for (int suffix = 1; Directory.Exists(Path.Combine(_root, id)); suffix++)
            id = stamp + "-" + suffix.ToString(CultureInfo.InvariantCulture);
        return id;
    }

    /// <summary>Clears earlier sessions, keeping the newest one: a front-end that is still
    /// draining a session it has just stopped must find its files. Nothing is deleted on stop.</summary>
    private void Prune()
    {
        try
        {
            if (!Directory.Exists(_root)) return;
            var sessions = Directory.GetDirectories(_root).Order(StringComparer.Ordinal).ToList();
            foreach (string stale in sessions.Take(Math.Max(0, sessions.Count - 1)))
            {
                try { Directory.Delete(stale, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (DirectoryNotFoundException) { }
    }
}
