using PolyMount.Core.Ipc;
using QtSharp.Api;

namespace PolyMount.Tray;

/// <summary>
/// Qt-based PolyMount dashboard. Talks to the service over the shared named-pipe control client.
/// This is the first screen of the QtSharp UI migration; more windows (layer visualizer, config
/// editor, stats) follow.
/// </summary>
internal static class Program
{
    private static readonly string[] Columns =
        { "Name", "Mounted", "Mount point", "Source", "Rev", "Providers", "Cache", "Error" };

    private static QTableWidget _table = null!;
    private static QLabel _status = null!;

    [STAThread]
    private static int Main(string[] args)
    {
        var app = new QApplication(args);

        var window = new QWidget();
        window.SetWindowTitle("PolyMount Dashboard");
        window.Resize(1000, 540);

        var root = new QVBoxLayout(window);

        root.AddLayout(BuildToolbar());

        _table = new QTableWidget();
        _table.SetColumnCount(Columns.Length);
        _table.SetHorizontalHeaderLabels(Columns);
        _table.SetEditTriggers(QAbstractItemView.EditTriggers.NoEditTriggers);
        _table.SetSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows);
        root.AddWidget(_table);

        _status = new QLabel("starting...");
        root.AddWidget(_status);

        Refresh();

        window.Show();
        return app.Exec();
    }

    private static QHBoxLayout BuildToolbar()
    {
        var bar = new QHBoxLayout();

        var refresh = new QPushButton("Refresh");
        refresh.Clicked += _ => Refresh();
        bar.AddWidget(refresh);

        var mount = new QPushButton("Mount");
        mount.Clicked += _ => Act(() => ControlClient.Mount(SelectedName()));
        bar.AddWidget(mount);

        var unmount = new QPushButton("Unmount");
        unmount.Clicked += _ => Act(() => ControlClient.Unmount(SelectedName()));
        bar.AddWidget(unmount);

        var refreshHead = new QPushButton("Refresh HEAD");
        refreshHead.Clicked += _ => Act(() => ControlClient.RefreshHead(SelectedName()));
        bar.AddWidget(refreshHead);

        var clearCache = new QPushButton("Clear cache");
        clearCache.Clicked += _ => Act(() => ControlClient.ClearCache(SelectedName()));
        bar.AddWidget(clearCache);

        bar.AddStretch(1);
        return bar;
    }

    private static string? SelectedName()
    {
        int row = _table.CurrentRow();
        if (row < 0) return null;
        var item = _table.Item(row, 0);
        return item?.Text();
    }

    private static void Act(Func<ControlResponse> call)
    {
        try
        {
            var resp = call();
            _status.SetText(resp.Ok ? "ok" : "error: " + resp.Error);
            Fill(resp);
        }
        catch (Exception ex)
        {
            _status.SetText("service unreachable: " + ex.Message);
        }
    }

    private static void Refresh() => Act(ControlClient.Status);

    private static void Fill(ControlResponse resp)
    {
        var mounts = resp.Mounts;
        _table.SetRowCount(mounts.Count);
        for (int r = 0; r < mounts.Count; r++)
        {
            var m = mounts[r];
            Set(r, 0, m.Name);
            Set(r, 1, m.Mounted ? "yes" : "no");
            Set(r, 2, m.MountPoint);
            Set(r, 3, m.SourceType);
            Set(r, 4, m.Revision == 0 ? "" : m.Revision.ToString());
            Set(r, 5, string.Join(" -> ", m.Providers.Select(p => p.Name + (p.Writable ? "(w)" : ""))));
            Set(r, 6, FormatBytes(m.CacheBytes) + " / " + FormatBytes(m.CacheCapacity));
            Set(r, 7, m.Error ?? "");
        }
        _table.ResizeColumnsToContents();
    }

    private static void Set(int row, int col, string text) =>
        _table.SetItem(row, col, new QTableWidgetItem(text ?? ""));

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.##} {units[i]}";
    }
}
