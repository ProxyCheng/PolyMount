using PolyMount;
using PolyMount.Core;
using PolyMount.Core.Ipc;
using QtSharp.Api;

namespace PolyMount.Tray;

/// <summary>Create/edit mount instances, source settings and layer colors; save -> validate -> reload.</summary>
internal sealed class ConfigEditor
{
    private readonly QWidget _window;
    private readonly QListWidget _list;
    private readonly QLineEdit _name, _mountPoint, _storeRoot, _cacheMb;
    private readonly QCheckBox _enabled;
    private readonly QComboBox _sourceType;
    private readonly QLineEdit _url, _pinned, _trust, _user, _path;
    private readonly QCheckBox _writable;
    private readonly QLineEdit _overlayColor, _cacheColor, _svnColor, _dirColor;
    private readonly QLabel _status;

    private AppConfig _config = new();
    private int _current = -1;
    private bool _loading;

    public ConfigEditor()
    {
        _window = new QWidget();
        _window.SetWindowTitle("PolyMount Config Editor");
        _window.Resize(860, 620);

        var root = new QHBoxLayout(_window);

        // left: mount list + add/remove
        var left = new QVBoxLayout();
        left.AddWidget(new QLabel("Mount instances"));
        _list = new QListWidget();
        _list.CurrentRowChanged += _ => OnSelected();
        left.AddWidget(_list);
        var btns = new QHBoxLayout();
        var add = new QPushButton("+ New"); add.Clicked += _ => OnAdd(); btns.AddWidget(add);
        var rem = new QPushButton("Remove"); rem.Clicked += _ => OnRemove(); btns.AddWidget(rem);
        left.AddLayout(btns);
        root.AddLayout(left);

        // right: form
        var form = new QFormLayout();
        _name = new QLineEdit(); form.AddRow("Name", _name);
        _mountPoint = new QLineEdit(); form.AddRow("Mount point", _mountPoint);
        _storeRoot = new QLineEdit(); form.AddRow("StoreRoot", _storeRoot);
        _cacheMb = new QLineEdit(); form.AddRow("Cache limit (MB)", _cacheMb);
        _enabled = new QCheckBox(); form.AddRow("Enabled", _enabled);

        _sourceType = new QComboBox(); form.AddRow("Source type", _sourceType);
        _url = new QLineEdit(); form.AddRow("URL (svn)", _url);
        _pinned = new QLineEdit(); form.AddRow("Pinned rev (svn)", _pinned);
        _trust = new QLineEdit(); form.AddRow("Trust failures (svn)", _trust);
        _user = new QLineEdit(); form.AddRow("Username (svn)", _user);
        _path = new QLineEdit(); form.AddRow("Path (dir)", _path);
        _writable = new QCheckBox(); form.AddRow("Writable passthrough (dir)", _writable);

        _overlayColor = new QLineEdit(); form.AddRow("overlay color", _overlayColor);
        _cacheColor = new QLineEdit(); form.AddRow("cache color", _cacheColor);
        _svnColor = new QLineEdit(); form.AddRow("svn color", _svnColor);
        _dirColor = new QLineEdit(); form.AddRow("dir color", _dirColor);

        var save = new QPushButton("Save and reload"); save.Clicked += _ => OnSave();
        form.AddRow("", save);
        _status = new QLabel(); form.AddRow("", _status);

        root.AddLayout(form);

        Load();
    }

    public void ShowWindow()
    {
        _window.Show();
        _window.Raise();
        _window.ActivateWindow();
    }

    private void Load()
    {
        try
        {
            var resp = ControlClient.GetConfig();
            if (!resp.Ok || string.IsNullOrWhiteSpace(resp.Config))
            {
                _status.SetText("could not read config: " + (resp.Error ?? "empty"));
                return;
            }
            _config = AppConfig.FromJson(resp.Config);

            _sourceType.Clear();
            var types = resp.SourceTypes.Count > 0 ? resp.SourceTypes : new List<string> { "svn", "dir" };
            foreach (var t in types) _sourceType.AddItem(t);

            _overlayColor.SetText(ColorOf("overlay"));
            _cacheColor.SetText(ColorOf("cache"));
            _svnColor.SetText(ColorOf("svn"));
            _dirColor.SetText(ColorOf("dir"));

            RebuildList();
            _status.SetText($"loaded {_config.Mounts.Count} mount(s)");
        }
        catch (Exception ex) { _status.SetText("load failed: " + ex.Message); }
    }

    private string ColorOf(string key) => _config.LayerColors.TryGetValue(key, out var v) ? v : "";

    private void RebuildList()
    {
        _list.Clear();
        foreach (var m in _config.Mounts) _list.AddItem(m.Name + (m.Enabled ? "" : " (disabled)"));
        if (_config.Mounts.Count > 0) _list.SetCurrentRow(Math.Min(Math.Max(_current, 0), _config.Mounts.Count - 1));
    }

    private void OnSelected()
    {
        CommitCurrent();
        _current = _list.CurrentRow;
        if (_current < 0 || _current >= _config.Mounts.Count) return;
        LoadFields(_config.Mounts[_current]);
    }

    private void LoadFields(MountProfile m)
    {
        _loading = true;
        _name.SetText(m.Name);
        _mountPoint.SetText(m.MountPoint);
        _storeRoot.SetText(m.StoreRoot);
        _cacheMb.SetText((m.CacheCapacityBytes / (1024 * 1024)).ToString());
        _enabled.SetChecked(m.Enabled);
        SelectSourceType(m.Source.Type);
        _url.SetText(m.Source.Url);
        _pinned.SetText(m.Source.PinnedRevision.ToString());
        _trust.SetText(m.Source.TrustServerCertFailures);
        _user.SetText(m.Source.Username ?? "");
        _path.SetText(m.Source.Path);
        _writable.SetChecked(m.Source.Writable);
        _loading = false;
    }

    private void SelectSourceType(string type)
    {
        for (int i = 0; i < _sourceType.Count; i++)
            if (string.Equals(_sourceType.ItemText(i), type, StringComparison.OrdinalIgnoreCase)) { _sourceType.SetCurrentIndex(i); return; }
    }

    private void CommitCurrent()
    {
        if (_loading || _current < 0 || _current >= _config.Mounts.Count) return;
        var m = _config.Mounts[_current];
        m.Name = _name.Text.Trim();
        m.MountPoint = _mountPoint.Text.Trim();
        m.StoreRoot = _storeRoot.Text.Trim();
        if (long.TryParse(_cacheMb.Text.Trim(), out var mb) && mb >= 0) m.CacheCapacityBytes = mb * 1024 * 1024;
        m.Enabled = _enabled.IsChecked();
        m.Source.Type = _sourceType.CurrentText;
        m.Source.Url = _url.Text.Trim();
        if (long.TryParse(_pinned.Text.Trim(), out var pin)) m.Source.PinnedRevision = pin;
        m.Source.TrustServerCertFailures = _trust.Text.Trim();
        m.Source.Username = string.IsNullOrWhiteSpace(_user.Text) ? null : _user.Text.Trim();
        m.Source.Path = _path.Text.Trim();
        m.Source.Writable = _writable.IsChecked();
    }

    private void OnAdd()
    {
        CommitCurrent();
        _config.Mounts.Add(new MountProfile
        {
            Name = "Mount" + (_config.Mounts.Count + 1),
            Enabled = true,
            Source = new SourceConfig { Type = _sourceType.Count > 0 ? _sourceType.ItemText(0) : "svn" }
        });
        _current = _config.Mounts.Count - 1;
        RebuildList();
        _list.SetCurrentRow(_current);
    }

    private void OnRemove()
    {
        if (_current < 0 || _current >= _config.Mounts.Count) return;
        _config.Mounts.RemoveAt(_current);
        _current = -1;
        RebuildList();
    }

    private void OnSave()
    {
        CommitCurrent();
        _config.LayerColors.Clear();
        AddColor("overlay", _overlayColor.Text);
        AddColor("cache", _cacheColor.Text);
        AddColor("svn", _svnColor.Text);
        AddColor("dir", _dirColor.Text);

        try { _config.Validate(); }
        catch (Exception ex) { _status.SetText("validation failed: " + ex.Message); return; }

        try
        {
            var resp = ControlClient.SaveConfig(_config.ToJson());
            _status.SetText(resp.Ok ? "saved and reloaded." : "save failed: " + resp.Error);
        }
        catch (Exception ex) { _status.SetText("service unreachable: " + ex.Message); }
    }

    private void AddColor(string key, string text)
    {
        if (LayerColors.TryNormalizeHex(text, out var norm)) _config.LayerColors[key] = norm;
    }
}
