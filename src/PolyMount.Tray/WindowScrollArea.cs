using QtSharp.Api;

namespace PolyMount.Tray;

/// <summary>Keep complex workspaces usable on small screens without squeezing controls.</summary>
internal static class WindowScrollArea
{
    public static QWidget Create(QWidget window, int contentWidth, int contentHeight)
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
        content.SetMinimumSize(contentWidth, contentHeight);
        scroll.SetWidget(content);
        layout.AddWidget(scroll);
        return content;
    }
}
