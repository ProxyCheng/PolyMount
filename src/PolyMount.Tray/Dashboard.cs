using PolyMount.Core.Ipc;
using QtSharp.Api;

namespace PolyMount.Tray;

/// <summary>The main dashboard window: a table of mounts plus control buttons.</summary>
internal sealed class Dashboard
{
    private static readonly string[] Columns =
        { "Name", "Mounted", "Mount point", "Source", "Rev", "Providers", "Cache", "Error" };

    private readonly QWidget _window;
    private readonly QTableWidget _table;
    private readonly QLabel _status;
    private ConfigEditor? _editor;
    private StatsWindow? _stats;

    public Dashboard()
    {
        _window = new QWidget();
        _window.SetWindowTitle("PolyMount Dashboard");
        _window.Resize(1000, 540);

        var root = new QVBoxLayout(_window);
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
    }

    public void ShowWindow()
    {
        _window.Show();
        _window.Raise();
        _window.ActivateWindow();
        Refresh();
    }

    private QHBoxLayout BuildToolbar()
    {
        var bar = new QHBoxLayout();

        AddButton(bar, "Refresh", () => ControlClient.Status());
        AddButton(bar, "Mount", () => ControlClient.Mount(SelectedName()));
        AddButton(bar, "Unmount", () => ControlClient.Unmount(SelectedName()));
        AddButton(bar, "Refresh HEAD", () => ControlClient.RefreshHead(SelectedName()));
        AddButton(bar, "Clear cache", () => ControlClient.ClearCache(SelectedName()));

        bar.AddStretch(1);

        var edit = new QPushButton("Edit config...");
        edit.Clicked += _ => { _editor ??= new ConfigEditor(); _editor.ShowWindow(); };
        bar.AddWidget(edit);

        var stats = new QPushButton("Stats...");
        stats.Clicked += _ => { _stats ??= new StatsWindow(); _stats.ShowWindow(); };
        bar.AddWidget(stats);

        return bar;
    }

    private void AddButton(QHBoxLayout bar, string text, Func<ControlResponse> call)
    {
        var b = new QPushButton(text);
        b.Clicked += _ => Act(call);
        bar.AddWidget(b);
    }

    private string? SelectedName()
    {
        int row = _table.CurrentRow();
        if (row < 0) return null;
        return _table.Item(row, 0)?.Text();
    }

    private void Act(Func<ControlResponse> call)
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

    private void Refresh() => Act(ControlClient.Status);

    private void Fill(ControlResponse resp)
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

    private void Set(int row, int col, string text) =>
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
