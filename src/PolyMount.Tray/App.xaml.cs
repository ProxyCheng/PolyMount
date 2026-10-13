using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace PolyMount.Tray;

public partial class App : Application
{
    private NotifyIcon? _tray;
    private DashboardWindow? _dashboard;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "PolyMount"
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Dashboard", null, (_, _) => ShowDashboard());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowDashboard();

        ShowDashboard();
    }

    private void ShowDashboard()
    {
        if (_dashboard is { IsLoaded: true })
        {
            _dashboard.RefreshStatus();
            _dashboard.Activate();
            return;
        }

        _dashboard = new DashboardWindow();
        _dashboard.Closed += (_, _) => _dashboard = null;
        _dashboard.Show();
        _dashboard.Activate();
    }

    private void ExitApp()
    {
        if (_tray != null) _tray.Visible = false;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.OnExit(e);
    }
}
