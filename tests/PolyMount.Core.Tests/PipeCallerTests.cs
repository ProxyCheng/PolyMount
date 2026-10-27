using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core.Ipc;
using System.IO.Pipes;
using System.Security.Principal;

namespace PolyMount.Core.Tests;

/// <summary>Who the server thinks is on the other end of a control-pipe connection: the account is
/// read from the connected process, and when it cannot be read the caller is told why. The install
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
}
