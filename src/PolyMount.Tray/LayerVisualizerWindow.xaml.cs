using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PolyMount.Core.Ipc;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using MessageBox = System.Windows.MessageBox;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace PolyMount.Tray;

public partial class LayerVisualizerWindow : Window
{
    // Visual constants for the stacked-cylinder diagram.
    private const double CylWidth = 200;
    private const double SegHeight = 70;
    private const double CapRy = 18;
    private const double LeftPad = 40;
    private const double TopPad = 24;
    private const double LabelGap = 24;

    private static readonly Brush EffectiveBody = Freeze(Color.FromRgb(0x3C, 0xA8, 0x4B));
    private static readonly Brush EffectiveCap = Freeze(Color.FromRgb(0x5C, 0xC8, 0x6B));
    private static readonly Brush ShadowedBody = Freeze(Color.FromRgb(0xC9, 0x9A, 0x2E));
    private static readonly Brush ShadowedCap = Freeze(Color.FromRgb(0xE0, 0xB4, 0x4A));
    private static readonly Brush TombBody = Freeze(Color.FromRgb(0xC6, 0x3A, 0x3A));
    private static readonly Brush TombCap = Freeze(Color.FromRgb(0xDC, 0x5A, 0x5A));
    private static readonly Brush EmptyBody = Freeze(Color.FromArgb(0x30, 0x90, 0x90, 0x90));
    private static readonly Brush EmptyCap = Freeze(Color.FromArgb(0x50, 0xB0, 0xB0, 0xB0));
    private static readonly Brush Outline = Freeze(Color.FromRgb(0x55, 0x55, 0x55));

    private static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private enum State { Unknown, Effective, Shadowed, Tombstone }

    private readonly string _fullPath;
    private ProbeResult _result;

    public LayerVisualizerWindow(string fullPath, ProbeResult result)
    {
        InitializeComponent();
        _fullPath = fullPath;
        _result = result;
        Render();
    }

    private static State Classify(LayerProbe p)
    {
        if (p.Tombstoned) return State.Tombstone;
        if (p.Effective) return State.Effective;
        if (p.Exists) return State.Shadowed;
        return State.Unknown;
    }

    private static (Brush body, Brush cap) Palette(State s) => s switch
    {
        State.Effective => (EffectiveBody, EffectiveCap),
        State.Shadowed => (ShadowedBody, ShadowedCap),
        State.Tombstone => (TombBody, TombCap),
        _ => (EmptyBody, EmptyCap)
    };

    private static string StateLabel(State s) => s switch
    {
        State.Effective => "生效（存在）",
        State.Shadowed => "被覆盖（存在）",
        State.Tombstone => "不存在（墓碑）",
        _ => "不知道（向下层）"
    };

    private void Render()
    {
        var result = _result;
        Board.Children.Clear();

        PathText.Text = $"{result.MountPoint}\\{result.Rel.Replace('/', '\\')}".TrimEnd('\\');
        SummaryText.Text = result.Found
            ? $"挂载 “{result.Mount}” · 相对 “{(result.Rel.Length == 0 ? "<根>" : result.Rel)}” · 生效类型：{(result.IsDir ? "目录" : "文件")}"
            : $"挂载 “{result.Mount}” · 相对 “{(result.Rel.Length == 0 ? "<根>" : result.Rel)}” · 当前不可见（无生效层或被墓碑遮蔽）";

        BuildLegend();

        // Layers come top-down in resolution order (index 0 = highest priority).
        var layers = result.Layers;
        double x = LeftPad;
        double y = TopPad;

        for (int i = 0; i < layers.Count; i++)
        {
            var p = layers[i];
            var state = Classify(p);
            DrawSegment(x, y, state);
            DrawLabel(x + CylWidth + LabelGap, y, p, state);
            y += SegHeight;
        }

        DrawCap(x, y, EmptyCap, dashed: true); // close the bottom

        Board.Width = x + CylWidth + LabelGap + 300;
        Board.Height = y + CapRy + TopPad;
    }

    private void DrawSegment(double x, double y, State state)
    {
        var (body, cap) = Palette(state);
        bool empty = state == State.Unknown;

        var rect = new Rectangle { Width = CylWidth, Height = SegHeight, Fill = body };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        Board.Children.Add(rect);

        Board.Children.Add(VLine(x, y, y + SegHeight, empty));
        Board.Children.Add(VLine(x + CylWidth, y, y + SegHeight, empty));

        DrawCap(x, y, cap, dashed: empty);

        if (state == State.Tombstone)
            AddCenteredText(x, y, "⊘", 28, Brushes.White);
    }

    private void DrawCap(double x, double y, Brush fill, bool dashed)
    {
        var ellipse = new Ellipse
        {
            Width = CylWidth, Height = CapRy * 2, Fill = fill, Stroke = Outline, StrokeThickness = 1
        };
        if (dashed) ellipse.StrokeDashArray = new DoubleCollection { 3, 3 };
        Canvas.SetLeft(ellipse, x);
        Canvas.SetTop(ellipse, y - CapRy);
        Board.Children.Add(ellipse);
    }

    private Line VLine(double x, double y1, double y2, bool dashed)
    {
        var line = new Line { X1 = x, X2 = x, Y1 = y1, Y2 = y2, Stroke = Outline, StrokeThickness = 1 };
        if (dashed) line.StrokeDashArray = new DoubleCollection { 3, 3 };
        return line;
    }

    private void AddCenteredText(double x, double y, string text, double size, Brush brush)
    {
        var tb = new TextBlock { Text = text, FontSize = size, Foreground = brush, FontWeight = FontWeights.Bold };
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(tb, x + (CylWidth - tb.DesiredSize.Width) / 2);
        Canvas.SetTop(tb, y + (SegHeight - tb.DesiredSize.Height) / 2);
        Board.Children.Add(tb);
    }

    private void DrawLabel(double x, double y, LayerProbe p, State state)
    {
        var sb = new StackPanel();

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new Rectangle
        {
            Width = 13, Height = 13, RadiusX = 3, RadiusY = 3,
            Fill = ParseBrush(p.Color), Stroke = Outline, StrokeThickness = 1,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            ToolTip = $"来源层颜色 {p.Color}"
        });
        var title = new TextBlock { FontWeight = FontWeights.Bold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        title.Inlines.Add($"{p.Name}{(p.Writable ? " (可写)" : "")}  ");
        title.Inlines.Add(new System.Windows.Documents.Run("[" + StateLabel(state) + "]") { Foreground = Palette(state).body });
        header.Children.Add(title);
        sb.Children.Add(header);

        string detail = state switch
        {
            State.Unknown => "该层无此路径，解析向下层传递",
            State.Tombstone => "墓碑：遮蔽下层同名项",
            _ => p.IsDir ? "目录" : $"文件 · {FormatBytes(p.Size)}"
        };
        sb.Children.Add(new TextBlock { Text = detail, Foreground = Freeze(Color.FromRgb(0x55, 0x55, 0x55)) });
        sb.Children.Add(new TextBlock { Text = "gen: " + p.Generation, FontSize = 11, Foreground = Freeze(Color.FromRgb(0x99, 0x99, 0x99)) });

        var actions = BuildActions(p, state);
        if (actions != null) sb.Children.Add(actions);

        Canvas.SetLeft(sb, x);
        Canvas.SetTop(sb, y + 4);
        Board.Children.Add(sb);
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

    // Management actions appropriate to the layer's current state.
    private WrapPanel? BuildActions(LayerProbe p, State state)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };

        if (string.Equals(p.Name, "overlay", StringComparison.OrdinalIgnoreCase))
        {
            if (p.DirectTombstone)
                panel.Children.Add(Btn("恢复（删除墓碑）", p.Name, LayerActions.Restore));
            else if (p.Exists)
            {
                panel.Children.Add(Btn("删除（墓碑遮蔽）", p.Name, LayerActions.Hide));
                panel.Children.Add(Btn("丢弃本层改动", p.Name, LayerActions.DropLocal));
            }
            else
                panel.Children.Add(Btn("删除（墓碑遮蔽）", p.Name, LayerActions.Hide));
        }
        else if (string.Equals(p.Name, "cache", StringComparison.OrdinalIgnoreCase) && p.Exists)
        {
            panel.Children.Add(Btn("清除此文件缓存", p.Name, LayerActions.EvictCache));
        }

        return panel.Children.Count == 0 ? null : panel;
    }

    private Button Btn(string text, string layer, string action)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(6, 2, 6, 2) };
        b.Click += (_, _) => Act(layer, action);
        return b;
    }

    private void Act(string layer, string action)
    {
        try
        {
            var resp = ControlClient.LayerAction(_fullPath, layer, action);
            if (!resp.Ok)
            {
                MessageBox.Show(this, resp.Error ?? "操作失败", "PolyMount", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (resp.Probe != null) { _result = resp.Probe; Render(); }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "PolyMount", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BuildLegend()
    {
        Legend.Children.Clear();
        AddLegendItem(EffectiveBody, "生效（存在）");
        AddLegendItem(ShadowedBody, "被覆盖（存在）");
        AddLegendItem(TombBody, "不存在（墓碑）");
        AddLegendItem(EmptyBody, "不知道");
    }

    private void AddLegendItem(Brush fill, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
        panel.Children.Add(new Rectangle
        {
            Width = 16, Height = 16, Fill = fill, Stroke = Outline, StrokeThickness = 1, Margin = new Thickness(0, 0, 6, 0)
        });
        panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        Legend.Children.Add(panel);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.##} {units[i]}";
    }
}
