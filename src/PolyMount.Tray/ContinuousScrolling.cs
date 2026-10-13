using QtSharp.Api;

namespace PolyMount.Tray;

/// <summary>Consistent pixel scrolling for item views, including combo-box popups.</summary>
internal static class ContinuousScrolling
{
    public static QListWidget List() => Configure(new QListWidget());
    public static QTableWidget Table() => Configure(new QTableWidget());
    public static QComboBox Combo()
    {
        var combo = new QComboBox();
        Configure(combo.View());
        return combo;
    }
    private static T Configure<T>(T view) where T : QAbstractItemView
    {
        view.SetVerticalScrollMode(QAbstractItemView.ScrollMode.ScrollPerPixel);
        view.SetHorizontalScrollMode(QAbstractItemView.ScrollMode.ScrollPerPixel);
        return view;
    }
}
