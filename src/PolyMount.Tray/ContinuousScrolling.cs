using QtSharp.QtCore;
using QtSharp.QtGui;
using QtSharp.QtWidgets;
using Qt = QtSharp.QtCore.Qt;

namespace PolyMount.Tray;

/// <summary>Consistent pixel scrolling for item views, including combo-box popups.</summary>
internal static class ContinuousScrolling
{
    public static QListWidget List() => Configure(new QListWidget());
    public static QTableWidget Table() => Configure(new QTableWidget());
    /// <summary>A picker the wheel does not move. Qt lets the wheel change a combo box's current
    /// item under the pointer, which is harmless until the picker sits inside a page that scrolls:
    /// the reader turns the wheel over it, the page stays where it is and a setting they were not
    /// touching changes. Every combo in the tray is made here, and the wheel is handed to the widget
    /// above instead, so the page scrolls over a picker the way it scrolls over any other pixel.
    /// </summary>
    public static QComboBox Combo()
    {
        var combo = new QComboBox();
        Configure(combo.View());
        combo.InstallEventFilter(new WheelGuard(combo));
        return combo;
    }

    /// <summary>Swallows the wheel for one widget and passes it upward. Ignoring it here would end
    /// the journey — Qt does not hand an event on by itself — so it is sent to the parent, where the
    /// scroll area takes it exactly as it would if the pointer had missed the picker.</summary>
    private sealed class WheelGuard(QWidget guarded) : QObject
    {
        public override bool EventFilter(QObject watched, QEvent e)
        {
            if (watched != guarded || e.type() != QEvent.Type.Wheel) return false;
            if (guarded.Parent() is QWidget above) QCoreApplication.SendEvent(above, e);
            return true;
        }
    }
    private static T Configure<T>(T view) where T : QAbstractItemView
    {
        view.SetVerticalScrollMode(QAbstractItemView.ScrollMode.ScrollPerPixel);
        view.SetHorizontalScrollMode(QAbstractItemView.ScrollMode.ScrollPerPixel);
        // Every list and table in the tray is built here. The native focus rectangle draws a
        // dotted frame around the current row on top of the selection background, which already
        // says which row is current; the frame is only noise, so these views never take focus.
        // Clicking still selects and activates rows.
        view.SetFocusPolicy(Qt.FocusPolicy.NoFocus);
        return view;
    }
}
