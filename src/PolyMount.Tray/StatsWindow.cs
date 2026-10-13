using PolyMount.Core.Ipc;
using QtSharp.Api;

namespace PolyMount.Tray;

/// <summary>Per-layer read analytics (hit rate / access cost) in a table.</summary>
internal sealed class StatsWindow
{
    private static readonly string[] Columns =
        { "Mount", "Layer", "Hits", "Share %", "Avg ms", "Max ms", "Bytes" };
    private static readonly int[] Windows = { 60, 300, 900, 3600 };

    private readonly QWidget _window;
    private readonly QComboBox _windowBox;
    private readonly QTableWidget _table;
    private readonly QLabel _status;

    public StatsWindow()
    {
        _window = new QWidget();
        _window.SetWindowTitle("PolyMount Stats (layer hit rate / access cost)");
        _window.Resize(780, 560);

        var root = new QVBoxLayout(_window);

        var bar = new QHBoxLayout();
        bar.AddWidget(new QLabel("Window:"));
        _windowBox = new QComboBox();
        _windowBox.AddItem("Last 1 minute");
        _windowBox.AddItem("Last 5 minutes");
        _windowBox.AddItem("Last 15 minutes");
        _windowBox.AddItem("Last 1 hour");
        _windowBox.SetCurrentIndex(1);
        _windowBox.CurrentIndexChanged += _ => Refresh();
        bar.AddWidget(_windowBox);
        var refresh = new QPushButton("Refresh");
        refresh.Clicked += _ => Refresh();
        bar.AddWidget(refresh);
        bar.AddStretch(1);
        root.AddLayout(bar);

        _table = new QTableWidget();
        _table.SetColumnCount(Columns.Length);
        _table.SetHorizontalHeaderLabels(Columns);
        _table.SetEditTriggers(QAbstractItemView.EditTriggers.NoEditTriggers);
        root.AddWidget(_table);

        _status = new QLabel();
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

    private int WindowSeconds()
    {
        int i = _windowBox.CurrentIndex;
        return i >= 0 && i < Windows.Length ? Windows[i] : 300;
    }

    private void Refresh()
    {
        try
        {
            var resp = ControlClient.Metrics(WindowSeconds());
            if (!resp.Ok) { _status.SetText("error: " + resp.Error); return; }

            var rows = new List<(MountMetrics m, LayerMetric? l)>();
            foreach (var m in resp.Metrics)
            {
                if (m.Layers.Count == 0) rows.Add((m, null));
                else foreach (var l in m.Layers) rows.Add((m, l));
            }

            _table.SetRowCount(rows.Count);
            for (int r = 0; r < rows.Count; r++)
            {
                var (m, l) = rows[r];
                Set(r, 0, m.Mount);
                if (l == null) { Set(r, 1, "(no reads)"); for (int c = 2; c < Columns.Length; c++) Set(r, c, ""); continue; }
                Set(r, 1, l.Name);
                Set(r, 2, l.Hits.ToString());
                Set(r, 3, l.SharePercent.ToString("0.#"));
                Set(r, 4, l.AvgCostMs.ToString("0.##"));
                Set(r, 5, l.MaxCostMs.ToString("0.##"));
                Set(r, 6, l.Bytes.ToString());
            }
            _table.ResizeColumnsToContents();

            _status.SetText(string.Join("   ", resp.Metrics.Select(m =>
                $"{m.Mount}: {m.TotalReads} reads, cache {m.CacheHitPercent:0.#}%, avg {m.AvgCostMs:0.##} ms")));
        }
        catch (Exception ex)
        {
            _status.SetText("service unreachable: " + ex.Message);
        }
    }

    private void Set(int row, int col, string text) =>
        _table.SetItem(row, col, new QTableWidgetItem(text ?? ""));
}
