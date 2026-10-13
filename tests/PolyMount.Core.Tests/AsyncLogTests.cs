using Microsoft.VisualStudio.TestTools.UnitTesting;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.Async;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class AsyncLogTests
{
    private sealed class BlockedSink : ILogEventSink
    {
        public readonly ManualResetEventSlim Entered = new(), Release = new();
        public void Emit(LogEvent logEvent) { Entered.Set(); Release.Wait(TimeSpan.FromSeconds(10)); }
    }
    private sealed class Monitor : IAsyncLogEventSinkMonitor
    {
        public IAsyncLogEventSinkInspector Inspector = null!;
        public void StartMonitoring(IAsyncLogEventSinkInspector inspector) => Inspector = inspector;
        public void StopMonitoring(IAsyncLogEventSinkInspector inspector) { }
    }
    [TestMethod]
    public async Task Full_async_queue_drops_events_without_waiting_for_blocked_sink()
    {
        var sink = new BlockedSink(); var monitor = new Monitor();
        using var logger = new LoggerConfiguration().WriteTo.Async(c => c.Sink(sink),
            bufferSize: 8, blockWhenFull: false, monitor: monitor).CreateLogger();
        try
        {
            logger.Information("First event");
            Assert.IsTrue(sink.Entered.Wait(TimeSpan.FromSeconds(5)));
            await Task.Run(() =>
            {
                for (int i = 0; i < 100; i++) logger.Information("Event {Number}", i);
            }).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsTrue(monitor.Inspector.DroppedMessagesCount > 0);
            Assert.IsTrue(monitor.Inspector.Count <= 8);
        }
        finally { sink.Release.Set(); }
    }
}
