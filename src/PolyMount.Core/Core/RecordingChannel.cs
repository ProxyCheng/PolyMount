using System.Globalization;
using PolyMount.Core.Ipc;

namespace PolyMount.Core;

/// <summary>
/// A recording, as the service sees it: the switch that tells the tracer to count instead of log,
/// and the counters themselves.
///
/// There is no file and no thread here, and that is the point. A recording that wrote a line about
/// every event cost 18–37 MB/s of disk, several times the per-operation latency it was measuring,
/// and — until the writing was moved off the request threads — a third of its own data. Counting
/// costs two increments on the request thread, cannot drop anything, and needs no ceiling at which
/// to stop itself.
///
/// What a reader needs in order to locate a slow call is per-request detail, and that is already in
/// the sampled log: a request that took long enough to be interesting is exactly the one the rule
/// keeps a line about, with its path and its stage. The front end copies that file for the window.
/// This counts everything, including the requests no line was ever going to be written for.
/// </summary>
public sealed class RecordingChannel : IDisposable
{
    /// <summary>Identifies this service process, so a front-end can tell a service restart from
    /// an instance reload.</summary>
    public static readonly string ServiceInstanceId = Guid.NewGuid().ToString("N");

    /// <summary>Serializes the control plane — attach, detach, the status question — and nothing on
    /// the request path, which is what lets a detach be a reference swap.</summary>
    private readonly object _gate = new();
    private readonly string _root;
    /// <summary>Read once per recorded request and never locked: the counters a running recording
    /// writes into, or null while none is running.</summary>
    private RecordingCounters? _counters;
    private volatile bool _attached;
    private bool _disposed;
    private string _sessionId = "", _mount = "", _label = "";
    private long _startedTicks, _sequence;
    private DateTime _startedUtc;
    private DateTime? _stoppedUtc;
    private string? _stopReason, _error;

    public RecordingChannel(string root) => _root = root;

    /// <summary>What sessions are named after. Nothing is written under it any more; it stays so
    /// the channel still knows which instance's storage it belongs to.</summary>
    public string Root => _root;

    /// <summary>Whether a recording is attached right now. Read on the request path, so it is a
    /// volatile flag rather than a lock.</summary>
    public bool Attached => _attached;

    /// <summary>When the attached session began, or null while none is attached. Answers a status
    /// question without taking the gate.</summary>
    public string? StartedUtc => Volatile.Read(ref _startedTicks) is var ticks && ticks != 0
        ? new DateTime(ticks, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture)
        : null;

    /// <summary>Starts a session. An attached channel returns the running session unchanged, so a
    /// front-end that lost track — a restarted tray — adopts the recording instead of clobbering
    /// what it has already counted.</summary>
    public RecordingSessionInfo Start(string mount, string label)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_attached) return InfoLocked();
            _mount = mount;
            _label = label;
            _startedUtc = DateTime.UtcNow;
            _stoppedUtc = null;
            _stopReason = null;
            _error = null;
            // The flag is set only once the counters are in place: a request that sees Attached is
            // guaranteed to find somewhere to put its count.
            _counters = new RecordingCounters();
            _sessionId = _startedUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                + "." + Interlocked.Increment(ref _sequence).ToString(CultureInfo.InvariantCulture);
            Volatile.Write(ref _startedTicks, _startedUtc.Ticks);
            _attached = true;
            return InfoLocked();
        }
    }

    /// <summary>Detaches and answers with everything the recording counted. Idempotent, and safe to
    /// call while requests are still running: a request that lands after it is counted by nothing,
    /// and that is the whole of what it costs — there is no queue to drain and nothing to lose.</summary>
    public RecordingSessionInfo Stop(string reason)
    {
        lock (_gate)
        {
            if (!_attached) return InfoLocked();
            _attached = false;
            Volatile.Write(ref _startedTicks, 0);
            _stopReason = reason;
            _stoppedUtc = DateTime.UtcNow;
            return InfoLocked();
        }
    }

    /// <summary>Counts one finished request. Called from the request path: one reference read and
    /// two tally additions. It cannot throw and it has nowhere to block.</summary>
    internal void Count(string operation, string path, double elapsedMs)
        => Volatile.Read(ref _counters)?.Count(operation, path, elapsedMs);

    public RecordingSessionInfo Info() { lock (_gate) return InfoLocked(); }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop("disposed");
    }

    private RecordingSessionInfo InfoLocked() => new()
    {
        Active = _attached,
        Mount = _mount,
        Label = _label,
        SessionId = _sessionId,
        StartedUtc = _startedUtc == default ? "" : _startedUtc.ToString("O", CultureInfo.InvariantCulture),
        StoppedUtc = _stoppedUtc?.ToString("O", CultureInfo.InvariantCulture),
        StoppedReason = _stopReason,
        ServiceInstanceId = ServiceInstanceId,
        Error = _error,
        Counts = _counters?.Snapshot()
    };
}
