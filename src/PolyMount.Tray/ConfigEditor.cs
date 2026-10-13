using PolyMount;
using PolyMount.Core;
using PolyMount.Core.Ipc;
using QtSharp.Api;

namespace PolyMount.Tray;

/// <summary>A draft mount editor with an actual draggable provider chain and type-specific forms.</summary>
internal sealed class ConfigEditor : QWidget
{
    private readonly QWidget _fields, _overlayFields, _cacheFields, _svnFields, _dirFields;
    private readonly QListWidget _mountList, _layerList;
    private readonly QLineEdit _name, _mountPoint, _storage, _cacheStorage, _capacity, _url, _revision, _trust, _user, _password, _dirPath, _color, _layerName;
    private readonly QCheckBox _enabled, _writable, _layerEnabled;
    private readonly QCheckBox _compression, _cacheCompression;
    private readonly QPushButton _colorPreview;
    private readonly QLabel _status, _layerTitle, _layerDescription, _id;
    private readonly QPushButton _save, _removeLayer;
    private readonly QComboBox _presets;
    private readonly QTimer _timer;
    private readonly QLabel _overlayUsage, _cacheUsage;
    private readonly QProgressBar _cacheProgress;
    private Task<ControlResponse>? _usagePending;
    private DateTime _nextUsage;
    private List<ProviderInfo> _runtimeProviders = new();
    private bool _usageOnline;
    private AppConfig _config = new();
    private List<string> _sourceTypes = new();
    private List<LayerPreset> _presetDefinitions = new();
    private int _current = -1;
    private LayerConfig? _layer;
    private bool _loading, _dirty;
    private Task<ControlResponse>? _pending;
    private bool _saving;
    private bool _layerOperation;
    private string? _openName;
    private bool _addAfterLoad;
    private string? _originalName;
    public event Action? Saved;
    private static string T(string key) => I18n.T(key);

    public ConfigEditor(string? mountName = null, bool create = false)
    {
        _openName = mountName;
        _originalName = mountName;
        _addAfterLoad = create;
        SetObjectName("dashboard");
        SetStyleSheet(TrayTheme.StyleSheet);
        SetWindowIcon(TrayIcons.App);
        I18n.Bind(SetWindowTitle, "editor.title");
        Resize(1000, 820);
        var root = new QVBoxLayout(WindowScrollArea.Create(this, 900, 720));
        root.SetContentsMargins(24, 22, 24, 20);
        root.SetSpacing(16);
        var heading = Plain("", "pageTitle");
        I18n.Changed += () => heading.SetText(mountName == null ? T("mount.new") : I18n.F("editor.instance", mountName));
        heading.SetText(mountName == null ? T("mount.new") : I18n.F("editor.instance", mountName));
        root.AddWidget(heading);
        root.AddWidget(Label("editor.subtitle", "muted"));

        var split = new QSplitter();
        split.SetHandleWidth(14);
        split.SetChildrenCollapsible(false);
        // The internal selection model is retained for drafts; there is no all-instances UI.
        _mountList = new QListWidget();
        _mountList.Hide();
        _mountList.CurrentRowChanged += OnMountSelected;

        var middle = new QWidget();
        middle.SetMinimumWidth(360);
        var center = new QVBoxLayout(middle);
        center.SetContentsMargins(8, 0, 16, 0);
        center.SetSpacing(12);
        var mountForm = new QFormLayout();
        _name = Edit(mountForm, "name");
        _mountPoint = Edit(mountForm, "mount.location");
        _enabled = new QCheckBox();
        I18n.Bind(_enabled.SetText, "enabled");
        _enabled.Toggled += _ => Dirty();
        mountForm.AddRow("", _enabled);
        mountForm.SetRowWrapPolicy(QFormLayout.RowWrapPolicy.DontWrapRows);
        center.AddLayout(mountForm);
        center.AddWidget(Label("layer.stack", "sectionTitle"));
        center.AddWidget(Label("layer.hint", "muted"));
        _layerList = new QListWidget();
        _layerList.SetDragDropMode(QAbstractItemView.DragDropMode.InternalMove);
        _layerList.SetDefaultDropAction(Qt.DropAction.MoveAction);
        _layerList.SetSelectionMode(QAbstractItemView.SelectionMode.SingleSelection);
        _layerList.SetSpacing(8);
        _layerList.SetMinimumHeight(285);
        _layerList.SetStyleSheet("QListWidget::item { background: #f5f7fb; border: 1px solid #e2e8f0; padding: 15px 12px; } QListWidget::item:selected { background: #eaf1ff; border-color: #7ba3e5; color: #1e40af; }");
        _layerList.CurrentRowChanged += _ => OnLayerSelected();
        _layerList.Model().RowsMoved += (_, _, _, _, _) => ReorderLayers();
        center.AddWidget(_layerList, 1);
        var layerButtons = new QHBoxLayout();
        layerButtons.AddWidget(Button("layer.add", ChooseLayer, "plus"));
        _removeLayer = Button("layer.remove", RemoveLayer, "trash");
        layerButtons.AddWidget(_removeLayer);
        center.AddLayout(layerButtons);
        var presetRow = new QHBoxLayout();
        _presets = new QComboBox();
        presetRow.AddWidget(_presets, 1);
        presetRow.AddWidget(Button("preset", ApplyPreset, "layers"));
        center.AddLayout(presetRow);
        center.AddWidget(Label("layer.required", "muted"));
        split.AddWidget(middle);

        var scroll = new QScrollArea();
        scroll.SetMinimumWidth(340);
        scroll.SetWidgetResizable(true);
        scroll.SetFrameShape(QFrame.Shape.NoFrame);
        _fields = new QWidget();
        var right = new QVBoxLayout(_fields);
        right.SetContentsMargins(18, 18, 18, 18);
        right.SetSpacing(12);
        right.AddWidget(Label("layer.parameters", "eyebrow"));
        _layerTitle = Plain("", "detailTitle");
        right.AddWidget(_layerTitle);
        _layerDescription = Plain("", "muted");
        right.AddWidget(_layerDescription);
        var common = new QFormLayout();
        _id = Plain("", "detailValue");
        Row(common, "layer.id", _id);
        _layerName = Edit(common, "layer.name");
        _layerName.TextChanged += _ => UpdateLayerIdentity();
        _layerEnabled = new QCheckBox();
        I18n.Bind(_layerEnabled.SetText, "layer.enabled");
        _layerEnabled.Toggled += _ => UpdateLayerIdentity();
        common.AddRow("", _layerEnabled);
        var colorPanel = new QWidget();
        var colorLayout = new QVBoxLayout(colorPanel);
        colorLayout.SetContentsMargins(0, 0, 0, 0);
        _colorPreview = Button("color.choose", ChooseColor);
        colorLayout.AddWidget(_colorPreview);
        var palette = new QHBoxLayout();
        foreach (string color in LayerColors.OriginPalette)
        {
            var swatch = new QPushButton();
            swatch.SetFixedSize(24, 24);
            swatch.SetToolTip(color);
            swatch.SetStyleSheet($"background: {color}; border-radius: 12px; border: 2px solid white;");
            swatch.Clicked += _ => SetColor(color);
            palette.AddWidget(swatch);
        }
        colorLayout.AddLayout(palette);
        _color = new QLineEdit();
        _color.SetPlaceholderText("#RRGGBB");
        _color.TextChanged += _ => UpdateColor();
        colorLayout.AddWidget(_color);
        Row(common, "layer.color", colorPanel);
        right.AddLayout(common);

        _overlayFields = new QWidget();
        var overlayForm = new QFormLayout(_overlayFields);
        _overlayUsage = Plain("", "layerChain");
        overlayForm.AddRow(_overlayUsage);
        _storage = Edit(overlayForm, "layer.storage");
        _compression = new QCheckBox();
        I18n.Bind(_compression.SetText, "layer.compression");
        _compression.Toggled += _ => Dirty();
        overlayForm.AddRow("", _compression);
        overlayForm.AddRow("", Button("overlay.clear", () => LayerOperation(ControlCommands.ClearLocalChanges), "trash"));
        right.AddWidget(_overlayFields);
        _cacheFields = new QWidget();
        var cacheForm = new QFormLayout(_cacheFields);
        _cacheUsage = Plain("", "detailValue");
        cacheForm.AddRow(_cacheUsage);
        _cacheProgress = new QProgressBar();
        _cacheProgress.SetRange(0, 1000);
        _cacheProgress.SetTextVisible(false);
        _cacheProgress.SetFixedHeight(10);
        cacheForm.AddRow(_cacheProgress);
        _cacheStorage = Edit(cacheForm, "layer.storage");
        _cacheCompression = new QCheckBox();
        I18n.Bind(_cacheCompression.SetText, "layer.compression");
        _cacheCompression.Toggled += _ => Dirty();
        cacheForm.AddRow("", _cacheCompression);
        _capacity = Edit(cacheForm, "cache.limit");
        cacheForm.AddRow("", Button("cache.layer.clear", () => LayerOperation(ControlCommands.ClearCache)));
        right.AddWidget(_cacheFields);
        _svnFields = new QWidget();
        var svnForm = new QFormLayout(_svnFields);
        _url = Edit(svnForm, "svn.repository");
        _revision = Edit(svnForm, "svn.pin");
        _trust = Edit(svnForm, "svn.trust");
        _user = Edit(svnForm, "svn.user");
        _password = Edit(svnForm, "svn.password");
        _password.SetEchoMode(QLineEdit.EchoMode.Password);
        svnForm.AddRow("", Button("revision.head", () => LayerOperation(ControlCommands.RefreshHead)));
        right.AddWidget(_svnFields);
        _dirFields = new QWidget();
        var dirForm = new QFormLayout(_dirFields);
        _dirPath = Edit(dirForm, "dir.path");
        _writable = new QCheckBox();
        I18n.Bind(_writable.SetText, "dir.writable");
        _writable.Toggled += _ => Dirty();
        dirForm.AddRow("", _writable);
        right.AddWidget(_dirFields);
        right.AddStretch(1);
        scroll.SetWidget(_fields);
        split.AddWidget(scroll);
        split.SetStretchFactor(0, 2);
        split.SetStretchFactor(1, 2);
        root.AddWidget(split, 1);
        var footer = new QHBoxLayout();
        _status = Plain("", "muted");
        footer.AddWidget(_status, 1);
        footer.AddWidget(Button("mount.delete", OnRemove, "trash"));
        _save = Button("save", OnSave, "check");
        _save.SetObjectName("primary");
        _save.SetIcon(TrayIcons.Get("check", "#ffffff"));
        footer.AddWidget(_save);
        root.AddLayout(footer);
        _timer = new QTimer(this);
        _timer.Timeout += () => { CompleteRequest(); UpdateUsageRequest(); };
        _timer.Start(100);
        Translate();
        ShowLayer();
        Load();
    }

    public void ShowWindow(string? mountName = null, bool create = false)
    {
        bool wasVisible = IsVisible();
        Show();
        Raise();
        ActivateWindow();
        if (_pending != null) { _openName = mountName; _addAfterLoad |= create; return; }
        if (create) OnAdd();
        else if (mountName != null) SelectByName(mountName);
        else if (!_dirty && !wasVisible) Load();
    }

    internal void Capture(string path) { using var image = Grab(); image.Save(path); }

    internal void SmokeInteractions()
    {
        if (_pending != null || Current?.Layers is not { Count: > 0 } layers) return;
        for (int i = 0; i < layers.Count; i++) _layerList.SetCurrentRow(i);
        var before = layers.Select(l => l.Id).ToArray();
        if (layers.Count > 1)
        {
            using var parent = new QModelIndex();
            if (!_layerList.Model().MoveRows(parent, 0, 1, parent, layers.Count))
                throw new InvalidOperationException("Layer model refused a reorder");
            if (Current.Layers![^1].Id != before[0]) throw new InvalidOperationException("Visual layer order differs from configuration");
            _layerList.Model().MoveRows(parent, layers.Count - 1, 1, parent, 0);
        }
        _layerList.SetCurrentRow(0);
        if (_layer != null)
        {
            bool enabled = _layer.Enabled;
            string name = _layer.Name;
            string color = LayerColor(_layer);
            _layerEnabled.SetChecked(!enabled);
            if (_layer.Enabled == enabled) throw new InvalidOperationException("Layer enable state was not updated");
            _layerEnabled.SetChecked(enabled);
            _layerName.SetText("Smoke layer");
            if (_layer.Name != "Smoke layer") throw new InvalidOperationException("Layer name was not updated");
            _layerName.SetText(name);
            _color.SetText("#DC2626");
            if (_layer.Color != "#DC2626") throw new InvalidOperationException("Color preview is not linked to the layer");
            _color.SetText(color);
        }
    }

    private void Load()
    {
        if (_pending != null) return;
        _saving = false;
        _pending = Task.Run(ControlClient.GetConfig);
        _save.SetEnabled(false);
        _status.SetText(T("connecting"));
    }

    private void CompleteRequest()
    {
        if (_pending is not { IsCompleted: true }) return;
        var task = _pending;
        _pending = null;
        _save.SetEnabled(true);
        try
        {
            var response = task.GetAwaiter().GetResult();
            if (!response.Ok) throw new InvalidOperationException(response.Error);
            AcceptUsage(response);
            if (_layerOperation)
            {
                _layerOperation = false;
                _status.SetText(T("operation.done"));
                return;
            }
            if (_saving)
            {
                _originalName = _saveName;
                var failures = response.Mounts.Where(m => !string.IsNullOrWhiteSpace(m.Error)).Select(m => m.Name + ": " + m.Error).ToArray();
                _status.SetText(failures.Length > 0 ? T("saved.errors") + " " + string.Join("; ", failures) : T(_dirty ? "unsaved" : "saved"));
                Saved?.Invoke();
                if (_saveName == null) Hide();
                return;
            }
            if (response.Config == null) throw new InvalidOperationException("Empty configuration");
            _loading = true;
            _config = AppConfig.FromJson(response.Config);
            _sourceTypes = response.SourceTypes;
            _presetDefinitions = response.Presets;
            UpdatePresets();
            foreach (var mount in _config.Mounts) mount.Layers = mount.GetLayers();
            _current = -1;
            _layer = null;
            _dirty = false;
            RebuildMounts();
            _loading = false;
            if (_addAfterLoad) OnAdd();
            else if (_openName != null) SelectByName(_openName);
            else if (_originalName != null) SelectByName(_originalName);
            else if (_config.Mounts.Count > 0) _mountList.SetCurrentRow(0);
            _openName = null;
            _addAfterLoad = false;
            _status.SetText(Current == null ? T("instance.missing") : T(_dirty ? "unsaved" : "refreshed"));
        }
        catch (Exception ex)
        {
            if (_saving) _dirty = true;
            _status.SetText(T(_layerOperation ? "operation.failed" : _saving ? "save.failed" : "load.failed") + ex.Message);
            _layerOperation = false;
        }
    }

    private MountProfile? Current => _current >= 0 && _current < _config.Mounts.Count ? _config.Mounts[_current] : null;
    private void SelectByName(string name)
    {
        int index = _config.Mounts.FindIndex(m => m.Name == name);
        if (index >= 0) _mountList.SetCurrentRow(index);
    }

    private void RebuildMounts()
    {
        _mountList.BlockSignals(true);
        _mountList.Clear();
        foreach (var mount in _config.Mounts) _mountList.AddItem(mount.Name + (mount.Enabled ? "" : " · " + T("state.disabled")));
        _mountList.SetCurrentRow(_current);
        _mountList.BlockSignals(false);
    }

    private void OnMountSelected(int row)
    {
        if (_loading) return;
        try { CommitMount(); }
        catch (ArgumentException ex) { _status.SetText(ex.Message); _mountList.BlockSignals(true); _mountList.SetCurrentRow(_current); _mountList.BlockSignals(false); return; }
        _current = row;
        _layer = null;
        _loading = true;
        var m = Current;
        _name.SetText(m?.Name ?? "");
        _mountPoint.SetText(m?.MountPoint ?? "");
        _enabled.SetChecked(m?.Enabled == true);
        _loading = false;
        RebuildLayers();
        if (m?.Layers?.Count > 0) _layerList.SetCurrentRow(0);
        else ShowLayer();
    }

    private void CommitMount()
    {
        if (Current is not { } m || _loading) return;
        CommitLayer();
        m.StoreRoot = m.GetStoreRoot();
        m.Name = _name.Text.Trim();
        m.MountPoint = _mountPoint.Text.Trim();
        m.Enabled = _enabled.IsChecked();
        // Compatibility fields remain useful to older clients and service error status.
        var source = m.Layers!.FirstOrDefault(l => SourceRegistry.Contains(l.Type));
        if (source != null) m.Source = source.Source;
        m.CacheCapacityBytes = m.Layers!.Where(l => l.Type == "cache").Sum(l => l.CacheCapacityBytes);
    }

    private void RebuildLayers()
    {
        _layerList.BlockSignals(true);
        _layerList.Clear();
        if (Current?.Layers is { } layers)
            foreach (var layer in layers)
            {
                var item = new QListWidgetItem(LayerText(layer));
                item.SetData((int)Qt.ItemDataRole.UserRole, new QVariant(layer.Id));
                item.SetIcon(TrayIcons.Get(layer.Type == "dir" ? "folder" : layer.Type == "svn" ? "drive" : "layers", LayerColor(layer)));
                _layerList.AddItem(item);
            }
        int row = Current?.Layers?.FindIndex(l => l == _layer) ?? -1;
        _layerList.SetCurrentRow(row);
        _layerList.BlockSignals(false);
    }

    private void OnLayerSelected()
    {
        if (_loading) return;
        try { CommitLayer(); }
        catch (ArgumentException ex) { _status.SetText(ex.Message); RebuildLayers(); return; }
        int row = _layerList.CurrentRow;
        string? id = row < 0 ? null : _layerList.Item(row).Data((int)Qt.ItemDataRole.UserRole).ToString();
        _layer = Current?.Layers?.FirstOrDefault(l => l.Id == id);
        ShowLayer();
    }

    private void ShowLayer()
    {
        _loading = true;
        _layerTitle.SetText(_layer == null ? T("layer.select") : T("layer." + _layer.Type));
        _layerDescription.SetText(_layer == null ? "" : T("layer." + _layer.Type + ".hint"));
        _id.SetText(_layer?.Id ?? "—");
        _layerName.SetText(_layer?.Name ?? "");
        _layerEnabled.SetChecked(_layer?.Enabled == true);
        _color.SetText(_layer == null ? "" : LayerColor(_layer));
        _storage.SetText(_layer?.Path ?? "");
        _cacheStorage.SetText(_layer?.Path ?? "");
        _compression.SetChecked(_layer?.Compression == true);
        _cacheCompression.SetChecked(_layer?.Compression == true);
        string defaultStorage = Current == null || _layer == null ? "" : Path.Combine(Current.GetStoreRoot(), _layer.Id);
        _storage.SetPlaceholderText(defaultStorage);
        _cacheStorage.SetPlaceholderText(defaultStorage);
        _storage.SetToolTip(defaultStorage);
        _cacheStorage.SetToolTip(defaultStorage);
        _capacity.SetText(((_layer?.CacheCapacityBytes ?? 0) / 1048576).ToString());
        _url.SetText(_layer?.Source.Url ?? "");
        _revision.SetText((_layer?.Source.PinnedRevision ?? 0).ToString());
        _trust.SetText(_layer?.Source.TrustServerCertFailures ?? "");
        _user.SetText(_layer?.Source.Username ?? "");
        _password.SetText(_layer?.Source.Password ?? "");
        _dirPath.SetText(_layer?.Source.Path ?? "");
        _writable.SetChecked(_layer?.Source.Writable == true);
        _overlayFields.SetVisible(_layer?.Type == "overlay");
        _cacheFields.SetVisible(_layer?.Type == "cache");
        _svnFields.SetVisible(_layer?.Type == "svn");
        _dirFields.SetVisible(_layer?.Type == "dir");
        _color.SetEnabled(_layer != null);
        _colorPreview.SetEnabled(_layer != null);
        _layerName.SetEnabled(_layer != null);
        _layerEnabled.SetEnabled(_layer != null);
        _removeLayer.SetEnabled(_layer != null);
        _loading = false;
        RenderUsage();
    }

    private void CommitLayer()
    {
        if (_layer == null || _loading) return;
        if (!LayerColors.TryNormalizeHex(_color.Text, out var color)) throw new ArgumentException(T("validation.failed") + T("layer.color"));
        _layer.Color = color;
        _layer.Name = _layerName.Text.Trim();
        _layer.Enabled = _layerEnabled.IsChecked();
        if (_layer.Type == "overlay") _layer.Path = _storage.Text.Trim();
        if (_layer.Type == "cache") _layer.Path = _cacheStorage.Text.Trim();
        if (_layer.Type == "overlay") _layer.Compression = _compression.IsChecked();
        if (_layer.Type == "cache") _layer.Compression = _cacheCompression.IsChecked();
        if (_layer.Type == "cache")
        {
            if (!long.TryParse(_capacity.Text, out var mb) || mb <= 0 || mb > long.MaxValue / 1048576)
            {
                if (_layer.Enabled)
                throw new ArgumentException(T("validation.failed") + T("cache.limit"));
            }
            else _layer.CacheCapacityBytes = mb * 1048576;
        }
        if (_layer.Type == "svn")
        {
            if ((!long.TryParse(_revision.Text, out var revision) || revision < 0) && _layer.Enabled) throw new ArgumentException(T("revision.invalid"));
            _layer.Source.Url = _url.Text.Trim();
            _layer.Source.PinnedRevision = revision;
            _layer.Source.TrustServerCertFailures = _trust.Text.Trim();
            _layer.Source.Username = string.IsNullOrWhiteSpace(_user.Text) ? null : _user.Text.Trim();
            _layer.Source.Password = string.IsNullOrEmpty(_password.Text) ? null : _password.Text;
        }
        if (_layer.Type == "dir")
        {
            _layer.Source.Path = _dirPath.Text.Trim();
            _layer.Source.Writable = _writable.IsChecked();
        }
        _layer.Source.Type = _layer.Type;
    }

    private void ReorderLayers()
    {
        if (_loading || Current?.Layers is not { } layers) return;
        var ordered = new List<LayerConfig>();
        for (int i = 0; i < _layerList.Count; i++)
        {
            string id = _layerList.Item(i).Data((int)Qt.ItemDataRole.UserRole).ToString();
            ordered.Add(layers.First(l => l.Id == id));
        }
        Current.Layers = ordered;
        Dirty();
    }

    private void ChooseLayer()
    {
        if (Current == null) return;
        var dialog = new QDialog(this);
        dialog.SetWindowTitle(T("layer.choose"));
        dialog.SetStyleSheet(TrayTheme.StyleSheet);
        dialog.Resize(420, 320);
        var layout = new QVBoxLayout(dialog);
        layout.AddWidget(Label("layer.choose", "sectionTitle"));
        foreach (var type in new[] { "overlay", "cache" }.Concat(_sourceTypes))
        {
            string captured = type;
            var button = new QPushButton(T("layer." + type) + "\n" + T("layer." + type + ".hint"));
            button.SetIcon(TrayIcons.Get(type == "dir" ? "folder" : "layers"));
            button.Clicked += _ => { AddLayer(captured); dialog.Accept(); };
            layout.AddWidget(button);
        }
        var cancel = Button("cancel", dialog.Reject);
        layout.AddWidget(cancel);
        dialog.Exec();
    }

    private void AddLayer(string type)
    {
        if (Current?.Layers is not { } layers) return;
        try { CommitLayer(); }
        catch (ArgumentException ex) { _status.SetText(ex.Message); return; }
        string id = type;
        for (int i = 2; layers.Any(l => l.Id == id); i++) id = type + "-" + i;
        var layer = new LayerConfig { Id = id, Type = type, Source = new SourceConfig { Type = type } };
        int at = _layerList.CurrentRow < 0 ? layers.Count : _layerList.CurrentRow + 1;
        layers.Insert(at, layer);
        _layer = layer;
        RebuildLayers();
        ShowLayer();
        Dirty();
    }

    private void RemoveLayer()
    {
        if (_layer == null || !Confirm("remove.layer.confirm")) return;
        Current!.Layers!.Remove(_layer);
        _layer = null;
        RebuildLayers();
        if (_layerList.Count > 0) _layerList.SetCurrentRow(0);
        else ShowLayer();
        Dirty();
    }

    private void OnAdd()
    {
        if (_pending != null) return;
        try { CommitMount(); }
        catch (ArgumentException ex) { _status.SetText(ex.Message); return; }
        string name = "Mount";
        for (int i = 1; _config.Mounts.Any(m => m.Name == name); i++) name = "Mount" + i;
        var profile = new MountProfile { Name = name, Layers = new List<LayerConfig>() };
        _config.Mounts.Add(profile);
        _current = -1;
        _layer = null;
        RebuildMounts();
        _mountList.SetCurrentRow(_config.Mounts.Count - 1);
        Dirty();
        ShowLayer();
    }

    private void OnRemove()
    {
        if (Current == null || !Confirm("remove.mount.confirm")) return;
        _config.Mounts.RemoveAt(_current);
        _current = -1;
        _layer = null;
        RebuildMounts();
        OnMountSelected(-1);
        Dirty();
    }

    private void ApplyPreset()
    {
        if (Current == null || !Confirm("preset.confirm")) return;
        int index = _presets.CurrentIndex;
        if (index < 0 || index >= _presetDefinitions.Count) return;
        Current.Layers = AppConfig.FromJson(new AppConfig { Mounts = new() { new() { Layers = _presetDefinitions[index].Layers } } }.ToJson()).Mounts[0].Layers;
        _layer = null;
        RebuildLayers();
        _layerList.SetCurrentRow(Current.Layers!.Count - 1);
        Dirty();
    }

    private void OnSave()
    {
        if (_pending != null) return;
        try
        {
            CommitMount();
            var draft = Current == null ? null : AppConfig.FromJson(new AppConfig { Mounts = new() { Current } }.ToJson()).Mounts[0];
            _saving = true;
            string? originalName = _originalName;
            _pending = Task.Run(() =>
            {
                var latest = ControlClient.GetConfig();
                if (!latest.Ok || latest.Config == null) return latest;
                var config = AppConfig.FromJson(latest.Config);
                int index = originalName == null ? -1 : config.Mounts.FindIndex(m => m.Name == originalName);
                if (originalName != null && index < 0) throw new InvalidOperationException(T("instance.missing"));
                if (draft == null) { if (index >= 0) config.Mounts.RemoveAt(index); }
                else if (index >= 0) config.Mounts[index] = draft;
                else config.Mounts.Add(draft);
                return ControlClient.SaveConfig(config.ToJson());
            });
            _saveName = draft?.Name;
            _dirty = false;
            _save.SetEnabled(false);
            _status.SetText(T("working"));
        }
        catch (Exception ex) { _status.SetText(T("validation.failed") + ex.Message); }
    }

    private string LayerColor(LayerConfig layer) => string.IsNullOrEmpty(layer.Color) ? LayerColors.Resolve(layer.Type, _config.LayerColors) : layer.Color;
    private string? _saveName;
    private string LayerText(LayerConfig layer) => $"≡  {(string.IsNullOrEmpty(layer.Name) ? T("layer." + layer.Type) : layer.Name)}{(layer.Enabled ? "" : " · " + T("state.disabled"))}\n     {T("layer." + layer.Type)} · {layer.Id}";
    private void UpdateLayerIdentity()
    {
        if (_loading || _layer == null) return;
        _layer.Name = _layerName.Text.Trim();
        _layer.Enabled = _layerEnabled.IsChecked();
        RefreshLayerItem();
        Dirty();
    }
    private void RefreshLayerItem()
    {
        if (_layer == null) return;
        int index = Current?.Layers?.IndexOf(_layer) ?? -1;
        if (index < 0) return;
        var item = _layerList.Item(index);
        item.SetText(LayerText(_layer));
        item.SetIcon(TrayIcons.Get(_layer.Type == "dir" ? "folder" : "layers", _layer.Enabled ? LayerColor(_layer) : "#94A3B8"));
        item.SetForeground(new QBrush(new QColor(_layer.Enabled ? "#26354B" : "#94A3B8")));
    }
    private void SetColor(string color) => _color.SetText(color);
    private void LayerOperation(string command)
    {
        if (_pending != null || _layer == null || _originalName == null || !_layer.Enabled) return;
        string id = _layer.Id;
        if (_dirty) { _status.SetText(T("layer.operation.saveFirst")); return; }
        if (!Confirm(command == ControlCommands.ClearLocalChanges ? "confirm.overlay.clear" : command == ControlCommands.ClearCache ? "confirm.layer.cache" : "confirm.layer.head")) return;
        var request = new ControlRequest { Cmd = command, Name = _originalName, Layer = id };
        _saving = false;
        _layerOperation = true;
        _pending = Task.Run(() => ControlClient.Send(request));
        _save.SetEnabled(false);
        _status.SetText(T("working"));
    }
    private void UpdateColor()
    {
        if (!LayerColors.TryNormalizeHex(_color.Text, out var color)) return;
        _colorPreview.SetStyleSheet($"background: {color}; color: white; border: none; border-radius: 7px; padding: 10px;");
        if (_loading || _layer == null) return;
        _layer.Color = color;
        RefreshLayerItem();
        Dirty();
    }
    private void ChooseColor()
    {
        using var dialog = new QColorDialog(new QColor(_color.Text), this);
        dialog.SetWindowTitle(T("color.choose"));
        if (dialog.Exec() == 1) _color.SetText(dialog.SelectedColor().Name());
    }
    private bool Confirm(string key) => QMessageBox.Question(this, T("confirm"), T(key), T("confirm"), T("cancel"), null, 1, 1) == 0;
    private void Dirty() { if (!_loading) { _dirty = true; _status.SetText(T("unsaved")); } }
    private void Translate()
    {
        UpdatePresets();
        RebuildMounts();
        RebuildLayers();
        _layerTitle.SetText(_layer == null ? T("layer.select") : T("layer." + _layer.Type));
        _layerDescription.SetText(_layer == null ? "" : T("layer." + _layer.Type + ".hint"));
        _status.SetText(T(_dirty ? "unsaved" : "refreshed"));
        RenderUsage();
    }
    public override void ChangeEvent(QEvent e)
    {
        base.ChangeEvent(e);
        if (e.type() == QEvent.Type.LanguageChange && _presets != null) Translate();
    }
    private void UpdatePresets()
    {
        int index = Math.Max(0, _presets.CurrentIndex);
        _presets.Clear();
        foreach (var preset in _presetDefinitions) _presets.AddItem(preset.Titles.GetValueOrDefault(I18n.Language) ?? preset.Titles.GetValueOrDefault("en") ?? preset.Id);
        _presets.SetCurrentIndex(index);
    }
    private void UpdateUsageRequest()
    {
        if (_usagePending is { IsCompleted: true })
        {
            var pending = _usagePending; _usagePending = null;
            try { AcceptUsage(pending.GetAwaiter().GetResult()); }
            catch (Exception) { _usageOnline = false; RenderUsage(); }
            _nextUsage = DateTime.UtcNow.AddSeconds(2);
        }
        if (IsVisible() && _originalName != null && _pending == null && _usagePending == null && DateTime.UtcNow >= _nextUsage)
            _usagePending = Task.Run(ControlClient.Status);
    }
    private void AcceptUsage(ControlResponse response)
    {
        var mount = response.Mounts.FirstOrDefault(m => m.Name == _originalName);
        _usageOnline = response.Ok && mount != null;
        _runtimeProviders = mount?.Providers ?? new();
        RenderUsage();
    }
    private void RenderUsage()
    {
        var provider = _layer == null ? null : _runtimeProviders.FirstOrDefault(p => p.Name == _layer.Id);
        var usage = _usageOnline ? provider?.StorageUsage : null;
        if (usage == null)
        {
            string text = provider?.StorageError == null ? T(_usageOnline ? "usage.inactive" : "usage.offline") : T("usage.failed") + provider.StorageError;
            SetUsageText(_overlayUsage, text); SetUsageText(_cacheUsage, text);
            _cacheProgress.SetEnabled(false); _cacheProgress.SetValue(0); return;
        }
        SetUsageText(_overlayUsage, I18n.F("usage.overlay", UsageBytes(usage.DiskBytes), usage.DurableObjects, usage.FileObjects, usage.DirectoryObjects));
        int value = usage.CacheCapacity <= 0 ? 0 : (int)Math.Clamp(1000.0 * usage.CacheBytes / usage.CacheCapacity, 0, 1000);
        _cacheProgress.SetEnabled(true);
        if (_cacheProgress.Value != value) _cacheProgress.SetValue(value);
        SetUsageText(_cacheUsage, I18n.F("usage.cache", UsageBytes(usage.CacheBytes), UsageBytes(usage.CacheCapacity), value / 10.0) + "\n" +
            I18n.F("usage.cacheDetails", UsageBytes(usage.DiskBytes), UsageBytes(usage.DurableBytes), usage.DurableObjects));
        _cacheProgress.SetToolTip(T("usage.capacityHint"));
    }
    private static void SetUsageText(QLabel label, string text) { if (label.Text != text) label.SetText(text); }
    private static string UsageBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double amount = bytes; int unit = 0;
        while (amount >= 1024 && unit < units.Length - 1) { amount /= 1024; unit++; }
        return amount.ToString("0.#", I18n.Culture) + " " + units[unit];
    }
    private static QLabel Plain(string text, string name)
    {
        var label = new QLabel(text);
        label.SetObjectName(name);
        label.SetWordWrap(true);
        label.SetTextFormat(Qt.TextFormat.PlainText);
        return label;
    }
    private static QLabel Label(string key, string name) { var l = Plain("", name); I18n.Bind(l.SetText, key); return l; }
    private static void Row(QFormLayout form, string key, QWidget field) => form.AddRow(Label(key, "muted"), field);
    private QLineEdit Edit(QFormLayout form, string key)
    {
        var edit = new QLineEdit();
        edit.TextChanged += _ => Dirty();
        Row(form, key, edit);
        form.SetRowWrapPolicy(QFormLayout.RowWrapPolicy.DontWrapRows);
        form.SetFieldGrowthPolicy(QFormLayout.FieldGrowthPolicy.AllNonFixedFieldsGrow);
        return edit;
    }
    private static QPushButton Button(string key, Action action, string? icon = null)
    {
        var button = new QPushButton();
        I18n.Bind(button.SetText, key);
        if (icon != null) button.SetIcon(TrayIcons.Get(icon));
        button.Clicked += _ => action();
        return button;
    }
}
