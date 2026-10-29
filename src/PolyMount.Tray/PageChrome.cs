using QtSharp.QtCore;
using QtSharp.QtWidgets;

namespace PolyMount.Tray;

/// <summary>
/// The frame every section of the workbench wears: the page background, the margins and spacing
/// between blocks, and the title with a muted line under it.
///
/// It exists because the sections did not look alike. Each one built its own copy of this — three
/// copies of the same four numbers plus a heading — and the one that was assembled from a dialog
/// instead (the accounts) ended up with a dialog's heading, a dialog's padding and a dialog's
/// fixed size. A page is a page whether it started life as a window or not, and the way to keep
/// that true is for the frame to exist once.
/// </summary>
internal static class PageChrome
{
    /// <summary>
    /// Applies the shell to <paramref name="page"/> and returns the layout that section fills.
    /// Its own actions go into <paramref name="header"/>, right-aligned beside the title — the
    /// place every section puts them.
    /// </summary>
    public static QVBoxLayout Apply(QWidget page, string titleKey, string subtitleKey, out QHBoxLayout header)
    {
        page.SetObjectName("dashboard");
        page.SetAttribute(Qt.WidgetAttribute.WA_StyledBackground);

        var root = new QVBoxLayout(page);
        root.SetContentsMargins(24, 20, 24, 14);
        root.SetSpacing(12);

        var titles = new QVBoxLayout();
        titles.SetSpacing(5);
        titles.AddWidget(Label(titleKey, "pageTitle"));
        if (subtitleKey.Length > 0) titles.AddWidget(Label(subtitleKey, "muted"));
        header = new QHBoxLayout();
        header.AddLayout(titles, 1);
        header.AddStretch(1);
        root.AddLayout(header);
        return root;
    }

    /// <summary>
    /// The same frame for a page whose title belongs to a plugin rather than to the catalog: the
    /// page names itself from what the plugin said, and the label comes back so it can be renamed
    /// when the reader changes language. It carries no subtitle — a plugin page puts its own
    /// sentence under the title, because that sentence is state.
    /// </summary>
    public static QVBoxLayout Apply(QWidget page, out QLabel title, out QHBoxLayout header)
    {
        page.SetObjectName("dashboard");
        page.SetAttribute(Qt.WidgetAttribute.WA_StyledBackground);

        var root = new QVBoxLayout(page);
        root.SetContentsMargins(24, 20, 24, 14);
        root.SetSpacing(12);

        title = new QLabel();
        title.SetObjectName("pageTitle");
        title.SetWordWrap(true);
        header = new QHBoxLayout();
        header.AddWidget(title, 1);
        root.AddLayout(header);
        return root;
    }

    private static QLabel Label(string key, string name)
    {
        var label = new QLabel();
        I18n.Bind(label.SetText, key);
        label.SetObjectName(name);
        label.SetWordWrap(true);
        return label;
    }
}
