using System.Diagnostics;
using System.IO;
using QtSharp.QtCore;
using QtSharp.QtGui;
using QtSharp.QtWidgets;

namespace PolyMount.Tray;

/// <summary>What this copy of PolyMount is: the build and the commit behind it, what it runs on,
/// where it and the service beside it sit, and what it is made of. The third destination in the
/// sidebar, beside the mounts and the plugins, and the page a reader is sent to when a report asks
/// "which version are you running" — the question the workbench has no room for.
///
/// The same facts are what the copy button puts on the clipboard, as plain "name: value" lines in
/// English: a report is read by somebody else, and the one thing worse than no version is one
/// pasted in a language the reader cannot match against a build.
///
/// A path too long for the window is cut in the middle — the ends are what a reader looks for —
/// and kept whole in the tooltip and in the copied text, so shortening never loses the answer.</summary>
internal sealed class AboutPage : QWidget
{
    private readonly QLabel _copyNote;
    private readonly QLabel _version, _revision, _channel;
    private readonly QLabel _dotnet, _qt, _winfsp, _system;
    private readonly QLabel _serviceState, _serviceVersion, _serviceProgram;
    private readonly QLabel _root, _config;

    private static string T(string key) => I18n.T(key);

    public AboutPage()
    {
        // A section of the workbench, not a page of its own: the sidebar stays where it is and
        // swaps the right-hand side. The outer page owns the scrolling, so this carries none.
        // The frame every section shares: background, margins, and the title with its muted line.
        var root = PageChrome.Apply(this, "abouts.title", "abouts.subtitle", out var header);
        _copyNote = PlainLabel("", "muted");
        header.AddWidget(_copyNote);
        header.AddWidget(IconButton("refresh", "abouts.refresh", Refresh));
        header.AddWidget(TextButton("abouts.copy", Copy, "save", primary: true));
        root.AddLayout(header);

        var build = Section(root, "abouts.build");
        _version = Row(build, "abouts.row.version");
        _revision = Row(build, "abouts.row.revision");
        _channel = Row(build, "abouts.row.channel");

        var runtime = Section(root, "abouts.runtime");
        _dotnet = Row(runtime, "abouts.row.dotnet");
        _qt = Row(runtime, "abouts.row.qt");
        _winfsp = Row(runtime, "abouts.row.winfsp");
        _system = Row(runtime, "abouts.row.system");

        var service = Section(root, "abouts.service");
        _serviceState = Row(service, "abouts.row.state");
        _serviceVersion = Row(service, "abouts.row.serviceVersion");
        _serviceProgram = Row(service, "abouts.row.serviceProgram");

        var locations = Section(root, "abouts.locations");
        _root = Row(locations, "abouts.row.root");
        _config = Row(locations, "abouts.row.config");

        var licence = Section(root, "abouts.license");
        var body = PlainLabel("", "muted");
        I18n.Bind(body.SetText, "abouts.license.body");
        licence.AddRow(body);
        var links = new QWidget();
        var row = new QHBoxLayout(links);
        row.SetContentsMargins(0, 0, 0, 0);
        row.SetSpacing(8);
        var notices = TextButton("abouts.license.notices", OpenNotices, "folder");
        notices.Enabled = ProductInfo.Notices.Length > 0;
        row.AddWidget(notices);
        row.AddWidget(TextButton("abouts.license.project", () => Open(ProjectPage), null));
        row.AddWidget(TextButton("abouts.license.issues", () => Open(IssuesPage), null));
        row.AddStretch(1);
        licence.AddRow(links);

        root.AddStretch(1);
        Refresh();
        I18n.OnChanged(Translate, this);
    }

    /// <summary>The version this page shows, so a check can ask whether it is the build's.</summary>
    internal string VersionText => _version.Text;

    /// <summary>Whether the program folder was found — the one fact here that cannot come back
    /// empty, so a page of dashes is not mistaken for a healthy one.</summary>
    internal bool ShowsRoot => _root.Text.Length > 0 && _root.Text != "—";

    /// <summary>Reads everything again. Cheap except for one pipe round trip to ask whether the
    /// service answers, which is the only fact here that changes while the window is open.</summary>
    internal void Refresh()
    {
        _version.SetText(ProductInfo.Version);
        _revision.SetText(ProductInfo.ShortRevision.Length > 0 ? ProductInfo.ShortRevision : "—");
        _revision.SetToolTip(ProductInfo.Full);
        _channel.SetText(T(ProductInfo.Installed ? "abouts.installed" : "abouts.development"));

        _dotnet.SetText(ProductInfo.Dotnet);
        _qt.SetText(ProductInfo.Qt);
        _winfsp.SetText(ProductInfo.WinFsp);
        _system.SetText(ProductInfo.System);

        _serviceState.SetText(T(ServiceControl.IsRunning() ? "abouts.connected" : "abouts.disconnected"));
        string version = ProductInfo.ServiceVersion;
        _serviceVersion.SetText(version.Length > 0 ? version : "—");
        Show(_serviceProgram, ProductInfo.ServiceExe);

        Show(_root, ProductInfo.Root);
        Show(_config, ProductInfo.ConfigPath);
    }

    /// <summary>A language change re-asks for the labels and the two words that are not a value;
    /// everything else on the page is a fact and does not move.</summary>
    private void Translate()
    {
        _copyNote.SetText("");
        _channel.SetText(T(ProductInfo.Installed ? "abouts.installed" : "abouts.development"));
        _serviceState.SetText(T(ServiceControl.IsRunning() ? "abouts.connected" : "abouts.disconnected"));
    }

    private void Copy()
    {
        var lines = new List<string>
        {
            "PolyMount " + ProductInfo.Full,
            "installed: " + (ProductInfo.Installed ? "setup at " + ProductInfo.Root : "development tree at " + ProductInfo.Root),
            ".NET: " + ProductInfo.Dotnet,
            "Qt: " + ProductInfo.Qt,
            "WinFsp: " + ProductInfo.WinFsp,
            "system: " + ProductInfo.System
        };
        if (ProductInfo.ServiceVersion is { Length: > 0 } service)
            lines.Add("service: " + service + " at " + ProductInfo.ServiceExe);
        if (ProductInfo.ConfigPath is { Length: > 0 } config)
            lines.Add("configuration: " + config);
        try { QGuiApplication.Clipboard().SetText(string.Join(Environment.NewLine, lines)); }
        catch (Exception) { return; }

        _copyNote.SetText(T("abouts.copied"));
        var timer = new QTimer(this);
        timer.SetSingleShot(true);
        timer.Timeout += () =>
        {
            _copyNote.SetText("");
            timer.DeleteLater();
        };
        timer.Start(2500);
    }

    private void OpenNotices()
    {
        if (ProductInfo.Notices is { Length: > 0 } path) Reveal(path);
    }

    /// <summary>The project's own pages. Only https on the project's host is opened: the addresses
    /// are constants here, but the rule is the same one the update check follows, so a future
    /// change cannot quietly hand the shell something else.</summary>
    private const string ProjectPage = "https://github.com/ProxyCheng/PolyMount";
    private const string IssuesPage = "https://github.com/ProxyCheng/PolyMount/issues";

    private static void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)) return;
        if (target.Scheme != Uri.UriSchemeHttps) return;
        if (!string.Equals(target.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { }
    }

    private static void Reveal(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception) { }
    }

    /// <summary>A value that is a path: shortened on screen, whole in the tooltip.</summary>
    private static void Show(QLabel label, string value)
    {
        label.SetText(value.Length > 0 ? Short(value) : "—");
        label.SetToolTip(value);
    }

    internal static string Short(string text, int limit = 64)
    {
        if (text.Length <= limit) return text;
        int head = (limit - 1) / 2, tail = limit - 1 - head;
        return text[..head] + "…" + text[^tail..];
    }

    // ---- small builders -----------------------------------------------------------------

    private static QFormLayout Section(QVBoxLayout root, string headingKey)
    {
        root.AddWidget(Label(headingKey, "eyebrow"));
        var panel = Panel("surface");
        var layout = new QVBoxLayout(panel);
        layout.SetContentsMargins(16, 12, 16, 12);
        var form = new QFormLayout();
        form.SetRowWrapPolicy(QFormLayout.RowWrapPolicy.DontWrapRows);
        form.SetFieldGrowthPolicy(QFormLayout.FieldGrowthPolicy.AllNonFixedFieldsGrow);
        form.SetHorizontalSpacing(18);
        form.SetVerticalSpacing(7);
        layout.AddLayout(form);
        root.AddWidget(panel);
        return form;
    }

    private static QLabel Row(QFormLayout form, string key)
    {
        var value = PlainLabel("", "detailValue");
        value.SetTextInteractionFlags(Qt.TextInteractionFlags.TextSelectableByMouse);
        form.AddRow(Label(key, "muted"), value);
        return value;
    }

    private static QWidget Panel(string name)
    {
        var widget = new QWidget();
        widget.SetObjectName(name);
        widget.SetAttribute(Qt.WidgetAttribute.WA_StyledBackground);
        return widget;
    }

    private static QLabel Label(string key, string name)
    {
        var label = PlainLabel("", name);
        I18n.Bind(label.SetText, key);
        return label;
    }

    private static QLabel PlainLabel(string text, string name)
    {
        var label = new QLabel(text);
        label.SetObjectName(name);
        label.SetWordWrap(true);
        label.SetTextFormat(Qt.TextFormat.PlainText);
        return label;
    }

    private static QPushButton IconButton(string icon, string tooltipKey, Action action)
    {
        var button = new QPushButton();
        button.SetObjectName("iconButton");
        button.SetIcon(TrayIcons.Get(icon, TrayTheme.Tokens.Icon));
        I18n.Bind(button.SetToolTip, tooltipKey);
        I18n.Bind(button.SetAccessibleName, tooltipKey);
        button.SetCursor(new QCursor(Qt.CursorShape.PointingHandCursor));
        button.Clicked += _ => action();
        return button;
    }

    private static QPushButton TextButton(string key, Action action, string? icon, bool primary = false)
    {
        var button = new QPushButton();
        I18n.Bind(button.SetText, key);
        button.SetObjectName(primary ? "primary" : "");
        button.SetCursor(new QCursor(Qt.CursorShape.PointingHandCursor));
        if (icon != null) button.SetIcon(TrayIcons.Get(icon, primary ? TrayTheme.Tokens.OnPrimary : TrayTheme.Tokens.Icon));
        button.Clicked += _ => action();
        return button;
    }
}
