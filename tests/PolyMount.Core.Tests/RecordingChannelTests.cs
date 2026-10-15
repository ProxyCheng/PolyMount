using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Core.Ipc;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class RecordingChannelTests
{
    /// <summary>The point of a recording: the work the sampling rule throws away — a layer probe
    /// under an untraced parent — is counted while one is attached, and no line is written about
    /// it anywhere. That is the trade: exact counts instead of a line per request.</summary>
    [TestMethod]
    public async Task Recording_counts_the_probes_the_sampling_rule_discards()
    {
        using var temp = new TempDir();
        string sampled = Path.Combine(temp.Path, "requests.log");
        var channel = new RecordingChannel(temp.Sub("recording"));
        using var diagnostics = new RequestDiagnostics(sampled) { Channel = channel };

        // The sampling rule traces the first root (id 1) and drops the second, so a probe under
        // the second is the kind of work only a recording accounts for.
        using (RequestDiagnostics.Begin("VFS.List", "a", diagnostics)) { }
        Assert.IsNull(RequestDiagnostics.Begin("Layer.GetEntry:cache", "probe"));
        using (var root = RequestDiagnostics.Begin("VFS.List", "b", diagnostics))
        {
            Assert.IsNotNull(root);
            Assert.IsFalse(root!.Trace);
            Assert.IsNull(RequestDiagnostics.Begin("Layer.GetEntry:cache", "b"), "a probe under an untraced parent is dropped while nothing records it");

            var session = channel.Start("test", "recording");
            Assert.IsTrue(session.Active);
            using (RequestDiagnostics.Begin("Layer.GetEntry:cache", "b")) { }
            using (RequestDiagnostics.Begin("Layer.GetEntry:cache", "b")) { }
        }

        var stopped = channel.Stop("stopped");
        Assert.IsFalse(stopped.Active);
        Assert.AreEqual("stopped", stopped.StoppedReason);
        var probe = stopped.Counts!.Operations.Single(operation => operation.Operation == "Layer.GetEntry:cache");
        Assert.AreEqual(2, probe.Count, "the probes the sampling rule discards were not all counted");
        Assert.IsTrue(stopped.Counts.Paths.Any(path => path.Path == "b"), "the counted probes lost their path");
        Assert.IsTrue(stopped.Counts.Events >= 3, "the traced roots were not counted too");

        // The sampled log stays exactly what it was: no line about the probe, in either channel.
        await WaitForLog(sampled, "END id=1 parent=0");
        Assert.IsFalse(ReadLog(sampled).Contains("Layer.GetEntry:cache"), "the sampled log must not gain the probes a recording counts");
    }

    /// <summary>Counting is the part of a recording that has to be right, and it is written from
    /// every request thread at once.</summary>
    [TestMethod]
    public void Counts_are_exact_when_every_request_thread_writes_at_once()
    {
        using var temp = new TempDir();
        string sampled = Path.Combine(temp.Path, "requests.log");
        var channel = new RecordingChannel(temp.Sub("recording"));
        using var diagnostics = new RequestDiagnostics(sampled) { Channel = channel };
        channel.Start("test", "recording");
        const int threads = 8, each = 500;
        var failures = Run(threads, () => WriteEvents(diagnostics, each));
        if (failures.Count > 0) Assert.Fail("a counted request threw: " + failures[0]);

        var counts = channel.Info().Counts!;
        var probes = counts.Operations.Single(operation => operation.Operation == "Layer.GetEntry:cache");
        Assert.AreEqual((long)threads * each, probes.Count, "a count was lost between threads");
        Assert.AreEqual(counts.Events, counts.Operations.Sum(operation => operation.Count), "the operation counts do not add up to the total");
        Assert.AreEqual(counts.TotalMs, counts.Operations.Sum(operation => operation.TotalMs), 0.001, "the operation totals do not add up");
    }

    /// <summary>Starting while a session runs adopts it rather than replacing what it has counted.</summary>
    [TestMethod]
    public void Starting_a_running_session_adopts_it()
    {
        using var temp = new TempDir();
        var channel = new RecordingChannel(temp.Sub("recording"));
        var first = channel.Start("test", "one");
        WriteEvents(new RequestDiagnostics(Path.Combine(temp.Path, "sampled.log")) { Channel = channel }, 20);
        var second = channel.Start("test", "two");
        Assert.AreEqual(first.SessionId, second.SessionId);
        Assert.AreEqual("one", second.Label);
        Assert.AreEqual(20, second.Counts!.Operations.Single(operation => operation.Operation == "Layer.GetEntry:cache").Count);
        channel.Stop("stopped");
    }

    /// <summary>After it is taken down, requests are counted by nothing — there is no queue to
    /// drain and nothing to lose, so stopping is a reference swap and a snapshot.</summary>
    [TestMethod]
    public void A_stopped_recording_counts_nothing_more()
    {
        using var temp = new TempDir();
        string sampled = Path.Combine(temp.Path, "requests.log");
        var channel = new RecordingChannel(temp.Sub("recording"));
        using var diagnostics = new RequestDiagnostics(sampled) { Channel = channel };
        channel.Start("test", "recording");
        WriteEvents(diagnostics, 20);
        var stopped = channel.Stop("stopped");
        long counted = stopped.Counts!.Events;

        WriteEvents(diagnostics, 20);
        Assert.AreEqual(counted, channel.Info().Counts!.Events, "a detached recording counted more events");
    }

    [TestMethod]
    public void Disposing_twice_is_harmless()
    {
        using var temp = new TempDir();
        var channel = new RecordingChannel(temp.Sub("recording"));
        channel.Start("test", "recording");
        channel.Dispose();
        channel.Dispose();
        Assert.IsFalse(channel.Attached);
        Assert.AreEqual("disposed", channel.Info().StoppedReason);
        Assert.ThrowsException<ObjectDisposedException>(() => channel.Start("test", "again"));
    }

    /// <summary>Writes from several threads at once and returns whatever threw.</summary>
    private static List<Exception> Run(int threads, Action write)
    {
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var writers = new List<Thread>();
        using var barrier = new Barrier(threads);
        for (int i = 0; i < threads; i++)
        {
            var writer = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    write();
                }
                catch (Exception ex) { failures.Enqueue(ex); }
            });
            writer.Start();
            writers.Add(writer);
        }
        foreach (var writer in writers) writer.Join();
        return failures.ToList();
    }

    private static void WriteEvents(RequestDiagnostics diagnostics, int count)
    {
        using var root = RequestDiagnostics.Begin("VFS.List", "path", diagnostics);
        for (int i = 0; i < count; i++) using (RequestDiagnostics.Begin("Layer.GetEntry:cache", "path")) { }
    }

    private static async Task WaitForLog(string path, string text)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) && ReadLog(path).Contains(text)) return;
            await Task.Delay(20);
        }
        Assert.Fail("Asynchronous log was not written: " + text);
    }

    private static string ReadLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
