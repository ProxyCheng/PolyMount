using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Core.Ipc;

namespace PolyMount.Core.Tests;

/// <summary>
/// A plugin's management page: the list a sidebar is built from, and the view a window draws. What
/// has to be pinned is that Core carries both without reading them — the page is data the plugin
/// produced, the buttons are whole control requests, and a plugin that falls over filling its page
/// is a page with a sentence on it rather than a failed command.
/// </summary>
[TestClass]
public class PluginPanelTests
{
    [TestMethod]
    public void A_panel_travels_across_the_wire()
    {
        var view = new PluginPanelView { Summary = new LocalizedText("3 programs", "3 个程序") };
        view.Facts.Add(new PluginPanelFact { Label = new LocalizedText("Payload", "载荷"), Value = new LocalizedText("deployed", "在位") });
        view.Columns.Add(new LocalizedText("Program", "程序"));
        var row = new PluginPanelRow { Id = "a", Cells = { "Editor.exe" } };
        row.Actions.Add(new PluginPanelAction
        {
            Id = "remove",
            Label = new LocalizedText("Remove", "移除"),
            Confirm = new LocalizedText("Sure?", "确定？"),
            Request = new ControlRequest { Cmd = "example.command", Action = "remove", Path = @"D:\a.exe" }
        });
        view.Rows.Add(row);
        view.Actions.Add(new PluginPanelAction
        {
            Id = "install",
            Label = new LocalizedText("Add a folder", "添加目录"),
            Input = PluginPanelInput.Directory,
            Request = new ControlRequest { Cmd = "example.command", Action = "install" }
        });

        var response = new ControlResponse { Panel = view };
        response.Panels.Add(new PluginPanelDescriptor { Id = "example", Title = new LocalizedText("Examples", "示例"), Icon = "plugin" });

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var back = JsonSerializer.Deserialize<ControlResponse>(JsonSerializer.Serialize(response, options), options)!;

        Assert.AreEqual(1, back.Panels.Count, "the list a sidebar is built from has to survive the wire");
        Assert.AreEqual("示例", back.Panels[0].Title.Resolve("zh-CN"));
        var panel = back.Panel!;
        Assert.AreEqual("3 programs", panel.Summary!.Resolve("en"));
        Assert.AreEqual("deployed", panel.Facts[0].Value.Resolve("en"));
        Assert.AreEqual("程序", panel.Columns[0].Resolve("zh-CN"));
        Assert.AreEqual("Editor.exe", panel.Rows[0].Cells[0]);
        Assert.AreEqual("Sure?", panel.Rows[0].Actions[0].Confirm!.Resolve("en"), "a button that cannot be undone says so");
        Assert.AreEqual(@"D:\a.exe", panel.Rows[0].Actions[0].Request.Path);
        Assert.AreEqual(PluginPanelInput.Directory, panel.Actions[0].Input, "the window has to know it must ask for a folder");
    }

    [TestMethod]
    public void The_list_is_what_a_sidebar_is_built_from()
    {
        PluginPanels.Clear();
        try
        {
            PluginPanels.Register(new PluginPanel("example", new LocalizedText("Examples", "示例"), "plugin",
                _ => new PluginPanelView()));
            PluginPanels.Register(new PluginPanel("own", new LocalizedText("Own page", "自己的页"), "settings",
                _ => new PluginPanelView(), OwnPage: true));

            var descriptors = PluginPanels.Descriptors();
            Assert.AreEqual(2, descriptors.Count);
            Assert.AreEqual("example", descriptors[0].Id);
            Assert.AreEqual("plugin", descriptors[0].Icon);
            Assert.AreEqual("Examples", descriptors[0].Title.Resolve("en"));
            Assert.IsFalse(descriptors[0].OwnPage,
                "a page is part of the plugin manager unless the plugin asked for a destination of its own");
            Assert.IsTrue(descriptors[1].OwnPage, "a plugin that asked for its own page has to be listed as one");
        }
        finally { PluginPanels.Clear(); }
    }

    [TestMethod]
    public void Two_plugins_cannot_answer_for_one_page()
    {
        PluginPanels.Clear();
        try
        {
            PluginPanels.Register(new PluginPanel("example", new LocalizedText("One"), "plugin", _ => new PluginPanelView()));
            Assert.ThrowsException<InvalidOperationException>(() =>
                PluginPanels.Register(new PluginPanel("EXAMPLE", new LocalizedText("Two"), "plugin", _ => new PluginPanelView())));
        }
        finally { PluginPanels.Clear(); }
    }

    [TestMethod]
    public void A_render_that_throws_is_its_own_error()
    {
        PluginPanels.Clear();
        try
        {
            PluginPanels.Register(new PluginPanel("example", new LocalizedText("Examples"), "plugin",
                _ => throw new InvalidOperationException("the machine is on fire")));

            var view = PluginPanels.Render("example", null!);
            Assert.AreEqual("the machine is on fire", view.Error!.Resolve("en"),
                "the reader is owed the sentence rather than a page that quietly lists nothing");
        }
        finally { PluginPanels.Clear(); }
    }

    [TestMethod]
    public void A_page_nobody_offers_is_an_error()
    {
        PluginPanels.Clear();
        Assert.ThrowsException<InvalidOperationException>(() => PluginPanels.Render("missing", null!));
    }
}
