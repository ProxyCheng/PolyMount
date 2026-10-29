using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Core.Ipc;

namespace PolyMount.Core.Tests;

/// <summary>
/// The advisory channel: a plugin says something, a window shows it and sends back the command it
/// carries. What has to be pinned is that Core carries a notice without reading it — the action is
/// a whole request, so it survives the wire and can be sent back unattended — and that a provider
/// that has nothing to say, or falls over, does not take the poll with it.
/// </summary>
[TestClass]
public class PluginNoticeTests
{
    [TestMethod]
    public void A_notice_carries_its_action_across_the_wire()
    {
        var response = new ControlResponse();
        response.Notices.Add(new PluginNotice(
            "example:one",
            new LocalizedText("Title", "标题"),
            new LocalizedText("Body", "正文"),
            new LocalizedText("Do it", "去做"),
            new ControlRequest { Cmd = "example.command", Action = "fix", Path = @"D:\thing.exe" }));

        string json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var back = JsonSerializer.Deserialize<ControlResponse>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.AreEqual(1, back.Notices.Count);
        var notice = back.Notices[0];
        Assert.AreEqual("example:one", notice.Id);
        Assert.AreEqual("标题", notice.Title.Resolve("zh-CN"));
        Assert.AreEqual("Body", notice.Body.Resolve("en"));
        Assert.AreEqual("Do it", notice.ActionLabel!.Resolve("en"), "the label a window shows is the plugin's, in the reader's language");
        Assert.AreEqual("example.command", notice.Action!.Cmd);
        Assert.AreEqual("fix", notice.Action.Action);
        Assert.AreEqual(@"D:\thing.exe", notice.Action.Path);
    }

    [TestMethod]
    public void A_provider_that_throws_does_not_take_the_poll_with_it()
    {
        PluginNotices.Clear();
        PluginNotices.Register(_ => throw new InvalidOperationException("this plugin is broken"));
        PluginNotices.Register(_ => new[] { new PluginNotice("kept", new LocalizedText("t"), new LocalizedText("b")) });
        try
        {
            var notices = PluginNotices.Collect(null!);
            Assert.AreEqual(1, notices.Count, "the provider that answered should still be heard");
            Assert.AreEqual("kept", notices[0].Id);
        }
        finally { PluginNotices.Clear(); }
    }

    [TestMethod]
    public void With_no_providers_there_is_nothing_to_ask_for()
    {
        PluginNotices.Clear();
        Assert.IsFalse(PluginNotices.Any);
        Assert.AreEqual(0, PluginNotices.Collect(null!).Count);
    }
}
