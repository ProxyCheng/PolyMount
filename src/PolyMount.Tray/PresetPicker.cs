using PolyMount.Core;
using QtSharp.QtCore;
using QtSharp.QtGui;
using QtSharp.QtWidgets;
using Qt = QtSharp.QtCore.Qt;

namespace PolyMount.Tray;

/// <summary>Chooses the preset a draft chain is rearranged to. It is opened from the preset
/// button — the action first, the choice second — and previews the stack that choice leaves
/// behind, because neither a preset's name nor its layer list says what it does to this instance.</summary>
internal sealed class PresetPicker : QDialog
{
    private readonly List<LayerPreset> _presets;
    private readonly List<LayerConfig> _current;
    private readonly Dictionary<string, LocalizedText> _titles;
    private readonly List<(LayerPreset Preset, QListWidgetItem Item, QWidget Card)> _cards = new();
    private readonly QListWidget _list, _preview;
    private readonly QLabel _previewTitle, _previewSummary;
    private readonly QPushButton _apply;
    private readonly QLabel _selection;
    private static string T(string key) => I18n.T(key);

    internal LayerPreset? Selected { get; private set; }

    public PresetPicker(QWidget parent, IEnumerable<LayerPreset> presets, IEnumerable<LayerConfig> current,
        IEnumerable<LayerDescriptor> descriptors) : base(parent)
    {
        _presets = presets.ToList();
        _current = current.ToList();
        _titles = descriptors.ToDictionary(descriptor => descriptor.Type, descriptor => descriptor.Title,
            StringComparer.OrdinalIgnoreCase);
        SetObjectName("presetPicker");
        SetWindowTitle(T("preset.title"));
        SetWindowIcon(TrayIcons.App);
        Resize(880, 560);
        SetMinimumSize(720, 420);
        var root = new QVBoxLayout(this);
        root.SetContentsMargins(24, 22, 24, 20);
        root.SetSpacing(12);
        var heading = new QLabel(T("preset.title"));
        heading.SetObjectName("detailTitle");
        root.AddWidget(heading);
        var subtitle = new QLabel(T("preset.subtitle"));
        subtitle.SetObjectName("muted");
        subtitle.SetWordWrap(true);
        root.AddWidget(subtitle);

        var body = new QHBoxLayout();
        body.SetSpacing(18);
        _list = ContinuousScrolling.List();
        _list.SetObjectName("layerCatalog");
        _list.SetSpacing(6);
        _list.SetSelectionMode(QAbstractItemView.SelectionMode.SingleSelection);
        _list.SetHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff);
        _list.CurrentRowChanged += _ => SelectCurrent();
        _list.ItemActivated += _ => ConfirmSelection();
        body.AddWidget(_list, 3);

        var preview = new QWidget();
        preview.SetObjectName("pickerPreview");
        preview.SetMinimumWidth(300);
        var previewLayout = new QVBoxLayout(preview);
        previewLayout.SetContentsMargins(20, 18, 20, 18);
        previewLayout.SetSpacing(10);
        _previewTitle = new QLabel(T("preset.preview"));
        _previewTitle.SetObjectName("eyebrow");
        previewLayout.AddWidget(_previewTitle);
        _previewSummary = new QLabel();
        _previewSummary.SetObjectName("muted");
        _previewSummary.SetWordWrap(true);
        previewLayout.AddWidget(_previewSummary);
        _preview = ContinuousScrolling.List();
        _preview.SetObjectName("layerCatalog");
        _preview.SetSelectionMode(QAbstractItemView.SelectionMode.NoSelection);
        _preview.SetHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff);
        previewLayout.AddWidget(_preview, 1);
        body.AddWidget(preview, 2);
        root.AddLayout(body, 1);

        var footer = new QHBoxLayout();
        _selection = new QLabel();
        _selection.SetObjectName("muted");
        footer.AddWidget(_selection, 1);
        var cancel = new QPushButton();
        I18n.Bind(cancel.SetText, "cancel");
        cancel.Clicked += _ => Reject();
        footer.AddWidget(cancel);
        _apply = new QPushButton();
        I18n.Bind(_apply.SetText, "preset.apply");
        _apply.SetObjectName("primary");
        _apply.SetDefault(true);
        _apply.Clicked += _ => ConfirmSelection();
        footer.AddWidget(_apply);
        root.AddLayout(footer);

        foreach (var preset in _presets)
        {
            var item = new QListWidgetItem();
            item.SetData((int)Qt.ItemDataRole.UserRole, new QVariant(preset.Id));
            _list.AddItem(item);
            var card = new QWidget();
            card.SetObjectName("presetCard");
            card.SetAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents);
            var layout = new QVBoxLayout(card);
            layout.SetContentsMargins(12, 10, 12, 10);
            layout.SetSpacing(3);
            var title = new QLabel(preset.Titles.GetValueOrDefault(I18n.Language) ?? preset.Titles.GetValueOrDefault("en") ?? preset.Id);
            title.SetObjectName("pickerCardTitle");
            layout.AddWidget(title);
            // The chain is the answer to "what does this preset do here?" — the order is the
            // priority the stack will have, so it is written in the order the list will show.
            var chain = new QLabel(string.Join(" → ", preset.Layers.Select(layer => Title(layer.Type))));
            chain.SetObjectName("muted");
            chain.SetWordWrap(true);
            chain.SetMaximumHeight(38);
            layout.AddWidget(chain);
            _list.SetItemWidget(item, card);
            // A row with a widget is not sized from that widget; without a hint its height comes
            // from the item's empty text and the card is clipped.
            item.SetSizeHint(new QSize(0, Math.Max(card.SizeHint().Height(), 64)));
            _cards.Add((preset, item, card));
        }
        SelectCurrent();
        if (_cards.Count == 0) _apply.SetEnabled(false);
    }

    private string Title(string type) =>
        _titles.TryGetValue(type, out var text) ? text.Resolve(I18n.Language) : type;

    private void SelectCurrent()
    {
        int row = _list.CurrentRow;
        Selected = row >= 0 && row < _cards.Count ? _cards[row].Preset : null;
        _selection.SetText(Selected == null ? "" : I18n.F("preset.layers", Selected.Layers.Count));
        _apply.SetEnabled(Selected != null);
        for (int i = 0; i < _cards.Count; i++)
            _cards[i].Card.SetStyleSheet(i == row
                ? "QWidget#presetCard { background: " + TrayTheme.Tokens.PrimarySoft + "; border: 1px solid " + TrayTheme.Tokens.AccentBorder + "; border-radius: 9px; }"
                : "QWidget#presetCard { background: " + TrayTheme.Tokens.Surface + "; border: 1px solid " + TrayTheme.Tokens.Border + "; border-radius: 9px; }");
        ShowPreview();
    }

    /// <summary>The chain this preset would leave behind, layer by layer, with the ones it adds
    /// marked. The merge is the one applying runs, so the preview cannot describe a different
    /// arrangement than the button produces — including layers kept beyond the preset's own.</summary>
    private void ShowPreview()
    {
        _preview.Clear();
        if (Selected == null)
        {
            _previewSummary.SetText("");
            return;
        }
        var merged = LayerPresetMerger.Merge(_current, Selected);
        int added = merged.Count(layer => !_current.Contains(layer));
        _previewSummary.SetText(added == 0
            ? I18n.F("preset.preview.same", merged.Count)
            : I18n.F("preset.preview.summary", merged.Count, added));
        foreach (var layer in merged)
        {
            bool isNew = !_current.Contains(layer);
            var item = new QListWidgetItem(
                (isNew ? "+  " : "·  ") + Title(layer.Type) + (layer.Enabled ? "" : " · " + T("state.disabled")));
            item.SetForeground(new QBrush(new QColor(isNew ? TrayTheme.Tokens.Primary : TrayTheme.Tokens.Muted)));
            _preview.AddItem(item);
        }
    }

    private void ConfirmSelection() { if (Selected != null) Accept(); }

    internal static void Smoke(QWidget parent, IEnumerable<LayerPreset> presets, IEnumerable<LayerConfig> current,
        IEnumerable<LayerDescriptor> descriptors, string? capture = null)
    {
        var picker = new PresetPicker(parent, presets, current, descriptors);
        picker.Show();
        try
        {
            if (picker._cards.Count == 0) throw new InvalidOperationException("Preset picker received no presets");
            picker._list.SetCurrentRow(picker._cards.Count - 1);
            QCoreApplication.ProcessEvents();
            if (picker.Selected?.Id != picker._cards[^1].Preset.Id)
                throw new InvalidOperationException("Preset selection did not follow the list");
            if (!picker._apply.IsEnabled()) throw new InvalidOperationException("Selected preset cannot be applied");
            // The preview has to be the arrangement the button would produce, one row per layer.
            var expected = LayerPresetMerger.Merge(current, picker.Selected!);
            if (picker._preview.Count != expected.Count)
                throw new InvalidOperationException(
                    $"Preview shows {picker._preview.Count} layers where applying would leave {expected.Count}");
            if (capture != null) { using var image = picker.Grab(); image.Save(capture); }
            picker.ConfirmSelection();
            if (picker.Result() != 1 || picker.Selected == null)
                throw new InvalidOperationException("Preset selection was not accepted");
        }
        finally { picker.Hide(); picker.DeleteLater(); }
    }
}
