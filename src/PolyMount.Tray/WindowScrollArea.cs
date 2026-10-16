using QtSharp.Api;

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
    /// its own plot. Left to itself the height comes from the layout, so the page scrolls.
    ///
    /// The width is a floor these layouts meet, which is what makes it safe to pin: it keeps the
    /// chart grid two columns wide on a narrow window and scrolls sideways instead of reflowing.</summary>
    public static QWidget Create(QWidget window, int contentWidth)
    {
        window.SetMinimumSize(480, 360);
        var layout = new QVBoxLayout(window);
        layout.SetContentsMargins(0, 0, 0, 0);
        var scroll = new QScrollArea(window);
        scroll.SetWidgetResizable(true);
        scroll.SetFrameShape(QFrame.Shape.NoFrame);
        var content = new QWidget();
        content.SetObjectName("dashboard");
        content.SetAttribute(Qt.WidgetAttribute.WA_StyledBackground);
        content.SetMinimumWidth(contentWidth);
        scroll.SetWidget(content);
        layout.AddWidget(scroll);
        return content;
    }
}
