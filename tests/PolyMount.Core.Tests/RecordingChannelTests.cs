using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Core.Ipc;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class RecordingChannelTests
{
    /// <summary>The point of the channel: the work the sampling rule throws away — a layer probe
    /// under an untraced parent — is exactly what a recording is for, and while the channel is
    /// attached it is written there and nowhere else.</summary>
    [TestMethod]
    public async Task Recording_carries_the_layer_probes_the_sampling_rule_discards()
    {
        using var temp = new TempDir();
        string sampled = Path.Combine(temp.Path, "requests.log");
        var channel = new RecordingChannel(temp.Sub("recording"));
        using var diagnostics = new RequestDiagnostics(sampled) { Channel = channel };

        // The sampling rule traces the first root (id 1) and drops the second, so a probe under
        // the second is the kind of work only a recording can see.
        using (RequestDiagnostics.Begin("VFS.List", "a", diagnostics)) { }
        Assert.IsNull(RequestDiagnostics.Begin("Layer.GetEntry:cache", "probe"));
        using (var root = RequestDiagnostics.Begin("VFS.List", "b", diagnostics))
        {
            Assert.IsNotNull(root);
            Assert.IsFalse(root!.Trace);
            Assert.IsNull(RequestDiagnostics.Begin("Layer.GetEntry:cache", "b"), "a probe under an untraced parent is dropped while nothing records it");

            var session = channel.Start("test", "recording");
            Assert.IsTrue(session.Active);
            Assert.IsTrue(session.Bytes >= 0);

            using var probe = RequestDiagnostics.Begin("Layer.GetEntry:cache", "b");
            Assert.IsNotNull(probe, "a recording wants the probe the sampling rule discards");
            Assert.IsTrue(probe!.Id < 0, "a scope the rule would have dropped is marked by its id");
            probe.Stage("entries=1");
        }

        var stopped = channel.Stop("stopped");
        Assert.IsFalse(stopped.Active);
        Assert.AreEqual("stopped", stopped.StoppedReason);
        Assert.IsTrue(stopped.Bytes > 0, "the recording holds the events written while it was attached");
        string recording = ReadRecording(stopped);
        StringAssert.Contains(recording, "Layer.GetEntry:cache");
        StringAssert.Contains(recording, "stage='entries=1'");
        StringAssert.Contains(recording, "op=VFS.List");

        // The sampled log stays exactly what it was: the probe never reaches it.
        await WaitForLog(sampled, "END id=1 parent=0");
        Assert.IsFalse(ReadLog(sampled).Contains("Layer.GetEntry:cache"), "the sampled log must not gain the probes a recording captured");
    }

    /// <summary>Starting while a session runs adopts it rather than replacing the evidence.</summary>
    [TestMethod]
    public void Starting_a_running_session_adopts_it()
    {
        using var temp = new TempDir();
        var channel = new RecordingChannel(temp.Sub("recording"));
        var first = channel.Start("test", "one");
        var second = channel.Start("test", "two");
        Assert.AreEqual(first.SessionId, second.SessionId);
        Assert.AreEqual("one", second.Label);
        channel.Stop("stopped");
    }

    /// <summary>After it is taken down the channel writes nothing more, and no thread that was in
    /// flight when it went down can reach a closed sink.</summary>
    [TestMethod]
    public void A_stopped_recording_stops_growing()
    {
        using var temp = new TempDir();
        string sampled = Path.Combine(temp.Path, "requests.log");
        var channel = new RecordingChannel(temp.Sub("recording"));
        using var diagnostics = new RequestDiagnostics(sampled) { Channel = channel };
        channel.Start("test", "recording");
        WriteEvents(diagnostics, 20);
        var stopped = channel.Stop("stopped");
        long lines = LineCount(stopped);

        WriteEvents(diagnostics, 20);
        diagnostics.Flush();
        Assert.AreEqual(lines, LineCount(stopped), "a detached channel writes nothing more");
    }

    /// <summary>Reaching the ceiling ends the session and says so, rather than quietly dropping
    /// the beginning of the recording.</summary>
    [TestMethod]
    public void Reaching_the_capacity_stops_the_session_and_says_why()
    {
        using var temp = new TempDir();
        string sampled = Path.Combine(temp.Path, "requests.log");
        var channel = new RecordingChannel(temp.Sub("recording")) { CapacityBytes = 0 };
        using var diagnostics = new RequestDiagnostics(sampled) { Channel = channel };
        channel.Start("test", "recording");
        Assert.IsTrue(channel.Attached);

        diagnostics.Flush();
        Assert.IsFalse(channel.Attached, "the flush that finds the ceiling ends the recording");
        Assert.AreEqual("capacity", channel.Info().StoppedReason);
        Assert.IsFalse(channel.Info().Active);
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

    /// <summary>Sessions are kept as directories beside the sampled log, and only the newest one
    /// survives the next start, because the front-end may still be draining the last one.</summary>
    [TestMethod]
    public void Starting_prunes_earlier_sessions_but_keeps_the_newest()
    {
        using var temp = new TempDir();
        string root = temp.Sub("recording");
        var channel = new RecordingChannel(root);
        channel.Start("test", "one");
        string first = channel.Info().Directory;
        channel.Stop("stopped");
        Directory.CreateDirectory(Path.Combine(root, "20000101-000000"));
        channel.Start("test", "two");
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "20000101-000000")), "older sessions are cleared");
        Assert.IsTrue(Directory.Exists(first), "the session just stopped is still there to be drained");
        channel.Stop("stopped");
    }

    private static void WriteEvents(RequestDiagnostics diagnostics, int count)
    {
        using var root = RequestDiagnostics.Begin("VFS.List", "path", diagnostics);
        for (int i = 0; i < count; i++) using (RequestDiagnostics.Begin("Layer.GetEntry:cache", "path")) { }
    }

    private static long LineCount(RecordingSessionInfo info)
    {
        long lines = 0;
        foreach (var segment in info.Segments) lines += File.ReadAllLines(segment.Source).Length;
        return lines;
    }

    private static string ReadRecording(RecordingSessionInfo info)
    {
        var text = new System.Text.StringBuilder();
        foreach (var segment in info.Segments)
        {
            using var stream = new FileStream(segment.Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text.Append(reader.ReadToEnd());
        }
        return text.ToString();
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
