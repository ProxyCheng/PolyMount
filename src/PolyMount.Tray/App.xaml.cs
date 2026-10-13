using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace PolyMount.Tray;

public partial class App : Application
{
    private NotifyIcon? _tray;
    private DashboardWindow? _dashboard;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Launched from the Explorer context menu: show just the layer view, no tray icon.
        int idx = Array.IndexOf(e.Args, "--probe");
        if (idx >= 0 && idx + 1 < e.Args.Length)
        {
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            ShowProbe(e.Args[idx + 1]);
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "PolyMount"
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Dashboard", null, (_, _) => ShowDashboard());

        var shellItem = new ToolStripMenuItem("Explorer context menu") { Checked = ShellMenuInstaller.IsInstalled() };
        shellItem.Click += (_, _) => ToggleShellMenu(shellItem);
        menu.Items.Add(shellItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowDashboard();

        ShowDashboard();
    }

    private void ShowProbe(string path)
    {
        try
        {
            var resp = ControlClient.Probe(path);
            if (!resp.Ok || resp.Probe == null)
            {
                MessageBox.Show(resp.Error ?? "could not get layer info", "PolyMount", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown();
                return;
            }
            new LayerVisualizerWindow(path, resp.Probe).Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to connect to the PolyMount service: " + ex.Message, "PolyMount", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void ToggleShellMenu(ToolStripMenuItem item)
    {
        try
        {
            var exe = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (ShellMenuInstaller.IsInstalled())
            {
                ShellMenuInstaller.Uninstall();
                item.Checked = false;
            }
            else
            {
                ShellMenuInstaller.Install(exe);
                item.Checked = true;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PolyMount", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
