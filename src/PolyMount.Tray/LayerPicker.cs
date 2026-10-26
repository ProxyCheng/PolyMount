using PolyMount.Core;
using QtSharp.QtCore;
using QtSharp.QtGui;
using QtSharp.QtWidgets;
using Qt = QtSharp.QtCore.Qt;

namespace PolyMount.Tray;

/// <summary>Browse provider metadata before adding a layer to the draft chain.</summary>
internal sealed class LayerPicker : QDialog
{
    private readonly List<LayerDescriptor> _descriptors;
    private readonly List<(LayerDescriptor Descriptor, QListWidgetItem Item, QWidget Card, QLabel Title, QLabel Description)> _cards = new();
    private readonly QListWidget _list;
    private readonly QLineEdit _search;
    private readonly QLabel _count, _empty, _title, _type, _description, _fieldsTitle, _fields, _selection;
    private readonly QPushButton _add;
    private static string T(string key) => I18n.T(key);
    internal LayerDescriptor? Selected { get; private set; }

    public LayerPicker(QWidget parent, IEnumerable<LayerDescriptor> descriptors, string insertion) : base(parent)
    {
        _descriptors = descriptors.ToList();
        SetObjectName("layerPicker");
        SetWindowTitle(T("picker.title"));
        SetWindowIcon(TrayIcons.App);
        
        Resize(840, 650);
        SetMinimumSize(720, 540);
        var root = new QVBoxLayout(this);
        root.SetContentsMargins(24, 22, 24, 20);
        root.SetSpacing(16);
        root.AddWidget(BoundLabel("picker.title", "detailTitle"));
        root.AddWidget(BoundLabel("picker.subtitle", "muted"));
        _search = new QLineEdit();
        _search.SetClearButtonEnabled(true);
        _search.SetPlaceholderText(T("picker.search"));
        _search.TextChanged += _ => Filter();
        root.AddWidget(_search);

        var body = new QHBoxLayout();
        body.SetSpacing(18);
        var browser = new QWidget();
        var browseLayout = new QVBoxLayout(browser);
        browseLayout.SetContentsMargins(0, 0, 0, 0);
        browseLayout.SetSpacing(8);
        _count = Label("", "eyebrow");
        browseLayout.AddWidget(_count);
        _list = ContinuousScrolling.List();
        _list.SetObjectName("layerCatalog");
        _list.SetSpacing(6);
        _list.SetSelectionMode(QAbstractItemView.SelectionMode.SingleSelection);
        _list.SetHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff);
        _list.CurrentRowChanged += _ => SelectCurrent();
        _list.ItemActivated += _ => ConfirmSelection();
        browseLayout.AddWidget(_list, 1);
        _empty = BoundLabel("picker.empty", "emptyState");
        browseLayout.AddWidget(_empty);
        body.AddWidget(browser, 3);

        var preview = new QWidget();
        preview.SetObjectName("pickerPreview");
        preview.SetMinimumWidth(265);
        var previewLayout = new QVBoxLayout(preview);
        previewLayout.SetContentsMargins(20, 18, 20, 18);
        previewLayout.SetSpacing(12);
        previewLayout.AddWidget(BoundLabel("picker.preview", "eyebrow"));
        _title = Label("", "sectionTitle");
        previewLayout.AddWidget(_title);
        _type = Label("", "pickerType");
        previewLayout.AddWidget(_type);
        var scroll = new QScrollArea();
        scroll.SetWidgetResizable(true);
        scroll.SetFrameShape(QFrame.Shape.NoFrame);
        var details = new QWidget();
        var detailLayout = new QVBoxLayout(details);
        detailLayout.SetContentsMargins(0, 0, 4, 0);
        detailLayout.SetSpacing(14);
        _description = Label("", "detailValue");
        detailLayout.AddWidget(_description);
        _fieldsTitle = BoundLabel("picker.fields", "eyebrow");
        detailLayout.AddWidget(_fieldsTitle);
        _fields = Label("", "muted");
        detailLayout.AddWidget(_fields);
        detailLayout.AddStretch(1);
        scroll.SetWidget(details);
        previewLayout.AddWidget(scroll, 1);
        previewLayout.AddWidget(BoundLabel("picker.configure", "muted"));
        body.AddWidget(preview, 2);
        root.AddLayout(body, 1);

        var placement = new QWidget();
        placement.SetObjectName("pickerPlacement");
        var placementLayout = new QVBoxLayout(placement);
        placementLayout.SetContentsMargins(14, 10, 14, 10);
        placementLayout.SetSpacing(4);
        placementLayout.AddWidget(Label(insertion, "detailValue"));
        placementLayout.AddWidget(BoundLabel("picker.draft", "muted"));
        root.AddWidget(placement);
        var footer = new QHBoxLayout();
        _selection = Label("", "muted");
        footer.AddWidget(_selection, 1);
        var cancel = new QPushButton();
        I18n.Bind(cancel.SetText, "cancel");
        cancel.Clicked += _ => Reject();
        footer.AddWidget(cancel);
        _add = new QPushButton();
        I18n.Bind(_add.SetText, "picker.add");
        _add.SetObjectName("primary");
        _add.SetIcon(TrayIcons.Get("plus", TrayTheme.Tokens.OnPrimary));
        _add.SetDefault(true);
        _add.Clicked += _ => ConfirmSelection();
        footer.AddWidget(_add);
        root.AddLayout(footer);

        foreach (var descriptor in _descriptors)
        {
            var item = new QListWidgetItem();
            item.SetSizeHint(new QSize(340, 100));
            item.SetData((int)Qt.ItemDataRole.UserRole, new QVariant(descriptor.Type));
            _list.AddItem(item);
            var card = new QWidget();
            card.SetObjectName("pickerCard");
            card.SetAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents);
            var cardLayout = new QHBoxLayout(card);
            cardLayout.SetContentsMargins(12, 12, 12, 12);
            cardLayout.SetSpacing(12);
            var icon = new QLabel();
            icon.SetPixmap(TrayIcons.Get(descriptor.Icon, LayerColors.Resolve(descriptor.Type, null)).Pixmap(new QSize(28, 28)));
            icon.SetFixedSize(32, 32);
            cardLayout.AddWidget(icon);
            var copy = new QVBoxLayout();
            copy.SetSpacing(4);
            var title = Label(descriptor.Title.Resolve(I18n.Language), "pickerCardTitle");
            copy.AddWidget(title);
            copy.AddWidget(Label(descriptor.Type, "pickerType"));
            var description = Label(descriptor.Description.Resolve(I18n.Language), "muted");
            description.SetMaximumHeight(34);
            description.SetToolTip(descriptor.Description.Resolve(I18n.Language));
            copy.AddWidget(description);
            cardLayout.AddLayout(copy, 1);
            _list.SetItemWidget(item, card);
            _cards.Add((descriptor, item, card, title, description));
        }
        Filter();
        _search.SetFocus();
    }

    private void Filter()
    {
        string query = _search.Text.Trim();
        foreach (var (descriptor, item, _, _, _) in _cards)
        {
            string text = descriptor.Type + " " + string.Join(" ", descriptor.Title.Values.Values) + " " +
                string.Join(" ", descriptor.Description.Values.Values);
            item.SetHidden(!text.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        int count = _cards.Count(c => !c.Item.IsHidden());
        _count.SetText(I18n.F("picker.count", count));
        _empty.SetVisible(count == 0);
        _list.SetVisible(count > 0);
        int current = _list.CurrentRow;
        if (current < 0 || _cards[current].Item.IsHidden())
            _list.SetCurrentRow(_cards.FindIndex(c => !c.Item.IsHidden()));
        SelectCurrent();
    }

    private void SelectCurrent()
    {
        int row = _list.CurrentRow;
        Selected = row >= 0 && !_cards[row].Item.IsHidden() ? _cards[row].Descriptor : null;
        if (Selected != null) _list.ScrollToItem(_cards[row].Item);
        _title.SetText(Selected?.Title.Resolve(I18n.Language) ?? T("picker.select"));
        _type.SetText(Selected?.Type ?? "");
        _description.SetText(Selected?.Description.Resolve(I18n.Language) ?? "");
        _fieldsTitle.SetVisible(Selected != null);
        _fields.SetText(Selected == null ? "" : Selected.Fields.Count == 0 ? T("picker.commonOnly") :
            string.Join("\n", Selected.Fields.Select(f => "•  " + f.Title.Resolve(I18n.Language))));
        _selection.SetText(Selected == null ? T("picker.select") : I18n.F("picker.selected", Selected.Title.Resolve(I18n.Language)));
        _add.SetEnabled(Selected != null);
        foreach (var (descriptor, _, card, _, _) in _cards)
            card.SetStyleSheet(ReferenceEquals(descriptor, Selected)
                ? "QWidget#pickerCard { background: " + TrayTheme.Tokens.PrimarySoft + "; border: 1px solid " + TrayTheme.Tokens.AccentBorder + "; border-radius: 9px; }"
                : "QWidget#pickerCard { background: " + TrayTheme.Tokens.Surface + "; border: 1px solid " + TrayTheme.Tokens.Border + "; border-radius: 9px; }");
    }
    private void ConfirmSelection() { if (Selected != null) Accept(); }

    public override void ChangeEvent(QEvent e)
    {
        base.ChangeEvent(e);
        if (e.type() != QEvent.Type.LanguageChange || _cards == null || _search == null) return;
        SetWindowTitle(T("picker.title"));
        _search.SetPlaceholderText(T("picker.search"));
        foreach (var (descriptor, _, _, title, description) in _cards)
        {
            title.SetText(descriptor.Title.Resolve(I18n.Language));
            description.SetText(descriptor.Description.Resolve(I18n.Language));
            description.SetToolTip(descriptor.Description.Resolve(I18n.Language));
        }
        Filter();
    }

    internal static void Smoke(QWidget parent, IEnumerable<LayerDescriptor> descriptors, string? capture = null)
    {
        var picker = new LayerPicker(parent, descriptors, T("picker.atEnd"));
        picker.Show();
        try
        {
            if (picker._descriptors.Count == 0) throw new InvalidOperationException("Layer picker received no provider metadata");
            picker._search.SetText("no-provider-with-this-name");
            if (picker.Selected != null || picker._add.IsEnabled()) throw new InvalidOperationException("Empty search still allows adding a layer");
            var target = picker._descriptors[^1];
            picker._search.SetText(target.Type);
            if (picker.Selected?.Type != target.Type) throw new InvalidOperationException("Provider search selected the wrong layer");
            picker._search.Clear();
            picker._list.SetCurrentRow(picker._descriptors.Count - 1);
            QCoreApplication.ProcessEvents();
            if (capture != null) { using var image = picker.Grab(); image.Save(capture); }
            picker.ConfirmSelection();
            if (picker.Result() != 1 || picker.Selected?.Type != target.Type) throw new InvalidOperationException("Layer selection was not accepted");
        }
        finally { picker.Hide(); picker.DeleteLater(); }
    }

    private static QLabel Label(string text, string name)
    {
        var label = new QLabel(text);
        label.SetObjectName(name); label.SetWordWrap(true); label.SetTextFormat(Qt.TextFormat.PlainText);
        return label;
    }
    private static QLabel BoundLabel(string key, string name) { var label = Label("", name); I18n.Bind(label.SetText, key); return label; }
}
