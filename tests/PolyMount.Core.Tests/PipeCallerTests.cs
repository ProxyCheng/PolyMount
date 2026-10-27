using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core.Ipc;
using System.IO.Pipes;
using System.Security.Principal;

namespace PolyMount.Core.Tests;

/// <summary>Who the server thinks is on the other end of a control-pipe connection: the account is
/// read through the caller's process, and when it cannot be read the caller is told why. The install
/// check found the hard way that the service's own account may not be able to open the caller's
/// process at all (LocalService cannot open one owned by the user who installed the product), which
/// is a decision for the server rather than something to leave implicit.</summary>
[TestClass]
public sealed class PipeCallerTests
{
    [TestMethod]
    public void The_client_side_of_a_connection_names_the_account_that_connected()
    {
        string name = "PolyMount-caller-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync();
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        client.Connect(5000);
        Assert.IsTrue(accepted.Wait(TimeSpan.FromSeconds(5)), "the server never accepted the connection");

        var identity = PipeCaller.Client(server, out string reason);
        Assert.IsNotNull(identity, "the caller was not identified: " + reason);
        string expected = WindowsIdentity.GetCurrent().User?.Value ?? "";
        Assert.IsFalse(string.IsNullOrEmpty(expected), "the test account has no SID");
        Assert.AreEqual(expected, identity!.UserSid);
        Assert.AreEqual(Environment.ProcessId, identity.ProcessId);
        Assert.AreEqual("", reason, "a successful identification carries no reason");
    }

    [TestMethod]
    public void Naming_a_caller_leaves_the_thread_as_it_found_it()
    {
        // The request handler goes on to do the service's work on this thread, so a token left
        // behind would run that work as the caller. This is a regression guard: impersonating the
        // connection (ImpersonateNamedPipeClient) gives the account without opening the caller's
        // process, and it was taken back out because the token did not stay on the thread that
        // asked — the next directory such a thread created failed with
        // ERROR_BAD_IMPERSONATION_LEVEL. Both kinds of client are asked, because a client that
        // requests identification is the one that used to leave it.
        Ask(TokenImpersonationLevel.Identification);
        Ask(null);

        string dir = Path.Combine(Path.GetTempPath(), "PolyMount-token-" + Guid.NewGuid().ToString("N"));
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex)
        {
            Assert.Fail($"the thread is still impersonating a caller: {ex.GetType().Name}: {ex.Message}");
        }
        finally { try { Directory.Delete(dir); } catch { } }

        static void Ask(TokenImpersonationLevel? level)
        {
            string name = "PolyMount-token-" + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var accepted = server.WaitForConnectionAsync();
            using var client = level is { } l
                ? new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, l)
                : new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(5000);
            Assert.IsTrue(accepted.Wait(TimeSpan.FromSeconds(5)), "the server never accepted the connection");
            var identity = PipeCaller.Client(server, out string reason);
            Assert.IsNotNull(identity, reason);
            Assert.IsFalse(string.IsNullOrEmpty(identity!.ImagePath), "the process route reads the image too");
        }
    }
}
