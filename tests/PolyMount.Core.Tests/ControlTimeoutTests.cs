using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core.Ipc;
using System.IO.Pipes;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class ControlTimeoutTests
{
    [TestMethod]
    public async Task Connected_but_unresponsive_service_times_out()
    {
        string previousPipe = ControlClient.PipeName;
        int previousTimeout = ControlClient.ResponseTimeoutMs;
        string name = "PolyMount-timeout-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        try
        {
            ControlClient.PipeName = name;
            ControlClient.ResponseTimeoutMs = 100;
            var connected = server.WaitForConnectionAsync();
            var request = Task.Run(() => Assert.ThrowsException<TimeoutException>(() => ControlClient.Status()));
            await connected.WaitAsync(TimeSpan.FromSeconds(5));
            using var reader = new StreamReader(server, leaveOpen: true);
            await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await request.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            ControlClient.PipeName = previousPipe;
            ControlClient.ResponseTimeoutMs = previousTimeout;
        }
    }
}
