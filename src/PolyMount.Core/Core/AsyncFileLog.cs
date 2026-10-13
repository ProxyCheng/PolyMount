using Serilog;
using Serilog.Core;
using Serilog.Sinks.Async;

namespace PolyMount.Core;

/// <summary>Shared Serilog sink policy; no application-owned file queue or rotation.</summary>
public static class AsyncFileLog
{
    public const string OutputTemplate = "{Timestamp:O} {Level:u3} {Message:lj}{NewLine}{Exception}";
    public static Logger Create(string path, IAsyncLogEventSinkMonitor? monitor = null) =>
        new LoggerConfiguration().MinimumLevel.Information()
            .WriteTo.Async(sink => sink.File(path, outputTemplate: OutputTemplate,
                fileSizeLimitBytes: 8 * 1024 * 1024, rollOnFileSizeLimit: true,
                retainedFileCountLimit: 2, shared: false),
                bufferSize: 4096, blockWhenFull: false, monitor: monitor)
            .CreateLogger();
}
