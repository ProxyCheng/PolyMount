using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core.Ipc;
using System.IO.Pipes;

namespace PolyMount.Core.Tests;

/// <summary>
/// The path a front-end takes instead of asking the supervisor about an instance: ask once where
/// every instance is served, then talk to each one's own pipe. Every call here is answered by a
/// pipe that says which service it is, so "who answered" is a fact in the response rather than an
/// assumption about the client.
/// </summary>
[TestClass]
public sealed class InstanceDirectoryTests
{
    [TestMethod]
    public void A_front_end_asks_once_and_then_reaches_the_instance_itself()
    {
        using var alpha = Worker("alpha");
        using var beta = Worker("beta");
        using var supervisor = Supervisor(alpha.Pipe, beta.Pipe);
        using var restore = new SupervisorPipe(supervisor.Pipe);

        Assert.AreEqual("alpha-process", InstanceDirectory.ClientFor("alpha").Status().Mounts.Single().SourceType);
        Assert.AreEqual("beta-process", InstanceDirectory.ClientFor("beta").Status().Mounts.Single().SourceType);
        // Three questions asked, one instance list read: the addresses are the directory's to keep.
        InstanceDirectory.ClientFor("alpha").Status();
        InstanceDirectory.ClientFor("alpha").Status();
        Assert.AreEqual(1, supervisor.Requests);
        Assert.AreEqual(1, beta.Requests);
        // A path is all a window opened from Explorer has: the mount point decides which process
        // is asked, and a path under no mount point is the supervisor's to answer.
        Assert.AreEqual("alpha-process", InstanceDirectory.ClientForPath(@"D:\alpha\sub\file.txt").Status().Mounts.Single().SourceType);
        Assert.AreEqual(supervisor.Pipe, InstanceDirectory.ClientForPath(@"C:\elsewhere\file.txt").PipeName);
        Assert.AreEqual(supervisor.Pipe, InstanceDirectory.ClientFor("nobody").PipeName);
        Assert.AreEqual(supervisor.Pipe, InstanceDirectory.ClientFor(null).PipeName);
        Assert.AreEqual(2, InstanceDirectory.All().Count);
    }

    [TestMethod]
    public void An_unreachable_supervisor_leaves_the_caller_talking_to_it()
    {
        using var restore = new SupervisorPipe("PolyMount-absent-" + Guid.NewGuid().ToString("N"));

        // Nothing to ask: the client is still the one for an instance — that is what the caller
        // means to talk to — addressed to the supervisor, and the call that needed the answer is
        // the one that reports the service is not there.
        string name = "absent-" + Guid.NewGuid().ToString("N");
        var client = InstanceDirectory.ClientFor(name);
        Assert.AreEqual(SupervisorClient.Default.PipeName, client.PipeName);
        Assert.AreEqual(name, client.Instance);
    }

    /// <summary>Points the directory at a test's own supervisor, and puts back what it found.</summary>
    private sealed class SupervisorPipe : IDisposable
    {
        private readonly SupervisorClient _previous = SupervisorClient.Default;

        public SupervisorPipe(string pipe)
        {
            SupervisorClient.Default = new SupervisorClient(pipe);
            InstanceDirectory.Invalidate();
        }

        public void Dispose()
        {
            SupervisorClient.Default = _previous;
            InstanceDirectory.Invalidate();
        }
    }

    private static FakeControlService Worker(string name) => new("PolyMount-" + name + "-" + Guid.NewGuid().ToString("N"),
        _ => new ControlResponse { Mounts = { new MountStatus { Name = name, SourceType = name + "-process" } } });

    private static FakeControlService Supervisor(string alphaPipe, string betaPipe) => new(
        "PolyMount-supervisor-" + Guid.NewGuid().ToString("N"),
        request => request.Cmd == ControlCommands.Instances
            ? new ControlResponse
            {
                Instances =
                {
                    new InstanceEndpoint { Id = "alpha-id", Name = "alpha", MountPoint = @"D:\alpha", Pipe = alphaPipe, WorkerPid = 11 },
                    new InstanceEndpoint { Id = "beta-id", Name = "beta", MountPoint = @"D:\beta", Pipe = betaPipe, WorkerPid = 22 }
                }
            }
            : new ControlResponse());

    /// <summary>A pipe that answers the control protocol, counting what it was asked. One request
    /// per connection, the way the real one works, so a client that asks twice is two requests.</summary>
    private sealed class FakeControlService : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _requests;

        public FakeControlService(string pipe, Func<ControlRequest, ControlResponse> answer)
        {
            Pipe = pipe;
            _loop = Task.Run(() => Serve(pipe, answer));
        }

        public string Pipe { get; }
        public int Requests => Volatile.Read(ref _requests);

        private async Task Serve(string pipe, Func<ControlRequest, ControlResponse> answer)
        {
            while (!_stop.IsCancellationRequested)
            {
                using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try { await server.WaitForConnectionAsync(_stop.Token); }
                catch (OperationCanceledException) { return; }
                try
                {
                    using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
                    using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                    string? line = await reader.ReadLineAsync(_stop.Token);
                    if (line == null) continue;
                    Interlocked.Increment(ref _requests);
                    var response = answer(JsonSerializer.Deserialize<ControlRequest>(line) ?? new ControlRequest());
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response));
                }
                catch (OperationCanceledException) { return; }
                catch (IOException) { }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _loop.Wait(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { }
            _stop.Dispose();
        }
    }
}
