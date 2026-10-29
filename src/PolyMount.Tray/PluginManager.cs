using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using PolyMount.Core;
using PolyMount.Core.Ipc;
using QtSharp.QtCore;
using QtSharp.QtGui;
using QtSharp.QtWidgets;

namespace PolyMount.Tray;

/// <summary>What the service loads, what each plugin contributes, and how to add or switch one.
///
/// This is the workbench's second section, beside the mount overview, not a window of its own.
/// It is a list of installed plugins and, below it, the DLLs the plugin folder holds but the
/// configuration does not name.
///
/// **A running service makes the section read-only.** Changing a plugin's set restarts every
/// worker and remounts every instance, and an imported DLL cannot be replaced while the service
/// holds it open; so the adjustment is an offline act. With the service stopped the section reads
/// and writes the configuration file itself (located from the service registration, the
/// installation metadata or the last time the service answered). The state it shows then is the
/// configuration's — enabled, disabled, missing — not what a running host loaded.
///
/// Each row is two lines: what the plugin calls itself, its state and the controls; and what it
/// adds, aligned under the title. The description, the file it came from and the full list of
/// extension points are the row's tooltip. An error is the only thing that gets a red line of its
/// own.</summary>
internal sealed class PluginManager : QWidget
{
    private readonly SupervisorClient _client = SupervisorClient.Default;
    private readonly QLineEdit _search;
    private readonly QPushButton _refresh, _import, _openFolder;
    private readonly QLabel _notice, _installedHeading, _availableHeading, _status;
    private readonly QWidget _installedPanel, _availablePanel, _emptyPanel;
    private readonly QVBoxLayout _installedRows, _availableRows;
    private readonly QLabel _emptyTitle, _emptyHint;
    private readonly QPushButton _emptyRetry, _emptyImport, _emptyFolder;
    private readonly BusyLine _busy;
    private readonly QTimer _timer;
    private readonly List<QWidget> _installedWidgets = new(), _availableWidgets = new();

    private ControlResponse? _catalog;
    private Task<ControlResponse>? _pending;
    private string _query = "";
    private bool _offline;
    /// <summary>What the installed list was built from the last time it was built. The five-second
    /// poll rebuilds a row only when its plugin actually changed: a row that unfolds into a
    /// plugin's own page must not fold itself back, and the page inside it must not be thrown away
    /// and asked for again while somebody is reading it.</summary>
    private string _installedFingerprint = "";
    /// <summary>How to unfold a row, and the pages it holds, by the row's key. Registered by the row
    /// itself so the smoke run can drive the same thing a reader clicks — a row that does not unfold
    /// into its plugin's page is the failure worth catching, and it cannot be seen any other way.</summary>
    private readonly Dictionary<string, Action<bool>> _unfolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<PluginPanelBody>> _rowPages = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The configuration and the folders the service uses. Learned from the service while
    /// it answers, and from its registration when it does not; the plugin section edits the file
    /// itself only in that second state.</summary>
    private string? _configPath, _pluginsDirectory, _serviceDirectory;
    private DateTime _nextPoll = DateTime.MinValue;

    private static string T(string key) => I18n.T(key);

    public PluginManager()
    {
        // A section of the workbench, not a page of its own: the sidebar stays where it is and
        // swaps the right-hand side. The outer page owns the scrolling, so this carries none of
        // its own — two scroll areas nested in one another is the kind of layout nobody can size.
        // The frame every section shares: background, margins, and the title with its muted line.
        var root = PageChrome.Apply(this, "plugins.title", "plugins.subtitle", out var header);
        _refresh = IconButton("refresh", "plugins.refresh", Load);
        header.AddWidget(_refresh);
        _openFolder = TextButton("plugins.openFolder", OpenFolder, "folder");
        I18n.Bind(_openFolder.SetToolTip, "plugins.openFolder.tip");
        header.AddWidget(_openFolder);
        _import = TextButton("plugins.import", Import, "plus", primary: true);
        I18n.Bind(_import.SetToolTip, "plugins.import.tip");
        header.AddWidget(_import);
        root.AddLayout(header);

        // The one sentence that says whether this section can be edited. It is here rather than in
        // a tooltip because a greyed-out row with no explanation reads as a broken page.
        _notice = PlainLabel("", "notice");
        root.AddWidget(_notice);

        _search = new QLineEdit();
        _search.SetClearButtonEnabled(true);
        I18n.Bind(_search.SetPlaceholderText, "plugins.search");
        _search.TextChanged += text =>
        {
            _query = (text ?? "").Trim();
            Fill();
        };
        // A search box over four rows is a control that only makes the section look busier. It
        // appears when the list is long enough that finding a row is a task.
        _search.Hide();
        root.AddWidget(_search);

        // The one empty state, reused for "nothing installed" and "nothing to read": both are a
        // sentence about why the list is empty and the action that changes it.
        _emptyPanel = Panel("surface");
        var empty = new QVBoxLayout(_emptyPanel);
        empty.SetContentsMargins(24, 26, 24, 26);
        empty.SetSpacing(8);
        _emptyTitle = PlainLabel("", "sectionTitle");
        _emptyTitle.SetAlignment(Qt.Alignment.AlignCenter);
        empty.AddWidget(_emptyTitle);
        _emptyHint = PlainLabel("", "muted");
        _emptyHint.SetAlignment(Qt.Alignment.AlignCenter);
        empty.AddWidget(_emptyHint);
        var emptyActions = new QHBoxLayout();
        emptyActions.AddStretch(1);
        _emptyRetry = TextButton("plugins.retry", Load, "refresh");
        emptyActions.AddWidget(_emptyRetry);
        _emptyImport = TextButton("plugins.import", Import, "plus", primary: true);
        emptyActions.AddWidget(_emptyImport);
        _emptyFolder = TextButton("plugins.openFolder", OpenFolder, "folder");
        emptyActions.AddWidget(_emptyFolder);
        emptyActions.AddStretch(1);
        empty.AddLayout(emptyActions);
        root.AddWidget(_emptyPanel);

        _installedHeading = PlainLabel("", "eyebrow");
        root.AddWidget(_installedHeading);
        _installedPanel = Panel("surface");
        _installedRows = new QVBoxLayout(_installedPanel);
        _installedRows.SetContentsMargins(0, 4, 0, 4);
        _installedRows.SetSpacing(0);
        root.AddWidget(_installedPanel);

        _availableHeading = PlainLabel("", "eyebrow");
        root.AddWidget(_availableHeading);
        _availablePanel = Panel("surface");
        _availableRows = new QVBoxLayout(_availablePanel);
        _availableRows.SetContentsMargins(0, 4, 0, 4);
        _availableRows.SetSpacing(0);
        root.AddWidget(_availablePanel);

        root.AddWidget(Label("plugins.footnote", "muted"));
        root.AddStretch(1);
        _busy = new BusyLine();
        root.AddWidget(_busy);
        _status = PlainLabel("", "muted");
        root.AddWidget(_status);

        _timer = new QTimer(this);
        _timer.Timeout += Tick;
        _timer.Start(100);
        I18n.OnChanged(Fill, this);
        TrayTheme.OnChanged(Fill, this);
        Load();
    }

    // ---- data ---------------------------------------------------------------------------

    /// <summary>Asks the service first. That is both the freshest answer and the way this section
    /// learns which configuration and plugin folder the service uses; when it does not answer,
    /// the same question is put to the files.</summary>
    private void Load()
    {
        if (_pending != null) return;
        _pending = Task.Run(() => _client.Send(new ControlRequest { Cmd = ControlCommands.Plugins }));
        _status.SetText(T("plugins.loading"));
        SetStatusStyle(false);
        _nextPoll = DateTime.UtcNow.AddSeconds(5);
        UpdateBusy();
    }

    private void Tick()
    {
        if (_pending is { IsCompleted: true } pending)
        {
            _pending = null;
            ControlResponse? response = null;
            try { response = pending.GetAwaiter().GetResult(); }
            catch (Exception) { }
            if (response is { Ok: true })
            {
                _offline = false;
                _catalog = response;
                Remember(response);
                _status.SetText("");
                SetStatusStyle(false);
            }
            else
            {
                _offline = true;
                try { LoadOffline(); } catch (Exception) { _catalog = null; }
                _status.SetText("");
                SetStatusStyle(false);
            }
            _nextPoll = DateTime.UtcNow.AddSeconds(5);
            UpdateBusy();
            Fill();
            return;
        }
        // The sidebar's stop/start is the only other way the state changes, and this is how the
        // section notices it: a cheap retry every few seconds, never while a read is in flight.
        if (_pending == null && DateTime.UtcNow >= _nextPoll) Load();
    }

    private void Remember(ControlResponse response)
    {
        if (response.PluginDirectory is { Length: > 0 } directory)
        {
            _pluginsDirectory = directory;
            UiSettings.Set("Plugins.Directory", directory);
            _serviceDirectory ??= Path.GetDirectoryName(directory);
        }
        if (response.ConfigPath is { Length: > 0 } config)
        {
            _configPath = config;
            UiSettings.Set("Plugins.ConfigPath", config);
        }
        // What the plugins said about themselves, so an offline list keeps human names instead of
        // falling back to file names the moment the service stops.
        try { UiSettings.Set("Plugins.Catalog", JsonSerializer.Serialize(response.Plugins)); }
        catch (Exception) { }
    }

    /// <summary>The same list read from the configuration. Titles and what a plugin adds come from
    /// the last online reading; everything that decides what a row does comes from the file.</summary>
    private void LoadOffline()
    {
        _catalog = null;
        _configPath ??= UiSettings.Get("Plugins.ConfigPath");
        _pluginsDirectory ??= UiSettings.Get("Plugins.Directory");
        if (string.IsNullOrEmpty(_configPath) || !File.Exists(_configPath)) LocateFromMachine();
        if (string.IsNullOrEmpty(_configPath) || !File.Exists(_configPath)) return;
        if (string.IsNullOrEmpty(_serviceDirectory))
            _serviceDirectory = _pluginsDirectory is { Length: > 0 } pluginsFolder && string.Equals(Path.GetFileName(pluginsFolder), "plugins", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(pluginsFolder)
                : Path.GetDirectoryName(_configPath);
        AppConfig? config;
        try { config = AppConfig.Load(_configPath); }
        catch (Exception) { return; }
        if (config == null) return;

        var cached = CachedCatalog();
        var plugins = new List<PluginView>();
        foreach (var entry in config.Plugins)
        {
            string full = Resolve(entry.AssemblyPath);
            cached.TryGetValue(full, out var known);
            plugins.Add(new PluginView
            {
                AssemblyPath = entry.AssemblyPath,
                FileName = Path.GetFileName(entry.AssemblyPath.Replace('\\', '/')),
                FullPath = full,
                Enabled = entry.Enabled,
                Present = full.Length > 0 && File.Exists(full),
                Loaded = false,
                Version = known?.Version is { Length: > 0 } version ? version
                    : full.Length > 0 ? PluginLoader.ReadVersion(full) : "",
                // Read from the file when the service has never described it — a fresh install
                // whose service has not run yet is exactly when an incompatible plugin has to be
                // visible rather than waiting for a start-up that would refuse the set.
                ApiVersion = known?.ApiVersion is { Length: > 0 } cachedApi ? cachedApi
                    : full.Length > 0 && File.Exists(full) ? PluginLoader.ReadApiVersion(full) : "",
                Title = known?.Title,
                Description = known?.Description,
                Icon = known?.Icon ?? "",
                SourceTypes = known?.SourceTypes ?? new(),
                LayerTypes = known?.LayerTypes ?? new(),
                Presets = known?.Presets ?? new(),
                Strategies = known?.Strategies ?? new(),
                Commands = known?.Commands ?? new(),
                Operations = known?.Operations ?? new()
            });
        }

        string directory = _pluginsDirectory
            ?? (_serviceDirectory is { Length: > 0 } service ? Path.Combine(service, "plugins") : "");
        var files = new List<PluginFileView>();
        if (directory.Length > 0 && config.Plugins != null)
            foreach (string file in PluginLoader.DiscoverFiles(config.Plugins, _serviceDirectory ?? AppContext.BaseDirectory))
            {
                long size = 0;
                string modified = "";
                try
                {
                    var info = new FileInfo(file);
                    size = info.Length;
                    modified = info.LastWriteTimeUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                }
                catch (Exception) { }
                files.Add(new PluginFileView
                {
                    Name = Path.GetFileName(file),
                    FullPath = file,
                    Size = size,
                    ModifiedUtc = modified,
                    Version = PluginLoader.ReadVersion(file)
                });
            }
        _catalog = new ControlResponse
        {
            Plugins = plugins,
            PluginFiles = files,
            PluginDirectory = directory,
            ConfigPath = _configPath
        };
    }

    private Dictionary<string, PluginView> CachedCatalog()
    {
        var map = new Dictionary<string, PluginView>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string? json = UiSettings.Get("Plugins.Catalog");
            if (string.IsNullOrEmpty(json) || JsonSerializer.Deserialize<List<PluginView>>(json) is not { } list) return map;
            foreach (var plugin in list)
                if (!string.IsNullOrEmpty(plugin.FullPath)) map[plugin.FullPath] = plugin;
        }
        catch (Exception) { }
        return map;
    }

    /// <summary>Finds the service's configuration when it is not answering: the registration says
    /// what it was started with, the installation metadata says where the product lives, and the
    /// documented default is the last resort.</summary>
    private void LocateFromMachine()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PolyMount");
            if (key?.GetValue("ImagePath") is string image && image.Trim().Length > 0)
            {
                var (exe, config) = ParseImagePath(image);
                if (!string.IsNullOrEmpty(exe))
                {
                    _serviceDirectory = Path.GetDirectoryName(exe);
                    _pluginsDirectory ??= Path.Combine(_serviceDirectory!, "plugins");
                }
                _configPath = config ?? (_serviceDirectory is { Length: > 0 } service
                    ? Path.Combine(service, "config.json") : null);
            }
        }
        catch (Exception) { }

        if (string.IsNullOrEmpty(_configPath) || !File.Exists(_configPath))
        {
            try
            {
                string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
                string install = Path.Combine(root, "install.json");
                if (File.Exists(install))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(install));
                    if (document.RootElement.TryGetProperty("ConfigPath", out var configured) &&
                        configured.GetString() is { Length: > 0 } value)
                    {
                        _configPath = Environment.ExpandEnvironmentVariables(value);
                        _serviceDirectory ??= Path.Combine(root, "Service");
                        _pluginsDirectory ??= Path.Combine(_serviceDirectory, "plugins");
                    }
                }
            }
            catch (Exception) { }
        }

        if (string.IsNullOrEmpty(_configPath) || !File.Exists(_configPath))
        {
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PolyMount", "config.json");
            if (File.Exists(data))
            {
                _configPath = data;
                _serviceDirectory ??= Path.GetDirectoryName(data);
            }
        }

        if (!string.IsNullOrEmpty(_configPath)) UiSettings.Set("Plugins.ConfigPath", _configPath);
        if (!string.IsNullOrEmpty(_pluginsDirectory)) UiSettings.Set("Plugins.Directory", _pluginsDirectory);
    }

    /// <summary>The executable and the optional <c>--config</c> a service registration carries.
    /// The service's own name is the default when it names no configuration.</summary>
    private static (string? Exe, string? Config) ParseImagePath(string image)
    {
        string rest = image.Trim();
        string exe;
        if (rest.StartsWith('"'))
        {
            int end = rest.IndexOf('"', 1);
            if (end < 0) return (rest.Trim('"'), null);
            exe = rest[1..end];
            rest = rest[(end + 1)..];
        }
        else
        {
            int space = rest.IndexOf(' ');
            exe = space < 0 ? rest : rest[..space];
            rest = space < 0 ? "" : rest[(space + 1)..];
        }
        int at = rest.IndexOf("--config", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return (exe, null);
        string tail = rest[(at + "--config".Length)..].Trim();
        string? config;
        if (tail.StartsWith('"'))
        {
            int end = tail.IndexOf('"', 1);
            config = end > 1 ? tail[1..end] : null;
        }
        else
        {
            int space = tail.IndexOf(' ');
            config = space < 0 ? tail : tail[..space];
        }
        return (exe, string.IsNullOrWhiteSpace(config) ? null : config);
    }

    private string Resolve(string assemblyPath)
    {
        string directory = _serviceDirectory ?? Path.GetDirectoryName(_configPath ?? "") ?? AppContext.BaseDirectory;
        try { return Path.GetFullPath(assemblyPath.Replace('\\', '/'), directory); }
        catch (Exception) { return assemblyPath; }
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private PluginConfig? Entry(AppConfig config, string assemblyPath) =>
        config.Plugins.FirstOrDefault(plugin => SamePath(Resolve(plugin.AssemblyPath), Resolve(assemblyPath)));

    private void SetStatusStyle(bool error)
    {
        string color = error ? TrayTheme.Tokens.DangerStrong : TrayTheme.Tokens.Muted;
        string style = "color: " + color + ";";
        if (_status.StyleSheet != style) _status.SetStyleSheet(style);
    }

    private void UpdateBusy()
    {
        bool busy = _pending != null;
        _busy.SetBusy(busy);
        _refresh.SetEnabled(!busy);
        // Every control that changes the set lives behind "the service is stopped"; the notice
        // above the list says why.
        _import.SetEnabled(!busy && _offline);
        _emptyImport.SetEnabled(!busy && _offline);
        _openFolder.SetEnabled(!busy && !string.IsNullOrEmpty(_catalog?.PluginDirectory));
        _emptyFolder.SetEnabled(!busy && !string.IsNullOrEmpty(_catalog?.PluginDirectory));
        _search.SetEnabled(!busy && _catalog != null);
    }

    // ---- render -------------------------------------------------------------------------

    private void Fill()
    {
        if (_status == null) return;
        UpdateBusy();
        UpdateNotice();
        var catalog = _catalog;
        var installed = catalog?.Plugins ?? new List<PluginView>();
        string query = _query;
        var visible = installed.Where(plugin => Matches(plugin, query)).ToList();
        bool canEdit = _offline && _pending == null;

        bool empty = catalog == null || installed.Count == 0;
        _emptyPanel.SetVisible(empty);
        if (catalog == null)
        {
            SetText(_emptyTitle, T("plugins.noSource"));
            SetText(_emptyHint, T("plugins.noSourceHint"));
            _emptyRetry.SetVisible(true);
            _emptyImport.SetVisible(false);
            _emptyFolder.SetVisible(false);
        }
        else if (installed.Count == 0)
        {
            SetText(_emptyTitle, T("plugins.noneInstalled"));
            SetText(_emptyHint, T("plugins.noneInstalledHint"));
            _emptyRetry.SetVisible(false);
            _emptyImport.SetVisible(true);
            _emptyFolder.SetVisible(!string.IsNullOrEmpty(catalog.PluginDirectory));
        }

        _installedHeading.SetVisible(!empty);
        _installedPanel.SetVisible(!empty);
        _search.SetVisible(!empty && installed.Count > 6);
        SetText(_installedHeading, I18n.F("plugins.installed", installed.Count));
        // Rows carry state a rebuild would lose: which ones are unfolded, and the plugin pages
        // inside them. So they are put together only when something about the plugins — or the
        // language, or whether this section may be edited — actually changed.
        string fingerprint = Fingerprint(visible, canEdit);
        if (fingerprint != _installedFingerprint)
        {
            _installedFingerprint = fingerprint;
            Clear(_installedRows, _installedWidgets);
            _unfolders.Clear();
            _rowPages.Clear();
            for (int i = 0; i < visible.Count; i++)
            {
                if (i > 0) AddSeparator(_installedRows, _installedWidgets);
                var row = InstalledRow(visible[i], canEdit);
                _installedWidgets.Add(row);
                _installedRows.AddWidget(row);
            }
            if (visible.Count == 0 && installed.Count > 0)
            {
                var none = PlainLabel(T("plugins.noMatch"), "emptyState");
                _installedWidgets.Add(none);
                _installedRows.AddWidget(none);
            }
        }

        // The available section exists only when there is something to offer.
        var files = catalog?.PluginFiles ?? new List<PluginFileView>();
        bool available = !empty && files.Count > 0;
        _availableHeading.SetVisible(available);
        _availablePanel.SetVisible(available);
        SetText(_availableHeading, I18n.F("plugins.available", files.Count));
        Clear(_availableRows, _availableWidgets);
        if (available)
            for (int i = 0; i < files.Count; i++)
            {
                if (i > 0) AddSeparator(_availableRows, _availableWidgets);
                var row = AvailableRow(files[i], canEdit);
                _availableWidgets.Add(row);
                _availableRows.AddWidget(row);
            }
    }

    private void UpdateNotice()
    {
        string key = !_offline ? "plugins.notice.running"
            : _catalog == null ? "plugins.notice.noSource"
            : "plugins.notice.stopped";
        SetText(_notice, T(key));
        string style = $"background: {TrayTheme.Tokens.InfoSoft}; border: 1px solid {TrayTheme.Tokens.InfoBorder};"
            + $" border-radius: 8px; padding: 9px 12px; color: {TrayTheme.Tokens.Info};";
        if (_notice.StyleSheet != style) _notice.SetStyleSheet(style);
    }

    private static bool Matches(PluginView plugin, string query)
    {
        if (query.Length == 0) return true;
        bool Hit(string? value) => value != null && value.Contains(query, StringComparison.OrdinalIgnoreCase);
        return Hit(plugin.FileName) || Hit(plugin.AssemblyPath) || Hit(plugin.Version) ||
            Hit(plugin.Title?.Resolve(I18n.Language)) || Hit(plugin.Description?.Resolve(I18n.Language)) ||
            plugin.SourceTypes.Any(Hit) || plugin.LayerTypes.Any(Hit) || plugin.Presets.Any(Hit) ||
            plugin.Strategies.Any(Hit) || plugin.Operations.Any(Hit);
    }

    private static void AddSeparator(QVBoxLayout layout, List<QWidget> widgets)
    {
        var separator = new QFrame();
        separator.SetObjectName("pluginSeparator");
        separator.SetFrameShape(QFrame.Shape.HLine);
        widgets.Add(separator);
        layout.AddWidget(separator);
    }

    private static void Clear(QVBoxLayout layout, List<QWidget> widgets)
    {
        foreach (var widget in widgets)
        {
            layout.RemoveWidget(widget);
            widget.Hide();
            widget.DeleteLater();
        }
        widgets.Clear();
    }

    /// <summary>Everything a row is drawn from, as one string: the plugins in order, what each one
    /// reports about itself, the language, the scheme, and whether this section may be edited.
    /// Equal means the rows on screen still describe the world, so nothing has to be put together
    /// again — which is what keeps an unfolded row unfolded.</summary>
    private static string Fingerprint(List<PluginView> plugins, bool canEdit) =>
        string.Join("\n", plugins.Select(plugin => string.Join("|",
            plugin.FullPath, plugin.FileName, plugin.Enabled, plugin.Loaded, plugin.Present,
            plugin.Version, plugin.ApiVersion, plugin.Error, plugin.HasManifest,
            string.Join(",", plugin.Panels), string.Join(",", plugin.Permissions))))
        + "\n" + canEdit + "|" + I18n.Language + "|" + TrayTheme.Mode;

    /// <summary>Where a row remembers whether it is unfolded. The file, not the plugin's name: two
    /// installations can carry a plugin of the same name.</summary>
    private static string ExpandedKey(PluginView plugin) => "Plugins.Expanded." + RowKey(plugin);

    private static string RowKey(PluginView plugin) =>
        plugin.FullPath.Length > 0 ? plugin.FullPath : plugin.FileName;

    /// <summary>One fact about a plugin, as a label and a value in one aligned column. A wall of
    /// "key value · key value" is read once and skipped after that; the file path is the one thing
    /// here a reader copies.</summary>
    private static QHBoxLayout DetailLine(string label, string value)
    {
        var line = new QHBoxLayout();
        line.SetSpacing(12);
        var name = PlainLabel(label, "detailLabel");
        name.SetWordWrap(false);
        name.SetMinimumWidth(96);
        line.AddWidget(name);
        var text = PlainLabel(value, "detailValue");
        text.SetTextInteractionFlags(Qt.TextInteractionFlags.TextSelectableByMouse);
        line.AddWidget(text, 1);
        return line;
    }

    private static bool IsExpanded(PluginView plugin) => UiSettings.Get(ExpandedKey(plugin)) == "1";

    private QWidget InstalledRow(PluginView plugin, bool canEdit)
    {
        var row = new QWidget();
        row.SetObjectName("pluginRow");
        row.SetAttribute(Qt.WidgetAttribute.WA_StyledBackground);
        var column = new QVBoxLayout(row);
        column.SetContentsMargins(16, 12, 10, 12);
        column.SetSpacing(5);
        // A plugin built against another major contract can never run here, whatever the service
        // is doing. The row stays — it is a file on this machine and removing it is still a
        // decision someone may take — but it is drawn as what it is: greyed, unswitchable, with
        // the reason in the tooltip rather than only in an error the host never got far enough to
        // report.
        bool incompatible = IsIncompatible(plugin);
        string incompatibleWhy = incompatible
            ? I18n.F("plugins.tooltip.incompatible", plugin.ApiVersion, PluginApi.Version)
            : "";

        var line = new QHBoxLayout();
        line.SetSpacing(10);
        // The row unfolds into everything about the plugin: what it says it does, the file it came
        // from, what it adds, and — when it offers any — its own management page. That is where a
        // plugin's page lives by default; the sidebar copies are for a plugin that asked for one.
        var disclosure = new QPushButton();
        disclosure.SetObjectName("disclosure");
        disclosure.SetFixedSize(18, 18);
        disclosure.SetIconSize(new QSize(12, 12));
        disclosure.SetCursor(new QCursor(Qt.CursorShape.PointingHandCursor));
        line.AddWidget(disclosure);
        // The plugin's own mark, tinted by its state: one glyph tells a reader what kind of
        // extension it is and whether it is running, without a second dot beside it.
        var mark = new QLabel();
        mark.SetPixmap(TrayIcons.Get(plugin.Icon is { Length: > 0 } icon ? icon : "plugin",
            StateColor(plugin)).Pixmap(17, 17));
        line.AddWidget(mark);

        var name = PlainLabel(DisplayName(plugin), incompatible ? "muted" : "pluginName");
        name.SetWordWrap(false);
        line.AddWidget(name);
        if (plugin.Version.Length > 0)
        {
            var version = PlainLabel(I18n.F("plugins.version", plugin.Version), "muted");
            version.SetWordWrap(false);
            line.AddWidget(version);
        }
        var state = PlainLabel(StateText(plugin), "muted");
        state.SetWordWrap(false);
        state.SetStyleSheet("color: " + StateColor(plugin) + "; font-weight: 600;");
        line.AddWidget(state);
        // What the plugin says it reaches for, in its own vocabulary (network, registry, native).
        // A declaration rather than a restriction: it is here so that enabling a plugin is a
        // decision someone can make with the facts in front of them.
        if (plugin.Permissions.Count > 0)
        {
            var reaches = PlainLabel(T("plugins.permissions") + " " + string.Join(", ", plugin.Permissions), "muted");
            reaches.SetWordWrap(false);
            line.AddWidget(reaches);
        }
        line.AddStretch(1);

        var remove = IconButton("trash", "plugins.remove", () => Remove(plugin));
        remove.SetEnabled(canEdit);
        line.AddWidget(remove);

        var check = new QCheckBox();
        check.SetChecked(plugin.Enabled);
        // Neither direction: this build cannot run the plugin, so there is no state to switch
        // between. The trash button beside it is the way out — it takes the entry out of the
        // configuration, which is what makes a service that refuses this plugin start again.
        string checkTip = incompatible
            ? incompatibleWhy
            : T(plugin.Enabled ? "plugins.tooltip.disable" : "plugins.tooltip.enable");
        check.SetToolTip(checkTip);
        check.SetAccessibleName(checkTip);
        check.Toggled += on =>
        {
            if (incompatible && on) { check.SetChecked(false); return; }
            if (on != plugin.Enabled) Toggle(plugin, on);
        };
        check.SetEnabled(canEdit && !incompatible);
        line.AddWidget(check);
        column.AddLayout(line);

        // Said on the row, not only on hover: a switch that refuses to move with no visible
        // reason reads as a bug. This is the one place that can answer "why can I not turn it
        // on" — the installer's page only notes the mismatch, because there the decision is
        // whether the file is kept, not whether it will load.
        if (incompatible)
            column.AddWidget(PlainLabel(incompatibleWhy, "errorBox"));

        // The second line is the plugin's work, aligned under its title rather than under the
        // icon: the row is two lines so neither has to be read at a glance, and the technical
        // detail still has somewhere to live besides the tooltip.
        string summary = SummaryText(plugin);
        if (summary.Length > 0)
        {
            var detail = new QHBoxLayout();
            detail.SetContentsMargins(0, 0, 0, 0);
            // Aligned under the title: the disclosure and the plugin's mark sit to the left of it.
            detail.AddSpacing(55);
            var contributions = PlainLabel(summary, incompatible ? "muted" : "pluginContribution");
            contributions.SetWordWrap(false);
            detail.AddWidget(contributions, 1);
            column.AddLayout(detail);
        }

        if (!string.IsNullOrWhiteSpace(plugin.Error))
            column.AddWidget(PlainLabel(T("plugins.error") + " · " + plugin.Error, "errorBox"));

        // ---- unfolded: everything about this plugin, including the page it offers -----------
        //
        // The body is put together the first time the row is unfolded. A collapsed row costs one
        // button, and a row that is never opened never asks an instance for a page.
        QWidget? body = null;
        bool open = IsExpanded(plugin);
        var pages = new List<PluginPanelBody>();

        QWidget BuildBody()
        {
            // A sunken band inside the row, not a card of its own: what opened belongs to the row
            // the reader clicked. Everything in it is aligned to one column — description, the
            // facts about the file, then the page the plugin offers.
            var host = new QWidget();
            host.SetObjectName("pluginBody");
            host.SetAttribute(Qt.WidgetAttribute.WA_StyledBackground);
            var layout = new QVBoxLayout(host);
            layout.SetContentsMargins(16, 12, 14, 12);
            layout.SetSpacing(10);

            if (plugin.Description?.Resolve(I18n.Language) is { Length: > 0 } description)
                layout.AddWidget(PlainLabel(description, "muted"));

            var details = new List<(string Label, string Value)>();
            if (plugin.Id.Length > 0) details.Add((T("plugins.detail.id"), plugin.Id));
            details.Add((T("plugins.detail.file"), plugin.FullPath));
            if (plugin.Version.Length > 0) details.Add((T("plugins.detail.version"), plugin.Version));
            if (plugin.ApiVersion.Length > 0) details.Add((T("plugins.apiVersion"), plugin.ApiVersion));
            if (plugin.Roles.Count > 0)
                details.Add((T("plugins.roles"), string.Join(" / ", plugin.Roles.Select(RoleName))));
            if (plugin.Permissions.Count > 0)
                details.Add((T("plugins.permissions"), string.Join(", ", plugin.Permissions)));
            if (HasDetails(plugin)) details.Add((T("plugins.detail.adds"), ContributionText(plugin)));
            foreach (var (label, value) in details) layout.AddLayout(DetailLine(label, value));

            foreach (string id in plugin.Panels)
            {
                var descriptor = _catalog?.Panels.FirstOrDefault(panel =>
                    string.Equals(panel.Id, id, StringComparison.OrdinalIgnoreCase));
                if (descriptor == null) continue;
                var separator = new QFrame();
                separator.SetObjectName("pluginSeparator");
                separator.SetFrameShape(QFrame.Shape.HLine);
                layout.AddWidget(separator);

                // A heading over a page that is called what the plugin is called would be the same
                // words twice, so it says what the section is instead: the plugin's own management.
                var heading = new QHBoxLayout();
                heading.SetSpacing(8);
                string pageTitle = descriptor.Title.Resolve(I18n.Language);
                var mark = new QLabel();
                mark.SetPixmap(TrayIcons.Get(descriptor.Icon is { Length: > 0 } icon ? icon : "plugin",
                    TrayTheme.Tokens.Icon).Pixmap(14, 14));
                heading.AddWidget(mark);
                heading.AddWidget(PlainLabel(
                    string.Equals(pageTitle, DisplayName(plugin), StringComparison.Ordinal)
                        ? T("plugins.manage")
                        : pageTitle, "panelHeading"), 1);
                var page = new PluginPanelBody(id, flat: true);
                var refresh = IconButton("refresh", "plugins.refresh", page.Reload);
                heading.AddWidget(refresh);
                layout.AddLayout(heading);
                pages.Add(page);
                layout.AddWidget(page);
            }
            return host;
        }

        void Unfold(bool on, bool foldOthers = false)
        {
            open = on;
            if (on && foldOthers)
                foreach (var other in _unfolders.Where(entry =>
                             !string.Equals(entry.Key, RowKey(plugin), StringComparison.OrdinalIgnoreCase)))
                    other.Value(false);
            if (on && body == null)
            {
                body = BuildBody();
                column.AddWidget(body);
            }
            body?.SetVisible(on);
            disclosure.SetIcon(TrayIcons.Get(on ? "chevron-down" : "chevron-right", TrayTheme.Tokens.Icon));
            string tip = T(on ? "plugins.collapse" : "plugins.expand");
            disclosure.SetToolTip(tip);
            disclosure.SetAccessibleName(tip);
            UiSettings.Set(ExpandedKey(plugin), on ? "1" : "0");
            if (on) foreach (var page in pages) page.Reload();
        }

        disclosure.Clicked += _ => Unfold(!open, foldOthers: true);
        Unfold(open);
        _unfolders[RowKey(plugin)] = on => Unfold(on);
        _rowPages[RowKey(plugin)] = pages;

        // The row carries the rest on hover: what the plugin says it does, the file it came from,
        // everything it adds, and why it did not load.
        var tooltip = new List<string>();
        if (incompatible) tooltip.Add(incompatibleWhy);
        if (plugin.Description?.Resolve(I18n.Language) is { Length: > 0 } description) tooltip.Add(description);
        var facts = new List<string>();
        if (plugin.Id.Length > 0) facts.Add(plugin.Id);
        if (plugin.Roles.Count > 0)
            facts.Add(T("plugins.roles") + " " + string.Join(" / ", plugin.Roles.Select(RoleName)));
        if (plugin.ApiVersion.Length > 0) facts.Add(T("plugins.apiVersion") + " " + plugin.ApiVersion);
        if (!plugin.HasManifest) facts.Add(T("plugins.manifest.none"));
        if (facts.Count > 0) tooltip.Add(string.Join("  ·  ", facts));
        tooltip.Add(plugin.FullPath);
        if (HasDetails(plugin)) tooltip.Add(ContributionText(plugin));
        if (!string.IsNullOrWhiteSpace(plugin.Error)) tooltip.Add(plugin.Error);
        row.SetToolTip(string.Join("\n", tooltip));
        return row;
    }

    /// <summary>The role in the reader's language: a plugin's code runs in the servers (where a
    /// layer or a source lives) or in a front-end (where a tool runs), or both.</summary>
    private static string RoleName(string role) => role switch
    {
        PluginRoleNames.Backend => T("plugins.role.backend"),
        PluginRoleNames.Frontend => T("plugins.role.frontend"),
        _ => role
    };

    private static bool HasDetails(PluginView plugin) =>
        plugin.SourceTypes.Count > 0 || plugin.LayerTypes.Count > 0 || plugin.Presets.Count > 0 ||
        plugin.Strategies.Count > 0 || plugin.Operations.Count > 0 || plugin.CredentialKinds.Count > 0;

    /// <summary>What the plugin calls itself, or the file name when it does not say. A plugin
    /// that only has a name can still be found by it.</summary>
    private static string DisplayName(PluginView plugin) =>
        plugin.Title?.Resolve(I18n.Language) is { Length: > 0 } title ? title : plugin.FileName;

    private QWidget AvailableRow(PluginFileView file, bool canEdit)
    {
        var row = new QWidget();
        row.SetObjectName("pluginRow");
        row.SetAttribute(Qt.WidgetAttribute.WA_StyledBackground);
        var line = new QHBoxLayout(row);
        line.SetContentsMargins(16, 10, 10, 10);
        line.SetSpacing(10);

        var icon = new QLabel();
        icon.SetPixmap(TrayIcons.Get("plugin", TrayTheme.Tokens.Icon).Pixmap(16, 16));
        line.AddWidget(icon);

        var name = PlainLabel(file.Name, "pluginName");
        name.SetWordWrap(false);
        line.AddWidget(name);
        if (file.Version.Length > 0)
        {
            var version = PlainLabel(I18n.F("plugins.version", file.Version), "muted");
            version.SetWordWrap(false);
            line.AddWidget(version);
        }
        string detail = ByteUnits.Format(file.Size);
        if (file.ModifiedUtc.Length > 0 && DateTime.TryParse(file.ModifiedUtc, I18n.Culture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var modified))
            detail += "  ·  " + I18n.F("plugins.modified", modified.ToLocalTime().ToString("d", I18n.Culture));
        var secondary = PlainLabel(detail, "pluginPath");
        secondary.SetWordWrap(false);
        line.AddWidget(secondary);
        line.AddStretch(1);

        if (file.InUse)
        {
            line.AddWidget(PlainLabel(T("plugins.inUse"), "muted"));
        }
        else
        {
            var add = TextButton("plugins.add", () => AddFile(file), "plus");
            add.SetEnabled(canEdit);
            line.AddWidget(add);
        }
        row.SetToolTip(file.FullPath);
        return row;
    }

    /// <summary>What a plugin adds, short enough to sit on the row: the source or layer it
    /// provides by name and the presets as a count. The full list — tools included — is the row's
    /// tooltip; a row that lists every command name is a row nobody reads.</summary>
    private static string SummaryText(PluginView plugin)
    {
        if (!plugin.Enabled || (!plugin.Loaded && plugin.Error != null)) return "";
        var parts = new List<string>();
        if (plugin.SourceTypes.Count > 0)
            parts.Add(I18n.F("plugins.summary.source", string.Join(", ", plugin.SourceTypes)));
        if (plugin.LayerTypes.Count > 0)
            parts.Add(I18n.F("plugins.summary.layer", string.Join(", ", plugin.LayerTypes)));
        if (plugin.Presets.Count == 1) parts.Add(I18n.F("plugins.summary.preset", plugin.Presets[0]));
        else if (plugin.Presets.Count > 1) parts.Add(I18n.F("plugins.summary.presets", plugin.Presets.Count));
        // A credential kind is something a plugin adds just as a layer or a preset is: the row
        // says so, or a plugin whose whole contribution is a way of signing in reads as one that
        // adds nothing.
        if (plugin.CredentialKinds.Count > 0)
            parts.Add(I18n.F("plugins.summary.credential", string.Join(", ", plugin.CredentialKinds)));
        if (parts.Count == 0) return "";
        return string.Join(" · ", parts.Take(3)) + (parts.Count > 3 ? " …" : "");
    }

    /// <summary>Whether the plugin was built against a plugin contract this build cannot run. Read
    /// from the version the file records, so it is answered for a plugin the service has never
    /// loaded — disabled, or refused before its code ran.</summary>
    internal static bool IsIncompatible(PluginView plugin) =>
        plugin.ApiVersion.Length > 0 && !PluginApi.IsCompatible(plugin.ApiVersion);

    private string StateKey(PluginView plugin) =>
        !plugin.Present ? "plugins.state.missing"
        : IsIncompatible(plugin) ? "plugins.state.incompatible"
        : !plugin.Enabled ? "plugins.state.disabled"
        : _offline ? "plugins.state.pending"
        : plugin.Loaded ? "plugins.state.loaded"
        : plugin.Error != null ? "plugins.state.failed"
        : "plugins.state.inactive";

    private string StateText(PluginView plugin) => T(StateKey(plugin));

    private string StateColor(PluginView plugin) => StateKey(plugin) switch
    {
        "plugins.state.loaded" => TrayTheme.Tokens.Success,
        "plugins.state.disabled" => TrayTheme.Tokens.Muted,
        "plugins.state.pending" => TrayTheme.Tokens.Warning,
        "plugins.state.inactive" => TrayTheme.Tokens.Warning,
        _ => TrayTheme.Tokens.DangerStrong
    };

    /// <summary>What the plugin adds, on one line: the extension points a reader came to see.
    /// Commands are left out — they are the plumbing behind the operations their own layer and
    /// source types already advertise.</summary>
    private static string ContributionText(PluginView plugin)
    {
        // A plugin that is not loaded has not been asked what it contributes, so the empty list
        // is not evidence of an empty plugin.
        if (!plugin.Enabled) return T("plugins.contributions.disabled");
        if (!plugin.Loaded && plugin.Error != null) return T("plugins.contributions.failed");
        var parts = new List<string>();
        void Add(string labelKey, IReadOnlyList<string> values)
        {
            if (values.Count == 0) return;
            var shown = values.Where(value => value.Length > 0).Take(4).ToArray();
            if (shown.Length == 0) return;
            int more = values.Count - shown.Length;
            parts.Add(T(labelKey) + " " + string.Join(", ", shown) + (more > 0 ? " +" + more : ""));
        }
        Add("plugins.contributes.source", plugin.SourceTypes);
        Add("plugins.contributes.layer", plugin.LayerTypes);
        Add("plugins.contributes.preset", plugin.Presets);
        Add("plugins.contributes.strategy", plugin.Strategies);
        Add("plugins.contributes.credential", plugin.CredentialKinds);
        Add("plugins.contributes.tool", plugin.Operations);
        return parts.Count == 0 ? T("plugins.contributions.none") : string.Join("  ·  ", parts);
    }

    // ---- actions ------------------------------------------------------------------------
    //
    // All of them require the service to be stopped. While it runs, the controls are disabled and
    // the notice says why; this early return is the same rule where a click could still land.

    private void Toggle(PluginView plugin, bool enabled)
    {
        if (_pending != null || !_offline) { Fill(); return; }
        if (enabled && !plugin.Present)
        {
            Feedback(T("plugins.state.missing"));
            Fill();
            return;
        }
        string path = plugin.AssemblyPath;
        OfflineChange(DisplayName(plugin), config =>
        {
            if (Entry(config, path) is { } entry) entry.Enabled = enabled;
        });
    }

    private void Remove(PluginView plugin)
    {
        if (_pending != null || !_offline) return;
        if (!Confirm(T("plugins.confirm.title"), I18n.F("plugins.confirm.remove", DisplayName(plugin)))) return;
        string path = plugin.AssemblyPath;
        OfflineChange(DisplayName(plugin), config =>
            config.Plugins.RemoveAll(entry => SamePath(Resolve(entry.AssemblyPath), Resolve(path))));
    }

    private void Import()
    {
        if (_pending != null || !_offline) return;
        string path = QFileDialog.GetOpenFileName(this, T("plugins.import"), "", T("plugins.import.filter"));
        if (string.IsNullOrEmpty(path)) return;
        ImportFile(path);
    }

    private void AddFile(PluginFileView file)
    {
        if (_pending != null || !_offline) return;
        ImportFile(file.FullPath);
    }

    /// <summary>Copies the DLL into the service's plugin folder (when it is not there already) and
    /// names it in the configuration. The contract is checked from metadata before anything is
    /// written, so a dependency or a random DLL is refused rather than saved into a configuration
    /// the service would then fail to start with.</summary>
    private void ImportFile(string source)
    {
        if (!File.Exists(source) || !string.Equals(Path.GetExtension(source), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            Feedback(T("plugins.notAPlugin"));
            return;
        }
        if (!PluginLoader.IsPluginAssembly(source))
        {
            Feedback(T("plugins.notAPlugin"));
            return;
        }
        string? directory = _pluginsDirectory
            ?? (_serviceDirectory is { Length: > 0 } service ? Path.Combine(service, "plugins") : null);
        if (string.IsNullOrEmpty(directory))
        {
            Feedback(T("plugins.notice.noSource"));
            return;
        }
        string name = Path.GetFileName(source);
        string target = Path.Combine(directory, name);
        bool known = (_catalog?.Plugins.Any(plugin => SamePath(plugin.FullPath, target)) ?? false)
            || (_catalog?.PluginFiles.Any(file => SamePath(file.FullPath, target)) ?? false);
        if (known && !Confirm(T("plugins.confirm.title"), I18n.F("plugins.confirm.replace", name))) return;

        bool existed = File.Exists(target);
        try
        {
            Directory.CreateDirectory(directory);
            if (!SamePath(Path.GetFullPath(source), Path.GetFullPath(target))) File.Copy(source, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Feedback(T("plugins.failed") + " · " + ex.Message);
            return;
        }

        string relative = "plugins/" + name;
        if (!OfflineChange(name, config =>
        {
            if (Entry(config, relative) is { } entry) entry.Enabled = true;
            else config.Plugins.Add(new PluginConfig { AssemblyPath = relative, Enabled = true });
        }) && !existed)
        {
            try { File.Delete(target); } catch (Exception) { }
        }
    }

    /// <summary>Reads the configuration, applies one change, validates and writes it back. The
    /// service is stopped, so there is no in-memory copy to race with; validation is the same one
    /// the host applies, and a refused change never reaches the file.</summary>
    private bool OfflineChange(string applied, Action<AppConfig> change)
    {
        if (string.IsNullOrEmpty(_configPath) || !File.Exists(_configPath))
        {
            Feedback(T("plugins.notice.noSource"));
            return false;
        }
        try
        {
            var config = AppConfig.Load(_configPath) ?? throw new IOException("the configuration could not be read");
            change(config);
            config.Validate();
            AppConfig.Save(_configPath, config);
            LoadOffline();
            _status.SetText(I18n.F("plugins.offlineApplied", applied));
            SetStatusStyle(false);
            UpdateBusy();
            Fill();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            Feedback(T("plugins.needAdmin"));
        }
        catch (Exception ex)
        {
            Feedback(T("plugins.failed") + " · " + ex.Message);
        }
        Fill();
        return false;
    }

    private void OpenFolder()
    {
        string? directory = _catalog?.PluginDirectory ?? _pluginsDirectory;
        if (string.IsNullOrEmpty(directory)) return;
        try { Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }); }
        catch (Exception ex) { Feedback(T("plugins.openFolderFailed") + " " + ex.Message); }
    }

    private void Feedback(string message)
    {
        _status.SetText(message);
        SetStatusStyle(true);
    }

    private bool Confirm(string title, string message) => QMessageBox.Question(this, title, message,
        T("confirm"), T("cancel"), null, 1, 1) == 0;

    // ---- smoke ----------------------------------------------------------------------------

    /// <summary>
    /// The rule that keeps a plugin from being switched on when this build cannot run it: a row
    /// built from a fixture, because no machine is required to have an incompatible plugin for
    /// the rule to be checked. Two directions, and they are not symmetric — off cannot become on,
    /// while on can become off, or a service that refuses to start would have no way back from
    /// this window.
    /// </summary>
    private void VerifyIncompatibleRow()
    {
        PluginView Foreign(bool enabled) => new()
        {
            AssemblyPath = "PolyMount.Plugin.Foreign.dll",
            FileName = "PolyMount.Plugin.Foreign.dll",
            FullPath = Path.Combine(Path.GetTempPath(), "PolyMount.Plugin.Foreign.dll"),
            Enabled = enabled,
            Present = true,
            ApiVersion = (PluginApi.Major - 1) + ".0"
        };

        var off = Foreign(false);
        if (!IsIncompatible(off))
            throw new InvalidOperationException("a plugin built against another contract was not recognised as incompatible");
        if (StateKey(off) != "plugins.state.incompatible")
            throw new InvalidOperationException("an incompatible plugin is labelled '" + StateKey(off) + "'");

        var offRow = InstalledRow(off, canEdit: true);
        try
        {
            var switchOff = offRow.FindChild<QCheckBox>();
            if (switchOff == null)
                throw new InvalidOperationException("the plugin row has no switch to check");
            if (switchOff.IsEnabled())
                throw new InvalidOperationException("an incompatible plugin can be switched on");
            if (!switchOff.ToolTip.Contains(PluginApi.Version, StringComparison.Ordinal))
                throw new InvalidOperationException("the switch does not say which contract this build provides: " + switchOff.ToolTip);
        }
        finally { offRow.DeleteLater(); }

        var on = Foreign(true);
        var onRow = InstalledRow(on, canEdit: true);
        try
        {
            // Neither direction is a state to switch between: an incompatible plugin can be
            // neither enabled nor disabled here, and the row says why in words rather than only
            // on hover. Removing it — the button beside the switch — is what clears the entry.
            var switchOn = onRow.FindChild<QCheckBox>();
            if (switchOn is not { } onCheck || onCheck.IsEnabled())
                throw new InvalidOperationException("an incompatible plugin can be switched in this window");
            if (!Reads(onRow, I18n.F("plugins.tooltip.incompatible", on.ApiVersion, PluginApi.Version)))
                throw new InvalidOperationException("the row does not say why the plugin is unavailable");
        }
        finally { onRow.DeleteLater(); }
    }

    /// <summary>Whether any label in the row reads exactly this. The reason has to be visible, so
    /// the check is against what the row draws and not against the tooltip alone.</summary>
    private static bool Reads(QWidget row, string text) =>
        row.FindChildren<QLabel>().Any(label => string.Equals(label.Text, text, StringComparison.Ordinal));

    /// <summary>What a smoke run checks: the section read a list, drew a row per configured
    /// plugin, and matches the state it is in — read-only with the service running, editable when
    /// it is stopped. The contents are the service's or the configuration's; what is checked here
    /// is that the section can show them and that the controls say the same thing the notice
    /// does.</summary>
    internal void SmokeVerify()
    {
        VerifyIncompatibleRow();
        if (_catalog == null)
            throw new InvalidOperationException("the plugin section has neither a service nor a configuration to read");
        if (_catalog.Plugins.Count == 0)
            throw new InvalidOperationException("the plugin section lists no configured plugins");
        if (_installedWidgets.Count == 0)
            throw new InvalidOperationException("the plugin section drew no rows");
        if (string.IsNullOrWhiteSpace(_notice.Text))
            throw new InvalidOperationException("the state notice is missing");
        if (_offline)
        {
            if (!_import.IsEnabled())
                throw new InvalidOperationException("the import button is disabled while the service is stopped");
            return;
        }
        if (!_catalog.Plugins.Any(plugin => plugin.Loaded))
            throw new InvalidOperationException("no configured plugin reports as loaded");
        if (_import.IsEnabled())
            throw new InvalidOperationException("the import button is enabled while the service is running");
        VerifyUnfold();
    }

    /// <summary>Unfolds one row and waits for the page inside it to arrive. Shared by the smoke run
    /// and the screenshot, because both need the same thing: the row opened, and the page a reader
    /// would see actually filled in.</summary>
    private bool UnfoldAndWait(PluginView plugin)
    {
        if (!_unfolders.TryGetValue(RowKey(plugin), out var unfold)) return false;
        unfold(true);
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            QCoreApplication.ProcessEvents();
            if (_rowPages.TryGetValue(RowKey(plugin), out var pages) && pages.Count > 0
                && pages[0].View != null && !pages[0].Busy)
                return true;
            System.Threading.Thread.Sleep(25);
        }
        return _rowPages.TryGetValue(RowKey(plugin), out var loaded) && loaded.Count > 0
            && loaded[0].View != null && !loaded[0].Busy;
    }

    /// <summary>Puts a row back the way the reader left it after a check or a picture has disturbed
    /// it: neither may decide for somebody which rows are open.</summary>
    private void Restore(PluginView plugin, bool wasOpen)
    {
        if (_unfolders.TryGetValue(RowKey(plugin), out var unfold)) unfold(wasOpen);
        UiSettings.Set(ExpandedKey(plugin), wasOpen ? "1" : "0");
    }

    /// <summary>The plugin section, photographed with a plugin's page unfolded: the picture of this
    /// page that shows what it is for. Only when a plugin offers a page — otherwise it is a picture
    /// of a list.</summary>
    internal void Capture(string path)
    {
        var plugin = _catalog?.Plugins.FirstOrDefault(entry => entry.Loaded && entry.Panels.Count > 0);
        bool wasOpen = plugin != null && IsExpanded(plugin);
        try
        {
            if (plugin != null) UnfoldAndWait(plugin);
            Layout()?.Activate();
            QCoreApplication.ProcessEvents();
            using var image = Grab();
            image.Save(path);
        }
        finally
        {
            if (plugin != null) Restore(plugin, wasOpen);
        }
    }

    /// <summary>A row unfolds into the plugin's own details and the page it offers. Checked against
    /// whichever installed plugin offers one — a machine where none does has nothing to check — and
    /// put back the way the reader left it, because this run must not decide for them which rows are
    /// open.</summary>
    private void VerifyUnfold()
    {
        var plugin = _catalog?.Plugins.FirstOrDefault(entry => entry.Loaded && entry.Panels.Count > 0);
        if (plugin == null) return;
        if (!_unfolders.ContainsKey(RowKey(plugin)))
            throw new InvalidOperationException("a plugin that offers a page has no row to unfold");
        bool wasOpen = IsExpanded(plugin);
        try
        {
            if (_rowPages.TryGetValue(RowKey(plugin), out var drawn) && drawn.Count > 0 && !drawn[0].IsVisible())
                throw new InvalidOperationException("the unfolded page is not visible");
            if (!UnfoldAndWait(plugin))
                throw new InvalidOperationException(
                    $"{DisplayName(plugin)} offers {plugin.Panels.Count} page(s) and no page came back: " +
                    (_rowPages.TryGetValue(RowKey(plugin), out var pages) && pages.Count > 0
                        ? pages[0].LastError ?? "no reason reported"
                        : "unfolding the row drew none"));
        }
        finally { Restore(plugin, wasOpen); }
    }

    // ---- small builders -----------------------------------------------------------------

    private static QWidget Panel(string name)
    {
        var widget = new QWidget();
        widget.SetObjectName(name);
        widget.SetAttribute(Qt.WidgetAttribute.WA_StyledBackground);
        return widget;
    }

    private static QLabel Label(string key, string name)
    {
        var label = PlainLabel("", name);
        I18n.Bind(label.SetText, key);
        return label;
    }

    private static QLabel PlainLabel(string text, string name)
    {
        var label = new QLabel(text);
        label.SetObjectName(name);
        label.SetWordWrap(true);
        label.SetTextFormat(Qt.TextFormat.PlainText);
        return label;
    }

    private static QPushButton IconButton(string icon, string tooltipKey, Action action)
    {
        var button = new QPushButton();
        button.SetObjectName("iconButton");
        button.SetIcon(TrayIcons.Get(icon, TrayTheme.Tokens.Icon));
        I18n.Bind(button.SetToolTip, tooltipKey);
        button.SetAccessibleName(I18n.T(tooltipKey));
        button.SetCursor(new QCursor(Qt.CursorShape.PointingHandCursor));
        button.Clicked += _ => action();
        return button;
    }

    private static QPushButton TextButton(string key, Action action, string? icon, bool primary = false)
    {
        var button = new QPushButton();
        I18n.Bind(button.SetText, key);
        button.SetObjectName(primary ? "primary" : "");
        button.SetCursor(new QCursor(Qt.CursorShape.PointingHandCursor));
        if (icon != null) button.SetIcon(TrayIcons.Get(icon, primary ? TrayTheme.Tokens.OnPrimary : TrayTheme.Tokens.Icon));
        button.Clicked += _ => action();
        return button;
    }

    private static void SetText(QLabel label, string text)
    {
        if (label.Text != text) label.SetText(text);
    }
}
