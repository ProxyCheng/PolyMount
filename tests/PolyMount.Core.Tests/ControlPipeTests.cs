using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core.Ipc;

namespace PolyMount.Core.Tests;

[TestClass]
public class ControlPipeTests
{
    // What these pin is the shape the installed product needs, not this machine's account: the
    // service listens under an account of its own and the tray connects as the signed-in user.
    // The Win32 default descriptor grants the interactive user read access only, which a duplex
    // control client cannot use — the failure reaches the caller as "Access to the path is
    // denied." and looks like a service that is running and unreachable at the same time.

    private static List<PipeAccessRule> Rules() =>
        ControlPipe.Security().GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToList();

    [TestMethod]
    public void An_interactive_session_may_read_and_write()
    {
        var interactive = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
        var rule = Rules().SingleOrDefault(r => interactive.Equals(r.IdentityReference));
        Assert.IsNotNull(rule, "the tray, an MCP server and the Explorer overlay all run in one");
        Assert.AreEqual(AccessControlType.Allow, rule.AccessControlType);
        Assert.IsTrue(rule.PipeAccessRights.HasFlag(PipeAccessRights.ReadData), "a client reads the answer");
        Assert.IsTrue(rule.PipeAccessRights.HasFlag(PipeAccessRights.WriteData), "a client writes the request");
    }

    [TestMethod]
    public void The_listening_account_keeps_full_control()
    {
        // One pipe instance per connection: creating the next one is an access check of its own,
        // so the account that listens has to match a full-control rule whatever account it is.
        var rule = Rules().SingleOrDefault(r => WindowsIdentity.GetCurrent().User is { } user && user.Equals(r.IdentityReference));
        Assert.IsNotNull(rule, "the service account is named explicitly, because it is not the client's");
        Assert.AreEqual(PipeAccessRights.FullControl, rule.PipeAccessRights);
        Assert.AreEqual(AccessControlType.Allow, rule.AccessControlType);
    }

    [TestMethod]
    public void System_and_administrators_are_unchanged_from_the_platform_default()
    {
        foreach (var type in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            var sid = new SecurityIdentifier(type, null);
            var rule = Rules().SingleOrDefault(r => sid.Equals(r.IdentityReference));
            Assert.IsNotNull(rule, type.ToString());
            Assert.AreEqual(AccessControlType.Allow, rule.AccessControlType);
            Assert.AreEqual(PipeAccessRights.FullControl, rule.PipeAccessRights);
        }
    }

    [TestMethod]
    public void Nothing_is_denied_outright()
    {
        // A network logon simply does not carry the INTERACTIVE SID, so staying off the list is
        // enough; a deny rule here would be one more thing every future account has to undo.
        Assert.AreEqual(0, Rules().Count(r => r.AccessControlType == AccessControlType.Deny));
    }

    [TestMethod]
    public void A_pipe_created_from_the_rules_accepts_a_duplex_client()
    {
        string name = "polymount-test-" + Guid.NewGuid().ToString("N");
        using var server = ControlPipe.CreateServer(name);
        var waiting = server.WaitForConnectionAsync();
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        client.Connect(3000);
        Assert.IsTrue(waiting.Wait(3000), "the client connected but the server never saw it");
    }
}
