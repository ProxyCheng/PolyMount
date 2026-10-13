using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PolyMount.Core.Ipc;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using ColorConverter = System.Windows.Media.ColorConverter;
using Orientation = System.Windows.Controls.Orientation;

namespace PolyMount.Tray;

public partial class StatsWindow : Window
{
    private const double BarMaxWidth = 260;

    public StatsWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private int WindowSeconds()
    {
        if (WindowBox.SelectedItem is ComboBoxItem it && it.Tag is string s && int.TryParse(s, out var v)) return v;
        return 300;
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();
    private void OnWindowChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) Refresh(); }

    private void Refresh()
    {
        Board.Children.Clear();
        try
        {
            var resp = ControlClient.Metrics(WindowSeconds());
            if (!resp.Ok) { StatusText.Text = "error: " + resp.Error; return; }
            if (resp.Metrics.Count == 0) { StatusText.Text = "no mount instances"; return; }

            foreach (var m in resp.Metrics)
                Board.Children.Add(BuildMountCard(m));

            StatusText.Text = $"refreshed · window {WindowSeconds()}s";
        }
        catch (Exception ex)
        {
            StatusText.Text = "service unreachable: " + ex.Message;
        }
    }

    private UIElement BuildMountCard(MountMetrics m)
    {
        var card = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(10)
        };
        var panel = new StackPanel();

        var header = new TextBlock { FontWeight = FontWeights.Bold, FontSize = 14 };
        header.Text = m.TotalReads == 0
            ? $"{m.Mount} — no reads in window"
            : $"{m.Mount} — {m.TotalReads} reads · cache hit {m.CacheHitPercent:0.#}% · avg cost {m.AvgCostMs:0.##} ms";
        panel.Children.Add(header);

        if (m.TotalReads > 0)
        {
            double maxShare = m.Layers.Count > 0 ? m.Layers.Max(l => l.SharePercent) : 100;
            foreach (var l in m.Layers)
                panel.Children.Add(BuildLayerRow(l, maxShare));
        }

        card.Child = panel;
        return card;
    }

    private UIElement BuildLayerRow(LayerMetric l, double maxShare)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });   // name
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BarMaxWidth + 10) }); // bar
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });      // stats

        var swName = new StackPanel { Orientation = Orientation.Horizontal };
        swName.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Width = 12, Height = 12, RadiusX = 3, RadiusY = 3,
            Fill = ParseBrush(l.Color), Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        swName.Children.Add(new TextBlock { Text = l.Name, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(swName, 0);
        grid.Children.Add(swName);

        // Proportional bar (width scaled to the biggest share so the chart fills nicely).
        double w = maxShare <= 0 ? 0 : BarMaxWidth * (l.SharePercent / maxShare);
        var barHost = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        barHost.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(2, w), Height = 16, RadiusX = 3, RadiusY = 3, Fill = ParseBrush(l.Color)
        });
        barHost.Children.Add(new TextBlock
        {
            Text = $"  {l.SharePercent:0.#}%", VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray
        });
        Grid.SetColumn(barHost, 1);
        grid.Children.Add(barHost);

        var stats = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            Text = $"{l.Hits} hits · avg {l.AvgCostMs:0.##} ms · max {l.MaxCostMs:0.##} ms · {FormatBytes(l.Bytes)}"
        };
        Grid.SetColumn(stats, 2);
        grid.Children.Add(stats);

        return grid;
    }

    private static Brush ParseBrush(string hex)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch { }
        return Brushes.Gray;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.##} {units[i]}";
    }
}
