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

    /// <summary>How many events may be queued before the sink writes them. Sixteen times the
    /// sampled channel's: what a recording may not do is lose lines, and the first measurement
    /// above dropped 1180 of 1.67 million at the smaller size. Still a queue rather than a block
    /// — backpressure would slow the very requests being measured — so drops remain possible,
    /// which is why they are counted and reported.</summary>
    public const int BufferSize = 65536;

    /// <summary>Identifies this service process, so a front-end can tell a service restart from
    /// an instance reload.</summary>
    public static readonly string ServiceInstanceId = Guid.NewGuid().ToString("N");

    /// <summary>Serializes every recorded write against the detach that disposes the sink. A
    /// writer that saw <see cref="Attached"/> and then waits here finds no logger and does
    /// nothing, so no request thread can ever write into a disposed sink. Lock order is the
    /// tracer's flush gate and then this one, never the other way round.</summary>
    private readonly object _gate = new();
    private readonly string _root;
    private readonly AsyncLogMonitor _monitor = new();
    private Logger? _logger;
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
            _logger = AsyncFileLog.Create(Path.Combine(_directory, "events.log"), _monitor, SegmentBytes,
                retainedSegments: null, bufferSize: BufferSize);
            Volatile.Write(ref _startedTicks, _startedUtc.Ticks);
            _attached = true;
            return InfoLocked();
        }
    }

    /// <summary>Detaches and closes the session. Idempotent, and safe to call while requests are
    /// still running.</summary>
    public RecordingSessionInfo Stop(string reason)
    {
        Logger? logger = null;
        lock (_gate)
        {
            if (_attached)
            {
                _attached = false;
                Volatile.Write(ref _startedTicks, 0);
                _stopReason = reason;
                _stoppedUtc = DateTime.UtcNow;
                logger = _logger;
                _logger = null;
            }
        }
        if (logger != null)
        {
            // Outside the gate: draining the queue can take a moment, and no writer can reach the
            // sink any more, so request threads have no reason to wait behind it.
            try { logger.Dispose(); }
            catch (Exception ex) { _error = ex.Message; }
        }
        return Info();
    }

    /// <summary>Writes one event to the recording when one is attached. Called from the request
    /// path: it must not throw, and it blocks no longer than one queue enqueue.</summary>
    internal void Write(LogEventLevel level, string template, object?[] args)
    {
        if (!_attached) return;
        lock (_gate) _logger?.Write(level, template, args);
    }

    /// <summary>Called once a second by the tracer's flush. A recording that reached its ceiling
    /// stops itself and reports the reason; the oldest segments are never dropped to make room,
    /// because they are the part of a recording that cannot be re-run.</summary>
    internal void CheckCapacity()
    {
        if (_attached && DirectoryBytes() >= CapacityBytes) Stop("capacity");
    }

    public RecordingSessionInfo Info() { lock (_gate) return InfoLocked(); }

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
            DroppedEvents = _monitor.Dropped ?? 0,
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
