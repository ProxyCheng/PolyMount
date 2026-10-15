using Serilog;
using Serilog.Core;
using Serilog.Sinks.Async;

namespace PolyMount.Core;

/// <summary>Shared Serilog sink policy; no application-owned file queue or rotation.</summary>
public static class AsyncFileLog
{
    public const string OutputTemplate = "{Timestamp:O} {Level:u3} {Message:lj}{NewLine}{Exception}";

    /// <summary>The sampled diagnostics log: small segments, and only the newest two kept.
    /// A recording uses the overload below instead — it must not lose its beginning.</summary>
    public const long SegmentBytes = 8 * 1024 * 1024;
    public const int RetainedSegments = 2;

    public static Logger Create(string path, IAsyncLogEventSinkMonitor? monitor = null) =>
        Create(path, monitor, SegmentBytes, RetainedSegments);

    /// <summary>Rolled at <paramref name="segmentBytes"/>; a null <paramref name="retainedSegments"/>
    /// keeps every segment, which is what a recording needs: the oldest part of it is the part
    /// that cannot be re-run.</summary>
    public static Logger Create(string path, IAsyncLogEventSinkMonitor? monitor, long segmentBytes, int? retainedSegments,
        int bufferSize = 4096) =>
        new LoggerConfiguration().MinimumLevel.Information()
            .WriteTo.Async(sink => sink.File(path, outputTemplate: OutputTemplate,
                fileSizeLimitBytes: segmentBytes, rollOnFileSizeLimit: true,
                retainedFileCountLimit: retainedSegments, shared: false),
                bufferSize: bufferSize, blockWhenFull: false, monitor: monitor)
            .CreateLogger();
    /// <summary>A file the caller's own thread writes to, for a sink that already has a thread of
    /// its own: no queue to overflow and nothing rendered on the thread that logs.</summary>
    public static Logger CreateSynchronous(string path, long segmentBytes, int? retainedSegments) =>
        new LoggerConfiguration().MinimumLevel.Information()
            .WriteTo.File(path, outputTemplate: OutputTemplate,
                fileSizeLimitBytes: segmentBytes, rollOnFileSizeLimit: true,
                retainedFileCountLimit: retainedSegments, shared: false, buffered: true)
            .CreateLogger();
}

/// <summary>What one async sink had to drop. A log that silently lost lines is worse than one
/// that says how many it lost, so every channel that matters reports this.</summary>
public sealed class AsyncLogMonitor : IAsyncLogEventSinkMonitor
{
    private IAsyncLogEventSinkInspector? _inspector;

    /// <summary>Dropped so far, or null while the sink has not reported yet.</summary>
    public long? Dropped => _inspector?.DroppedMessagesCount;

    public void StartMonitoring(IAsyncLogEventSinkInspector inspector) => _inspector = inspector;
    public void StopMonitoring(IAsyncLogEventSinkInspector inspector) { }
}
