using System.Collections.Concurrent;
using System.Diagnostics;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.Async;

namespace PolyMount.Core;

/// <summary>Bounded background request tracing, including operations that have not returned.</summary>
public sealed class RequestDiagnostics : IDisposable
{
    /// <summary>What both channels print for a scope's own line.</summary>
    private const string LineTemplate =
        "{Kind:l} id={RequestId} parent={ParentId} thread={ThreadId} pid={ProcessId} op={Operation:l} path='{Path:l}' elapsedMs={ElapsedMs} stage='{Stage:l}'";
    private static readonly AsyncLocal<Scope?> Current = new();
    private readonly Logger _logger;
    private readonly AsyncLogMonitor _monitor = new();
    private readonly ConcurrentDictionary<long, Scope> _active = new();
    private sealed class Counter { public long Value; }
    private readonly ConcurrentDictionary<string, Counter> _counts = new();
    private readonly object _inspectionGate = new();
    private readonly Timer _timer;
    private long _sequence;
    private long _unsampled;
    private long _lastDropped;
    private bool _disposed;
    private long _lastSummary;

    public RequestDiagnostics(string path)
    {
        _logger = AsyncFileLog.Create(path, _monitor);
        _timer = new Timer(_ => Flush(), null, 1000, 1000);
    }

    /// <summary>The recording channel, when this tracer has one. Absent in tests that build a
    /// tracer directly, and then recording a mount is refused rather than silently ignored.</summary>
    public RecordingChannel? Channel { get; init; }

    /// <summary>Whether a recording is attached right now. The request path asks this twice per
    /// scope — once to decide whether to build it, once to decide where its lines go — so it is a
    /// volatile flag and not a lock.</summary>
    internal bool Recording => Channel is { Attached: true };

    /// <summary>When the attached recording began, or null when none is attached. Part of a
    /// status reply that must not touch the disk to answer.</summary>
    internal string? RecordingStartedUtc => Channel is { Attached: true } channel ? channel.StartedUtc : null;

    /// <summary>
    /// The diagnostics the calling request is traced into. Work that outlives the request
    /// that started it — read-ahead on a worker thread, for instance — has no ambient scope
    /// of its own, so it captures this and passes it back to <see cref="Begin"/> explicitly.
    /// </summary>
    public static RequestDiagnostics? Ambient => Current.Value?._owner;

    /// <summary>The process the request being handled came from, or 0 outside one. Nested scopes
    /// inherit their parent's, so the innermost answer is the caller's.</summary>
    public static int AmbientProcessId => Current.Value?.ProcessId ?? 0;

    public static Scope? Begin(string operation, string path, RequestDiagnostics? diagnostics = null, int processId = 0)
    {
        var parent = Current.Value;
        diagnostics ??= parent?._owner;
        if (diagnostics == null) return null;
        // Layer probes dominate cached directory enumeration. Unsampled parent
        // operations already track total latency and pending work; nested SVN
        // calls still receive their own always-traced scopes. A recording wants
        // exactly the probes the rule discards, so a suppressed probe is only
        // dropped when nothing is recording it.
        bool suppressed = operation.StartsWith("Layer.", StringComparison.Ordinal) && parent is { Trace: false };
        if (suppressed && !diagnostics.Recording) return null;
        var scope = new Scope(diagnostics, parent, operation, path, processId, suppressed);
        diagnostics._active[scope.Id] = scope;
        Current.Value = scope;
        scope.Emit("BEGIN");
        if (operation.StartsWith("WinFsp.", StringComparison.Ordinal))
            Interlocked.Increment(ref diagnostics._counts.GetOrAdd($"pid={scope.ProcessId} op={operation}", _ => new Counter()).Value);
        return scope;
    }

    public long DroppedMessages => _monitor.Dropped ?? _lastDropped;
    internal Action CallbackCounter(string operation, int processId)
    {
        var counter = _counts.GetOrAdd($"pid={processId} op={operation}", _ => new Counter());
        return () => Interlocked.Increment(ref counter.Value);
    }
    public void Error(string operation, Exception exception) => _logger.Error(exception, "Operation failed: {Operation}", operation);

    internal void Flush()
    {
        lock (_inspectionGate)
        {
            if (_disposed) return;
            if (Environment.TickCount64 - _lastSummary >= 5000)
            {
                _lastSummary = Environment.TickCount64;
                foreach (var pair in _counts)
                {
                    long count = Interlocked.Exchange(ref pair.Value.Value, 0);
                    if (count > 0) Write(LogEventLevel.Information, "COUNT {Counter:l} requests={Requests}", new object?[] { pair.Key, count });
                }
            }
            foreach (var scope in _active.Values)
                if (scope.ElapsedMs >= 1000 && scope.ElapsedMs - scope.LastReportMs >= 5000)
                {
                    scope.LastReportMs = scope.ElapsedMs;
                    scope.Emit("PENDING");
                }
            long dropped = DroppedMessages;
            if (dropped > _lastDropped) Write(LogEventLevel.Warning, "DROPPED count={Count} total={Total}", new object?[] { dropped - _lastDropped, dropped });
            _lastDropped = dropped;
        }
    }

    /// <summary>The lines that are not scopes — the callback counts and the drop reports. A
    /// recording does not write these either: it holds them as counts, which is what these lines
    /// are a summary of.</summary>
    private void Write(LogEventLevel level, string template, object?[] args) => _logger.Write(level, template, args);

    public void Dispose()
    {
        _timer.Dispose();
        lock (_inspectionGate)
        {
            Flush();
            _disposed = true;
            // The recording is closed before the sampled log, so a failure tearing down the
            // sampled sink cannot leave a recording attached with nothing left to close it.
            Channel?.Stop("disposed");
            _logger.Dispose(); // Serilog drains the queue and closes the sink.
        }
    }

    public sealed class Scope : IDisposable
    {
        internal readonly RequestDiagnostics _owner;
        private readonly Scope? _parent;
        private readonly string _operation, _path;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly bool _suppressed;
        private string _stage = "running";
        private int _finished;
        internal long Id { get; }
        internal int ProcessId { get; }
        internal bool Trace { get; }
        internal long ElapsedMs => _watch.ElapsedMilliseconds;
        internal long LastReportMs = -5000;

        internal Scope(RequestDiagnostics owner, Scope? parent, string operation, string path, int processId, bool suppressed)
        {
            _owner = owner; _parent = parent; _operation = operation; _suppressed = suppressed;
            _path = path.Replace('\r', ' ').Replace('\n', ' ');
            // A scope the sampling rule would have discarded takes its id from a range of its own.
            // Sharing the sequence would let attaching a recording shift which root requests the
            // 1/128 draw picks, and two exports taken either side of a recording would no longer
            // be comparable. Negative ids also mark exactly those lines in the recording.
            Id = suppressed ? -Interlocked.Increment(ref owner._unsampled) : Interlocked.Increment(ref owner._sequence);
            ProcessId = processId != 0 ? processId : parent?.ProcessId ?? 0;
            Trace = operation.StartsWith("SVN.", StringComparison.Ordinal) ||
                (parent == null ? Id % 128 == 1 : parent.Trace);
        }

        public void Stage(string stage) => Volatile.Write(ref _stage, stage);

        /// <summary>Writes this scope's line, if the sampling rule kept it. The decision here is
        /// exactly the one the callers used to make themselves; a recording is not a second place
        /// lines go, it is a count of what this declines to write.</summary>
        internal void Emit(string kind)
        {
            if (_suppressed || !(kind == "PENDING" || Trace || (kind == "END" && ElapsedMs >= 100))) return;
            object?[] args =
            {
                kind, Id, _parent?._owner == _owner ? _parent.Id : 0, _thread,
                ProcessId, _operation, _path, ElapsedMs, Volatile.Read(ref _stage)
            };
            _owner._logger.Information(LineTemplate, args);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) return;
            _owner._active.TryRemove(Id, out _);
            long elapsed = ElapsedMs;
            Emit("END");
            // A recording counts this request whether or not a line was written about it, which is
            // what lets a window be described exactly without describing every request in it.
            if (_owner.Recording) _owner.Channel!.Count(_operation, _path, elapsed);
            Current.Value = _parent;
        }
    }
}
