using System.Windows;
using PolyMount.Core.Ipc;

namespace PolyMount.Tray;

public sealed class MountRow
{
    public string Name { get; init; } = "";
    public bool Mounted { get; init; }
    public string MountPoint { get; init; } = "";
    public string SourceType { get; init; } = "";
    public long Revision { get; init; }
    public string ProvidersText { get; init; } = "";
    public string CacheText { get; init; } = "";
    public string Error { get; init; } = "";
}

public partial class DashboardWindow : Window
{
    public DashboardWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshStatus();
    }

    public void RefreshStatus()
    {
        try
        {
            var response = ControlClient.Status();
            StatusText.Text = "connected";
            Apply(response);
        }
        catch (Exception ex)
        {
            StatusText.Text = "service unreachable: " + ex.Message;
            MountList.ItemsSource = Array.Empty<MountRow>();
        }
    }

    private void Call(ControlRequest request)
    {
        try
        {
            var response = ControlClient.Send(request);
            StatusText.Text = response.Ok ? "ok" : "error: " + response.Error;
            Apply(response);
        }
        catch (Exception ex)
        {
            StatusText.Text = "service unreachable: " + ex.Message;
        }
    }

    private void Apply(ControlResponse response)
    {
        MountList.ItemsSource = response.Mounts.Select(m => new MountRow
        {
            Name = m.Name,
            Mounted = m.Mounted,
            MountPoint = m.MountPoint,
            SourceType = m.SourceType,
            Revision = m.Revision,
            ProvidersText = string.Join(" -> ", m.Providers.Select(p => p.Name + (p.Writable ? "(w)" : ""))),
            CacheText = FormatBytes(m.CacheBytes) + " / " + FormatBytes(m.CacheCapacity),
            Error = m.Error ?? ""
        }).ToList();
    }

    private string? SelectedName()
    {
        if (MountList.SelectedItem is MountRow row) return row.Name;
        return (MountList.ItemsSource as IReadOnlyList<MountRow>)?.FirstOrDefault()?.Name;
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => RefreshStatus();

    private void OnRefreshHead(object sender, RoutedEventArgs e) =>
        Call(new ControlRequest { Cmd = ControlCommands.RefreshHead, Name = SelectedName() });

    private void OnMount(object sender, RoutedEventArgs e) =>
        Call(new ControlRequest { Cmd = ControlCommands.Mount, Name = SelectedName() });

    private void OnUnmount(object sender, RoutedEventArgs e) =>
        Call(new ControlRequest { Cmd = ControlCommands.Unmount, Name = SelectedName() });

    private void OnClearCache(object sender, RoutedEventArgs e) =>
        Call(new ControlRequest { Cmd = ControlCommands.ClearCache, Name = SelectedName() });

    private void OnSetPin(object sender, RoutedEventArgs e)
    {
        if (!long.TryParse(PinBox.Text.Trim(), out var rev))
        {
            StatusText.Text = "invalid revision";
            return;
        }
        Call(new ControlRequest { Cmd = ControlCommands.SetPin, Name = SelectedName(), Rev = rev });
    }

    private static string FormatBytes(long bytes)
    {
        double value = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return value.ToString("0.#") + units[i];
    }
}
