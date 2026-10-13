using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PolyMount;
using PolyMount.Core;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace PolyMount.Tray;

public partial class MountEditorWindow : Window
{
    private AppConfig _config = new();
    private int _current = -1;
    private bool _loading;

    public MountEditorWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadFromService();
    }

    private void LoadFromService()
    {
        try
        {
            var resp = ControlClient.GetConfig();
            if (!resp.Ok || string.IsNullOrWhiteSpace(resp.Config))
            {
                StatusText.Text = "无法读取配置: " + (resp.Error ?? "空响应");
                return;
            }

            _config = AppConfig.FromJson(resp.Config);

            SourceTypeBox.ItemsSource = resp.SourceTypes.Count > 0
                ? resp.SourceTypes
                : new List<string> { "svn", "dir" };

            OverlayColorBox.Text = ColorOf("overlay");
            CacheColorBox.Text = ColorOf("cache");
            SvnColorBox.Text = ColorOf("svn");
            DirColorBox.Text = ColorOf("dir");
            RefreshSwatches();

            RebuildList();
            StatusText.Text = $"已加载 {_config.Mounts.Count} 个挂载实例";
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取配置失败: " + ex.Message;
        }
    }

    private string ColorOf(string key) => _config.LayerColors.TryGetValue(key, out var v) ? v : "";

    private void RebuildList()
    {
        MountListBox.Items.Clear();
        foreach (var m in _config.Mounts)
            MountListBox.Items.Add(m.Name + (m.Enabled ? "" : " (停用)"));
        if (_config.Mounts.Count > 0)
            MountListBox.SelectedIndex = Math.Min(Math.Max(_current, 0), _config.Mounts.Count - 1);
        else
        {
            _current = -1;
            EditorPanel.IsEnabled = false;
        }
    }

    private void OnMountSelected(object sender, SelectionChangedEventArgs e)
    {
        CommitCurrent();
        _current = MountListBox.SelectedIndex;
        if (_current < 0 || _current >= _config.Mounts.Count) { EditorPanel.IsEnabled = false; return; }
        LoadFields(_config.Mounts[_current]);
    }

    private void LoadFields(MountProfile m)
    {
        _loading = true;
        EditorPanel.IsEnabled = true;
        NameBox.Text = m.Name;
        MountPointBox.Text = m.MountPoint;
        StoreRootBox.Text = m.StoreRoot;
        CacheMbBox.Text = (m.CacheCapacityBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
        EnabledBox.IsChecked = m.Enabled;

        SourceTypeBox.SelectedItem = m.Source.Type;
        UrlBox.Text = m.Source.Url;
        PinnedBox.Text = m.Source.PinnedRevision.ToString(CultureInfo.InvariantCulture);
        TrustBox.Text = m.Source.TrustServerCertFailures;
        UserBox.Text = m.Source.Username ?? "";
        PathBox.Text = m.Source.Path;
        WritableBox.IsChecked = m.Source.Writable;
        _loading = false;
    }

    // Write the editor fields back into the currently selected profile.
    private void CommitCurrent()
    {
        if (_current < 0 || _current >= _config.Mounts.Count) return;
        var m = _config.Mounts[_current];
        m.Name = NameBox.Text.Trim();
        m.MountPoint = MountPointBox.Text.Trim();
        m.StoreRoot = StoreRootBox.Text.Trim();
        if (long.TryParse(CacheMbBox.Text.Trim(), out var mb) && mb >= 0)
            m.CacheCapacityBytes = mb * 1024 * 1024;
        m.Enabled = EnabledBox.IsChecked == true;

        m.Source.Type = SourceTypeBox.SelectedItem as string ?? m.Source.Type;
        m.Source.Url = UrlBox.Text.Trim();
        if (long.TryParse(PinnedBox.Text.Trim(), out var pin)) m.Source.PinnedRevision = pin;
        m.Source.TrustServerCertFailures = TrustBox.Text.Trim();
        m.Source.Username = string.IsNullOrWhiteSpace(UserBox.Text) ? null : UserBox.Text.Trim();
        m.Source.Path = PathBox.Text.Trim();
        m.Source.Writable = WritableBox.IsChecked == true;
    }

    private void OnSourceTypeChanged(object sender, SelectionChangedEventArgs e) { /* fields stay visible */ }

    private void OnAddMount(object sender, RoutedEventArgs e)
    {
        CommitCurrent();
        var name = "Mount" + (_config.Mounts.Count + 1);
        _config.Mounts.Add(new MountProfile
        {
            Name = name,
            Enabled = true,
            MountPoint = "",
            StoreRoot = "",
            Source = new SourceConfig { Type = (SourceTypeBox.Items.Count > 0 ? SourceTypeBox.Items[0] as string : "svn") ?? "svn" }
        });
        _current = _config.Mounts.Count - 1;
        RebuildList();
        MountListBox.SelectedIndex = _current;
    }

    private void OnRemoveMount(object sender, RoutedEventArgs e)
    {
        if (_current < 0 || _current >= _config.Mounts.Count) return;
        _config.Mounts.RemoveAt(_current);
        _current = -1;
        RebuildList();
    }

    private void OnColorChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        RefreshSwatches();
    }

    private void RefreshSwatches()
    {
        SetSwatch(OverlaySwatch, OverlayColorBox.Text, "overlay");
        SetSwatch(CacheSwatch, CacheColorBox.Text, "cache");
        SetSwatch(SvnSwatch, SvnColorBox.Text, "svn");
        SetSwatch(DirSwatch, DirColorBox.Text, "dir");
    }

    private static void SetSwatch(Border b, string text, string key)
    {
        var hex = LayerColors.TryNormalizeHex(text, out var norm) ? norm : LayerColors.Auto(key);
        try { b.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { b.Background = Brushes.Transparent; }
    }

    private void CollectColors()
    {
        _config.LayerColors.Clear();
        AddColor("overlay", OverlayColorBox.Text);
        AddColor("cache", CacheColorBox.Text);
        AddColor("svn", SvnColorBox.Text);
        AddColor("dir", DirColorBox.Text);
    }

    private void AddColor(string key, string text)
    {
        if (LayerColors.TryNormalizeHex(text, out var norm)) _config.LayerColors[key] = norm;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        CommitCurrent();
        CollectColors();
        try
        {
            _config.Validate();
        }
        catch (Exception ex)
        {
            StatusText.Text = "校验失败: " + ex.Message;
            return;
        }

        try
        {
            var resp = ControlClient.SaveConfig(_config.ToJson());
            if (!resp.Ok) { StatusText.Text = "保存失败: " + resp.Error; return; }
            StatusText.Text = "已保存并重载。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "服务不可达: " + ex.Message;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
