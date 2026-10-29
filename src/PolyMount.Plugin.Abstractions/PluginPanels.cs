using PolyMount.Plugins.Ipc;

namespace PolyMount.Plugins;

/// <summary>A place in a front end that belongs to one plugin, and how to fill it.
///
/// The product's windows are built from descriptors everywhere else — credential kinds, layer
/// fields, notices — because Core and the tray are not allowed to learn what a plugin is about.
/// A panel is the same idea at the scale of a page: the plugin says what it is called and hands
/// back a view whenever it is asked, and the window draws what it is given. Nothing in the tray
/// knows that one of these is about transactional opens.
///
/// <para><see cref="Id"/> is the panel's address for the life of the process: unique across every
/// loaded plugin, because two plugins naming one id would be one page answering for the other.
/// </para>
/// <para><see cref="OwnPage"/> decides where the page is shown. By default it is part of the
/// plugin manager: the plugin's own row in that page unfolds into it, so all of one plugin — what
/// it is, what it adds and what can be done about it — is in one place, and a sidebar that lists
/// four destinations does not grow by one for every plugin installed. A plugin whose page is
/// something a person opens often says so here, and the page also becomes a destination of its own
/// in the sidebar; that is the plugin asking for the reader's attention, not a requirement.
/// </para>
/// <para><see cref="Render"/> runs in the process that answers for an instance, so it may read
/// that instance's mount. It is asked again after every action the reader takes, and on every
/// visit, because what a panel shows is state rather than a document.</para>
/// <para>Only a plugin the backend processes load can own a panel: a worker or the supervisor
/// answers, and an assembly declared for front ends alone is never asked. A panel that has to
/// know about one mount says so in what it renders rather than reaching for one elsewhere — the
/// instance it is given is that mount, and nothing else is in scope.</para>
/// </summary>
public sealed record PluginPanel(
    string Id,
    LocalizedText Title,
    string Icon,
    Func<IPluginInstance, PluginPanelView> Render,
    bool OwnPage = false);

/// <summary>What a panel is, for the list a front end builds before anything is rendered.</summary>
public sealed class PluginPanelDescriptor
{
    public string Id { get; set; } = "";
    public LocalizedText Title { get; set; } = new();
    /// <summary>One of the tray's line-icon names; empty falls back to a generic mark.</summary>
    public string Icon { get; set; } = "";
    /// <summary>Whether this page is a destination of its own in the sidebar, beside the plugin
    /// manager, rather than only unfolded inside it.</summary>
    public bool OwnPage { get; set; }
}

/// <summary>What a panel shows at one moment: a few facts, a list, and what can be done about it.
///
/// Everything here is data the plugin produced. The window lays it out and sends the actions back
/// as the control requests they are, so a panel gains a button without the tray changing and a
/// button does what the plugin says it does.
/// </summary>
public sealed class PluginPanelView
{
    /// <summary>One sentence under the heading: what this panel is looking at right now.</summary>
    public LocalizedText? Summary { get; set; }

    /// <summary>Named values shown above the list, each already written as the reader sees it.</summary>
    public List<PluginPanelFact> Facts { get; set; } = new();

    /// <summary>Column headings for <see cref="Rows"/>. Localized here and literal in the cells:
    /// a heading is a word of the interface, a cell is a fact.</summary>
    public List<LocalizedText> Columns { get; set; } = new();

    public List<PluginPanelRow> Rows { get; set; } = new();

    /// <summary>What to do about the whole panel rather than one row.</summary>
    public List<PluginPanelAction> Actions { get; set; } = new();

    /// <summary>Said instead of an empty list, when there is nothing to list.</summary>
    public LocalizedText? Empty { get; set; }

    /// <summary>Said instead of everything above, when the panel could not be built.</summary>
    public LocalizedText? Error { get; set; }
}

/// <summary>One named value: the plugin writes both halves, the window aligns them. The value is
/// localized too, because half of what a panel says is a word ("missing", "agree") and the other
/// half is a fact ("C:\…", "3"); a plugin should not have to choose a language for the first.</summary>
public sealed class PluginPanelFact
{
    public LocalizedText Label { get; set; } = new();
    public LocalizedText Value { get; set; } = new();
}

/// <summary>One line of the list. <see cref="Id"/> is what an action of this row is about; a row
/// whose actions carry their own request does not need one.</summary>
public sealed class PluginPanelRow
{
    public string Id { get; set; } = "";
    /// <summary>The cells, in <see cref="PluginPanelView.Columns"/> order. A cell is written by the
    /// plugin and drawn as it is, shortened in the middle when it is longer than a line — so the
    /// path in a row stays readable and <see cref="Tooltip"/> is where the whole of it lives.</summary>
    public List<string> Cells { get; set; } = new();
    public List<PluginPanelAction> Actions { get; set; } = new();
    /// <summary>What a reader gets on hover: the cell that did not fit, and anything true about the
    /// row that is not worth a line of its own.</summary>
    public LocalizedText? Tooltip { get; set; }
}

/// <summary>A button: what it says, and the control request it sends when it is pressed.
///
/// The request travels back to the same instance the panel was rendered for, so a panel cannot
/// accidentally act on another mount. <see cref="Input"/> is how a panel asks for a path: the
/// window shows the picker, the reader chooses, and the choice becomes the request's
/// <c>Path</c> — the plugin never sees a file dialog and the window never learns why.
/// </summary>
public sealed class PluginPanelAction
{
    public string Id { get; set; } = "";
    public LocalizedText Label { get; set; } = new();
    public ControlRequest Request { get; set; } = new();

    public PluginPanelInput Input { get; set; }

    /// <summary>Asked before the request is sent, when the action cannot be undone by asking
    /// again. A panel that offers a delete says so here.</summary>
    public LocalizedText? Confirm { get; set; }

    /// <summary>A line-icon name; empty draws no icon.</summary>
    public string Icon { get; set; } = "";
}

/// <summary>What an action needs from the reader before it can be sent.</summary>
public enum PluginPanelInput
{
    None = 0,
    /// <summary>A folder: the action's request gets it in <c>Path</c>.</summary>
    Directory = 1,
    /// <summary>A file, likewise.</summary>
    File = 2,
}
