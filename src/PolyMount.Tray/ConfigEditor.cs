using PolyMount;
using PolyMount.Core;
using PolyMount.Core.Ipc;
using QtSharp.Api;

namespace PolyMount.Tray;

/// <summary>A draft mount editor with a draggable provider chain and descriptor-driven forms.</summary>
internal sealed class ConfigEditor : QWidget
{
    private readonly QWidget _fields;
    private readonly QVBoxLayout _propertyLayout;
    private QWidget? _propertyPanel;
    private readonly List<(LayerFieldDescriptor Field, QWidget Editor, QLabel Label)> _propertyEditors = new();
    private readonly List<(QPushButton Button, LayerActionDescriptor Action)> _propertyActions = new();
    private readonly QListWidget _mountList, _layerList;
    private readonly QLineEdit _name, _mountPoint, _color, _layerName;
    private readonly QCheckBox _enabled, _layerEnabled;
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
    private List<LayerDescriptor> _layerDescriptors = new();
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
    private long _editVersion, _saveVersion;
    private Action? _closeAfterSave;
    private bool _allowClose;
    private readonly QLabel _heading;
    internal bool IsDirty => _dirty;
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
        Resize(1000, 820);
        var root = new QVBoxLayout(WindowScrollArea.Create(this, 900, 720));
        root.SetContentsMargins(24, 22, 24, 20);
        root.SetSpacing(16);
        _heading = Plain("", "pageTitle");
        UpdateDocumentTitle();
        root.AddWidget(_heading);
        root.AddWidget(Label("editor.subtitle", "muted"));

        var split = new QSplitter();
        split.SetHandleWidth(14);
        split.SetChildrenCollapsible(false);
        // The internal selection model is retained for drafts; there is no all-instances UI.
        _mountList = ContinuousScrolling.List();
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
        _layerList = ContinuousScrolling.List();
        _layerList.SetDragDropMode(QAbstractItemView.DragDropMode.InternalMove);
        _layerList.SetDefaultDropAction(Qt.DropAction.MoveAction);
        _layerList.SetAutoScroll(true);
        _layerList.SetAutoScrollMargin(36);
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
        _presets = ContinuousScrolling.Combo();
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

        _overlayUsage = Plain("", "layerChain");
        right.AddWidget(_overlayUsage);
        _cacheUsage = Plain("", "detailValue");
        right.AddWidget(_cacheUsage);
        _cacheProgress = new QProgressBar();
        _cacheProgress.SetRange(0, 1000);
        _cacheProgress.SetTextVisible(false);
        _cacheProgress.SetFixedHeight(10);
        right.AddWidget(_cacheProgress);
        _propertyLayout = new QVBoxLayout();
        _propertyLayout.SetContentsMargins(0, 0, 0, 0);
        right.AddLayout(_propertyLayout);
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
        SmokePropertyForm();
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
            if (!WindowTitle.EndsWith(" *") || !_heading.Text.EndsWith(" *"))
                throw new InvalidOperationException("Unsaved edits have no document marker");
            bool closed = false;
            RequestClose(() => closed = true, () => 2);
            if (closed) throw new InvalidOperationException("Cancel closed the dirty editor");
            RequestClose(() => closed = true, () => 1);
            if (!closed) throw new InvalidOperationException("Discard did not permit closing the editor");
            _layerName.SetText(name);
            _color.SetText("#DC2626");
            if (_layer.Color != "#DC2626") throw new InvalidOperationException("Color preview is not linked to the layer");
            _color.SetText(color);
        }
    }

    private void SmokePropertyForm()
    {
        var originalLayer = _layer;
        var descriptor = new LayerDescriptor
        {
            Type = "ui-smoke-provider", Title = new("Smoke provider", "Smoke provider"),
            Fields = new()
            {
                new() { Path = "Parameters.Endpoint", Title = new("Endpoint", "Endpoint") },
                new() { Path = "Parameters.Secret", Kind = LayerFieldKind.Secret, Trim = false },
                new() { Path = "Parameters.Count", Kind = LayerFieldKind.Integer, Minimum = 0, DefaultValue = "1" },
                new() { Path = "Parameters.Enabled", Kind = LayerFieldKind.Boolean },
                new() { Path = "Parameters.Mode", Kind = LayerFieldKind.Choice, DefaultValue = "fast",
                    Choices = new() { new() { Value = "fast", Title = new("Fast", "Fast") } } }
            }
        };
        _layerDescriptors.Add(descriptor);
        try
        {
            _layer = new LayerConfig { Id = "smoke", Type = descriptor.Type };
            ShowLayer();
            ((QLineEdit)_propertyEditors[0].Editor).SetText("https://smoke.invalid");
            ((QLineEdit)_propertyEditors[1].Editor).SetText(" secret ");
            ((QCheckBox)_propertyEditors[3].Editor).SetChecked(true);
            TranslateProperties();
            LayerProperties.Apply(_layer, _propertyEditors.Select(e => (e.Field, PropertyValue(e.Field, e.Editor))));
            if (_layer.Parameters["Endpoint"].GetString() != "https://smoke.invalid" ||
                _layer.Parameters["Secret"].GetString() != " secret " || !_layer.Parameters["Enabled"].GetBoolean() ||
                _layer.Parameters["Count"].GetInt64() != 1 || _layer.Parameters["Mode"].GetString() != "fast")
                throw new InvalidOperationException("Descriptor-driven form did not preserve or bind plugin parameters");
        }
        finally
        {
            _layerDescriptors.Remove(descriptor);
            _layer = originalLayer;
            ShowLayer();
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
                _saving = false;
                _dirty = _editVersion != _saveVersion;
                _originalName = _saveName;
                UpdateDocumentTitle();
                var failures = response.Mounts.Where(m => !string.IsNullOrWhiteSpace(m.Error)).Select(m => m.Name + ": " + m.Error).ToArray();
                _status.SetText(failures.Length > 0 ? T("saved.errors") + " " + string.Join("; ", failures) : T(_dirty ? "unsaved" : "saved"));
                Saved?.Invoke();
                var close = _closeAfterSave;
                _closeAfterSave = null;
                if (close != null && !_dirty) { close(); return; }
                if (_saveName == null) Hide();
                return;
            }
            if (response.Config == null) throw new InvalidOperationException("Empty configuration");
            _loading = true;
            _config = AppConfig.FromJson(response.Config);
            _sourceTypes = response.SourceTypes;
            _layerDescriptors = response.LayerDescriptors;
            _presetDefinitions = response.Presets;
            UpdatePresets();
            foreach (var mount in _config.Mounts) mount.Layers = mount.GetLayers();
            _current = -1;
            _layer = null;
            _dirty = false;
            UpdateDocumentTitle();
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
            _saving = false;
            _closeAfterSave = null;
            UpdateDocumentTitle();
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
        var source = m.Layers!.FirstOrDefault(l => _sourceTypes.Contains(l.Type, StringComparer.OrdinalIgnoreCase));
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
                item.SetIcon(TrayIcons.Get(Descriptor(layer.Type).Icon, LayerColor(layer)));
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
        _layerTitle.SetText(_layer == null ? T("layer.select") : Descriptor(_layer.Type).Title.Resolve(I18n.Language));
        _layerDescription.SetText(_layer == null ? "" : Descriptor(_layer.Type).Description.Resolve(I18n.Language));
        _id.SetText(_layer?.Id ?? "—");
        _layerName.SetText(_layer?.Name ?? "");
        _layerEnabled.SetChecked(_layer?.Enabled == true);
        _color.SetText(_layer == null ? "" : LayerColor(_layer));
        BuildProperties();
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
        LayerProperties.Apply(_layer, _propertyEditors.Select(e => (e.Field, PropertyValue(e.Field, e.Editor))));
        _layer.Source.Type = _layer.Type;
    }

    private LayerDescriptor Descriptor(string type) => _layerDescriptors.FirstOrDefault(d => d.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
        ?? new LayerDescriptor { Type = type, Title = new(type, type) };

    private static string PropertyValue(LayerFieldDescriptor field, QWidget editor) => field.Kind switch
    {
        LayerFieldKind.Boolean => ((QCheckBox)editor).IsChecked().ToString(),
        LayerFieldKind.Choice => ((QComboBox)editor).CurrentIndex is var index && index >= 0 && index < field.Choices.Count ? field.Choices[index].Value : "",
        _ => ((QLineEdit)editor).Text
    };

    private void BuildProperties()
    {
        _propertyEditors.Clear();
        _propertyActions.Clear();
        if (_propertyPanel != null)
        {
            _propertyLayout.RemoveWidget(_propertyPanel);
            _propertyPanel.Hide();
            _propertyPanel.DeleteLater();
        }
        _propertyPanel = new QWidget();
        _propertyLayout.AddWidget(_propertyPanel);
        var layout = new QVBoxLayout(_propertyPanel);
        layout.SetContentsMargins(0, 0, 0, 0);
        var form = new QFormLayout();
        form.SetRowWrapPolicy(QFormLayout.RowWrapPolicy.DontWrapRows);
        form.SetFieldGrowthPolicy(QFormLayout.FieldGrowthPolicy.AllNonFixedFieldsGrow);
        layout.AddLayout(form);
        if (_layer == null) return;
        var descriptor = Descriptor(_layer.Type);
        foreach (var field in descriptor.Fields)
        {
            string value = LayerProperties.Read(_layer, field);
            QWidget editor;
            if (field.Kind == LayerFieldKind.Boolean)
            {
                var check = new QCheckBox();
                check.SetChecked(bool.TryParse(value, out bool flag) && flag);
                check.Toggled += _ => Dirty();
                editor = check;
            }
            else if (field.Kind == LayerFieldKind.Choice)
            {
                var combo = ContinuousScrolling.Combo();
                foreach (var choice in field.Choices) combo.AddItem(choice.Title.Resolve(I18n.Language));
                combo.SetCurrentIndex(field.Choices.FindIndex(c => c.Value == value));
                combo.CurrentIndexChanged += _ => Dirty();
                editor = combo;
            }
            else
            {
                var edit = new QLineEdit();
                if (field.Kind == LayerFieldKind.Secret) edit.SetEchoMode(QLineEdit.EchoMode.Password);
                edit.SetText(value);
                edit.SetPlaceholderText(field.Placeholder);
                edit.TextChanged += _ => Dirty();
                editor = edit;
            }
            var label = Plain(field.Title.Resolve(I18n.Language), "muted");
            editor.SetToolTip(field.Description.Resolve(I18n.Language));
            form.AddRow(label, editor);
            _propertyEditors.Add((field, editor, label));
        }
        foreach (var action in descriptor.Actions)
        {
            var button = new QPushButton(action.Title.Resolve(I18n.Language));
            button.Clicked += _ => LayerOperation(action);
            layout.AddWidget(button);
            _propertyActions.Add((button, action));
        }
    }

    private void TranslateProperties()
    {
        foreach (var (field, editor, label) in _propertyEditors)
        {
            label.SetText(field.Title.Resolve(I18n.Language));
            editor.SetToolTip(field.Description.Resolve(I18n.Language));
            if (editor is QComboBox combo)
            {
                int index = combo.CurrentIndex;
                combo.BlockSignals(true);
                combo.Clear();
                foreach (var choice in field.Choices) combo.AddItem(choice.Title.Resolve(I18n.Language));
                combo.SetCurrentIndex(index);
                combo.BlockSignals(false);
            }
        }
        foreach (var (button, action) in _propertyActions) button.SetText(action.Title.Resolve(I18n.Language));
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
        try { CommitLayer(); }
        catch (ArgumentException ex) { _status.SetText(ex.Message); return; }
        string insertion = _layer == null ? T("picker.atEnd") : I18n.F("picker.after", string.IsNullOrEmpty(_layer.Name) ? Descriptor(_layer.Type).Title.Resolve(I18n.Language) : _layer.Name);
        var dialog = new LayerPicker(this, _layerDescriptors, insertion);
        try
        {
            if (dialog.Exec() == 1 && dialog.Selected is { } descriptor) AddLayer(descriptor.Type);
        }
        finally { dialog.DeleteLater(); }
    }

    internal void SmokeLayerPicker(string? capture = null) => LayerPicker.Smoke(this, _layerDescriptors.Count > 0 ? _layerDescriptors : LayerRegistry.Descriptors, capture);

    private void AddLayer(string type)
    {
        if (Current?.Layers is not { } layers) return;
        try { CommitLayer(); }
        catch (ArgumentException ex) { _status.SetText(ex.Message); return; }
        string id = type;
        for (int i = 2; layers.Any(l => l.Id == id); i++) id = type + "-" + i;
        var layer = new LayerConfig { Id = id, Type = type, Source = new SourceConfig { Type = type } };
        LayerProperties.Apply(layer, Descriptor(type).Fields.Where(f => f.DefaultValue != null).Select(f => (f, f.DefaultValue!)));
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
        if (Current == null) return;
        int index = _presets.CurrentIndex;
        if (index < 0 || index >= _presetDefinitions.Count) return;
        try { CommitLayer(); }
        catch (ArgumentException ex) { _status.SetText(ex.Message); return; }
        if (!Confirm("preset.confirm")) return;
        Current.Layers = LayerPresetMerger.Merge(Current.GetLayers(), _presetDefinitions[index]);
        RebuildLayers();
        _layerList.SetCurrentRow(_layer == null ? Current.Layers.Count - 1 : Current.Layers.IndexOf(_layer));
        ShowLayer();
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
            _saveVersion = _editVersion;
            _save.SetEnabled(false);
            _status.SetText(T("working"));
        }
        catch (Exception ex) { _status.SetText(T("validation.failed") + ex.Message); }
    }

    private string LayerColor(LayerConfig layer) => string.IsNullOrEmpty(layer.Color) ? LayerColors.Resolve(layer.Type, _config.LayerColors) : layer.Color;
    private string? _saveName;
    private string LayerText(LayerConfig layer) => $"≡  {(string.IsNullOrEmpty(layer.Name) ? Descriptor(layer.Type).Title.Resolve(I18n.Language) : layer.Name)}{(layer.Enabled ? "" : " · " + T("state.disabled"))}\n     {Descriptor(layer.Type).Title.Resolve(I18n.Language)} · {layer.Id}";
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
        item.SetIcon(TrayIcons.Get(Descriptor(_layer.Type).Icon, _layer.Enabled ? LayerColor(_layer) : "#94A3B8"));
        item.SetForeground(new QBrush(new QColor(_layer.Enabled ? "#26354B" : "#94A3B8")));
    }
    private void SetColor(string color) => _color.SetText(color);
    private void LayerOperation(LayerActionDescriptor action)
    {
        if (_pending != null || _layer == null || _originalName == null || !_layer.Enabled) return;
        string id = _layer.Id;
        if (_dirty) { _status.SetText(T("layer.operation.saveFirst")); return; }
        if (action.Confirmation.Resolve(I18n.Language) is { Length: > 0 } confirmation &&
            QMessageBox.Question(this, T("confirm"), confirmation, T("confirm"), T("cancel"), null, 1, 1) != 0) return;
        var request = new ControlRequest { Cmd = action.Command, Name = _originalName, Layer = id };
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
    private void Dirty()
    {
        if (_loading) return;
        _editVersion++;
        _dirty = true;
        _status.SetText(T("unsaved"));
        UpdateDocumentTitle();
    }
    private void UpdateDocumentTitle()
    {
        string name = Current?.Name ?? _originalName ?? _openName ?? "";
        string title = (name.Length == 0 ? T("mount.new") : I18n.F("editor.instance", name)) + (_dirty ? " *" : "");
        SetWindowTitle(title);
        _heading?.SetText(title);
    }
    internal void RequestClose(Action close, Func<int>? choose = null)
    {
        if (_pending != null) { _status.SetText(T("working")); return; }
        if (!_dirty) { close(); return; }
        int choice = choose?.Invoke() ?? QMessageBox.Question(this, T("editor.close.title"), T("editor.close.message"),
            T("save"), T("editor.discard"), T("cancel"), 0, 2);
        if (choice == 1) { close(); return; }
        if (choice != 0) return;
        OnSave();
        if (_pending != null && _saving) _closeAfterSave = close;
    }
    public override void CloseEvent(QCloseEvent e)
    {
        if (_allowClose) { e.Accept(); return; }
        e.Ignore();
        RequestClose(() => { _allowClose = true; Close(); DeleteLater(); });
    }
    private void Translate()
    {
        UpdateDocumentTitle();
        UpdatePresets();
        RebuildMounts();
        RebuildLayers();
        _layerTitle.SetText(_layer == null ? T("layer.select") : Descriptor(_layer.Type).Title.Resolve(I18n.Language));
        _layerDescription.SetText(_layer == null ? "" : Descriptor(_layer.Type).Description.Resolve(I18n.Language));
        TranslateProperties();
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
        if (provider?.Type == "prediction-cache")
        {
            // This layer never serves a read — it warms the cache below — so its hit count
            // is zero by design. Show what actually moves: the model it learned and the
            // preloads it ran, worded by the core catalog.
            _overlayUsage.SetVisible(false);
            _cacheUsage.SetVisible(true);
            _cacheProgress.SetVisible(false);
            var template = provider.PredictionDetail?.Resolve(I18n.Language);
            SetUsageText(_cacheUsage, template == null ? "" : string.Format(I18n.Culture, template,
                provider.LearnedTransitions, provider.PreloadsStarted, provider.PreloadsCompleted,
                provider.PreloadsStarted == 0 ? 0 : 100 * provider.PreloadHits / provider.PreloadsStarted));
            return;
        }
        var usage = _usageOnline ? provider?.StorageUsage : null;
        _overlayUsage.SetVisible(usage != null && usage.CacheCapacity <= 0);
        _cacheUsage.SetVisible(usage?.CacheCapacity > 0);
        _cacheProgress.SetVisible(usage?.CacheCapacity > 0);
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
        if (provider?.Type == "memory-cache" && provider.MemoryDetail != null)
            SetUsageText(_cacheUsage, I18n.F("usage.cache", UsageBytes(usage.MemoryBytes), UsageBytes(usage.CacheCapacity), value / 10.0) + "\n" +
                string.Format(I18n.Culture, provider.MemoryDetail.Resolve(I18n.Language),
                    provider.BlockCacheHits, provider.BlockCacheMisses, provider.BlockWriteBacks));
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
