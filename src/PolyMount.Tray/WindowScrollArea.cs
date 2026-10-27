using QtSharp.QtCore;
using QtSharp.QtGui;
using QtSharp.QtWidgets;

namespace PolyMount.Tray;

/// <summary>Keep complex workspaces usable on small screens without squeezing controls.</summary>
internal static class WindowScrollArea
{
    /// <summary>Wraps a window's page in the scroll area that gives it the room to be read on a
    /// screen too small for it, rather than squeezing it.
    ///
    /// Only the width is pinned. A pinned height would be a claim about how tall the page is, and
    /// the layout is the thing that knows: a page whose sections are opened needs more room than any
    /// number written here, and a minimum *below* what the layout needs is worse than none at all —
    /// the scroll area takes the minimum as the widget's size, the layout is then short of room, and
    /// it squeezes its children into one another instead of the page scrolling. This is what the
    /// performance page did: pinned at 900 pixels against a layout that wanted more, it drew chart
    /// cards 173 pixels tall inside a 210 pixel minimum, which pushed each card's axis labels into
    /// its own plot.
    ///
    /// The height is not left to the layout either, because a layout's idea of the height it wants
    /// is not what a window has to give: a word-wrapped label reports a height for a given width,
    /// the layout adds those up, and a scroll area sizes the page to that preference — so every
    /// window shorter than the page *would like* scrolled the whole page, blank space and all,
    /// even where everything the page needs fitted. The page asks for its minimum instead (see
    /// <see cref="FillsWindow"/>), takes the window's height when that is larger, and scrolls only
    /// for what it cannot do without. What is under it — a chart grid, a list, a layer stack —
    /// scrunches and scrolls within the room it is given.
    ///
    /// The width is a floor these layouts meet, which is what makes it safe to pin: it keeps the
    /// chart grid two columns wide on a narrow window and scrolls sideways instead of reflowing.
    ///
    /// The scroll area itself is handed back as well, for the pages that measure what they got: a
    /// page's own minimum is the layout's, but whether a window is tall enough to show it without
    /// scrolling is a fact about this scroll area, and only it knows.</summary>
    public static QWidget Create(QWidget window, int contentWidth, out QScrollArea scroll)
    {
        window.SetMinimumSize(480, 360);
        var layout = new QVBoxLayout(window);
        layout.SetContentsMargins(0, 0, 0, 0);
        scroll = new QScrollArea(window);
        scroll.SetWidgetResizable(true);
        scroll.SetFrameShape(QFrame.Shape.NoFrame);
        var content = new FillsWindow();
        content.SetObjectName("dashboard");
        content.SetAttribute(Qt.WidgetAttribute.WA_StyledBackground);
        content.SetMinimumWidth(contentWidth);
        scroll.SetWidget(content);
        layout.AddWidget(scroll);
        return content;
    }

    /// <summary>A page that asks for its minimum height rather than the height its text would
    /// like.
    ///
    /// A word-wrapped label reports a height for a given width, which makes the layout it is in ask
    /// for one too — and a scroll area takes that for the page's height. So the page was laid out
    /// as tall as its labels would like (taller than the window, with the line spacing to spare)
    /// and every window shorter than that scrolled the whole page: the workspace's instance list
    /// and detail panel were pushed out of sight by blank space of their own making. Here the
    /// answer is the minimum. The page fills the window, what is in it takes the height that is
    /// left, and the scrollbar appears only when even the minimum does not fit — which is what the
    /// lists, the tables and the pages' own sections are for.</summary>
    private sealed class FillsWindow : QWidget
    {
        public override int HeightForWidth(int width) => MinimumSizeHint.Height();
    }
}
