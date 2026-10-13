using System.Collections.Concurrent;
using System.Diagnostics;
using Serilog;
using Serilog.Core;
using Serilog.Sinks.Async;

namespace PolyMount.Core;

/// <summary>Bounded background request tracing, including operations that have not returned.</summary>
public sealed class RequestDiagnostics : IDisposable
{
    private static readonly AsyncLocal<Scope?> Current = new();
    private readonly Logger _logger;
    private readonly QueueMonitor _monitor = new();
    private readonly ConcurrentDictionary<long, Scope> _active = new();
    private sealed class Counter { public long Value; }
    private readonly ConcurrentDictionary<string, Counter> _counts = new();
    private readonly object _inspectionGate = new();
    private readonly Timer _timer;
    private long _sequence;
    private long _lastDropped;
    private bool _disposed;
    private long _lastSummary;

    public RequestDiagnostics(string path)
    {
        _logger = AsyncFileLog.Create(path, _monitor);
        _timer = new Timer(_ => Flush(), null, 1000, 1000);
    }

    public static Scope? Begin(string operation, string path, RequestDiagnostics? diagnostics = null, int processId = 0)
    {
        var parent = Current.Value;
        diagnostics ??= parent?._owner;
        if (diagnostics == null) return null;
        // Layer probes dominate cached directory enumeration. Unsampled parent
        // operations already track total latency and pending work; nested SVN
        // calls still receive their own always-traced scopes.
        if (operation.StartsWith("Layer.", StringComparison.Ordinal) && parent is { Trace: false }) return null;
        var scope = new Scope(diagnostics, parent, operation, path, processId);
        diagnostics._active[scope.Id] = scope;
        Current.Value = scope;
        if (scope.Trace) scope.Emit("BEGIN");
        if (operation.StartsWith("WinFsp.", StringComparison.Ordinal))
            Interlocked.Increment(ref diagnostics._counts.GetOrAdd($"pid={scope.ProcessId} op={operation}", _ => new Counter()).Value);
        return scope;
    }

    public long DroppedMessages => _monitor.Inspector?.DroppedMessagesCount ?? _lastDropped;
    internal Action CallbackCounter(string operation, int processId)
    {
        var counter = _counts.GetOrAdd($"pid={processId} op={operation}", _ => new Counter());
        return () => Interlocked.Increment(ref counter.Value);
    }
    public void Error(string operation, Exception exception) => _logger.Error(exception, "Operation failed: {Operation}", operation);
    private sealed class QueueMonitor : IAsyncLogEventSinkMonitor
    {
        public IAsyncLogEventSinkInspector? Inspector;
        public void StartMonitoring(IAsyncLogEventSinkInspector inspector) => Inspector = inspector;
        public void StopMonitoring(IAsyncLogEventSinkInspector inspector) { }
    }

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
                    if (count > 0) _logger.Information("COUNT {Counter:l} requests={Requests}", pair.Key, count);
                }
            }
            foreach (var scope in _active.Values)
                if (scope.ElapsedMs >= 1000 && scope.ElapsedMs - scope.LastReportMs >= 5000)
                {
                    scope.LastReportMs = scope.ElapsedMs;
                    scope.Emit("PENDING");
                }
            long dropped = DroppedMessages;
            if (dropped > _lastDropped) _logger.Warning("DROPPED count={Count} total={Total}", dropped - _lastDropped, dropped);
            _lastDropped = dropped;
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_inspectionGate)
        {
            Flush();
            _disposed = true;
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
        private string _stage = "running";
        private int _finished;
        internal long Id { get; }
        internal int ProcessId { get; }
        internal bool Trace { get; }
        internal long ElapsedMs => _watch.ElapsedMilliseconds;
        internal long LastReportMs = -5000;

        internal Scope(RequestDiagnostics owner, Scope? parent, string operation, string path, int processId)
        {
            _owner = owner; _parent = parent; _operation = operation;
            _path = path.Replace('\r', ' ').Replace('\n', ' ');
            Id = Interlocked.Increment(ref owner._sequence);
            ProcessId = processId != 0 ? processId : parent?.ProcessId ?? 0;
            Trace = operation.StartsWith("SVN.", StringComparison.Ordinal) ||
                (parent == null ? Id % 128 == 1 : parent.Trace);
        }

        public void Stage(string stage) => Volatile.Write(ref _stage, stage);
        internal void Emit(string kind) => _owner._logger.Information(
            "{Kind:l} id={RequestId} parent={ParentId} thread={ThreadId} pid={ProcessId} op={Operation:l} path='{Path:l}' elapsedMs={ElapsedMs} stage='{Stage:l}'",
            kind, Id, _parent?._owner == _owner ? _parent.Id : 0, _thread, ProcessId, _operation, _path, ElapsedMs, Volatile.Read(ref _stage));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) return;
            _owner._active.TryRemove(Id, out _);
            if (Trace || ElapsedMs >= 100) Emit("END");
            Current.Value = _parent;
        }
    }
}
