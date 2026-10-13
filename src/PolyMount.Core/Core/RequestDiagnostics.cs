using System.Collections.Concurrent;
using System.Diagnostics;

namespace PolyMount.Core;

/// <summary>Bounded background request tracing, including operations that have not returned.</summary>
public sealed class RequestDiagnostics : IDisposable
{
    private static readonly AsyncLocal<Scope?> Current = new();
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly ConcurrentDictionary<long, Scope> _active = new();
    private sealed class Counter { public long Value; }
    private readonly ConcurrentDictionary<string, Counter> _counts = new();
    private readonly object _writerGate = new();
    private readonly string _path;
    private readonly Timer _timer;
    private long _sequence;
    private int _queued, _dropped;
    private bool _disposed;
    private long _lastSummary;

    public RequestDiagnostics(string path)
    {
        _path = path;
        _timer = new Timer(_ => Flush(), null, 1000, 1000);
    }

    public static Scope? Begin(string operation, string path, RequestDiagnostics? diagnostics = null, int processId = 0)
    {
        var parent = Current.Value;
        diagnostics ??= parent?._owner;
        if (diagnostics == null) return null;
        var scope = new Scope(diagnostics, parent, operation, path, processId);
        diagnostics._active[scope.Id] = scope;
        Current.Value = scope;
        if (scope.Trace) diagnostics.Enqueue(scope.Line("BEGIN"));
        if (operation.StartsWith("WinFsp.", StringComparison.Ordinal))
            Interlocked.Increment(ref diagnostics._counts.GetOrAdd($"pid={scope.ProcessId} op={operation}", _ => new Counter()).Value);
        return scope;
    }

    private void Enqueue(string line)
    {
        if (Interlocked.Increment(ref _queued) > 4096)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _dropped);
            return;
        }
        _lines.Enqueue(line);
    }

    internal void Flush()
    {
        lock (_writerGate)
        {
            if (_disposed) return;
            if (Environment.TickCount64 - _lastSummary >= 5000)
            {
                _lastSummary = Environment.TickCount64;
                foreach (var pair in _counts)
                {
                    long count = Interlocked.Exchange(ref pair.Value.Value, 0);
                    if (count > 0) Enqueue($"{DateTime.UtcNow:O} COUNT {pair.Key} requests={count}");
                }
            }
            foreach (var scope in _active.Values)
                if (scope.ElapsedMs >= 1000 && scope.ElapsedMs - scope.LastReportMs >= 5000)
                {
                    scope.LastReportMs = scope.ElapsedMs;
                    Enqueue(scope.Line("PENDING"));
                }
            try
            {
                // Keep one previous segment; file I/O never runs on a filesystem callback.
                if (File.Exists(_path) && new FileInfo(_path).Length >= 8 * 1024 * 1024)
                    File.Move(_path, _path + ".1", overwrite: true);
                using var writer = new StreamWriter(_path, append: true);
                int dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0) writer.WriteLine($"{DateTime.UtcNow:O} DROPPED count={dropped}");
                for (int i = 0; i < 4096 && _lines.TryDequeue(out var line); i++)
                {
                    Interlocked.Decrement(ref _queued);
                    writer.WriteLine(line);
                }
            }
            catch { /* Diagnostics must not prevent mounted filesystem access. */ }
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_writerGate)
        {
            Flush();
            _disposed = true;
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
        internal string Line(string kind) => $"{DateTime.UtcNow:O} {kind} id={Id} parent={(_parent?._owner == _owner ? _parent.Id : 0)} thread={_thread} pid={ProcessId} op={_operation} path='{_path}' elapsedMs={ElapsedMs} stage='{Volatile.Read(ref _stage)}'";

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) return;
            _owner._active.TryRemove(Id, out _);
            if (Trace || ElapsedMs >= 100) _owner.Enqueue(Line("END"));
            Current.Value = _parent;
        }
    }
}
