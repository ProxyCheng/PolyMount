using PolyMount.Core;
using PolyMount.Core.Ipc;
using QtSharp.Api;

namespace PolyMount.Tray;

/// <summary>
/// The accounts this product keeps. Named ones are a person's, not a server's — the same account
/// usually opens several hosts — and a layer names the one it uses; the separate group at the
/// bottom is what earlier sign-ins remembered by address.
///
/// The dialog never sees a secret: the list is metadata, a password only travels one way, and an
/// edit that leaves the field empty keeps the stored one.
/// </summary>
internal sealed class CredentialManager : QDialog
{
    private readonly QListWidget _list, _origins;
    private readonly QLineEdit _search;
    private readonly QLabel _empty;
    private readonly QPushButton _edit, _remove, _close;
    private List<CredentialRecord> _items = new();
    private List<OriginRecord> _saved = new();
    private readonly Dictionary<string, int> _usage = new(StringComparer.OrdinalIgnoreCase);

    private static string T(string key) => I18n.T(key);

    public CredentialManager(QWidget? parent) : base(parent)
    {
        SetObjectName("credentialManager");
        SetWindowTitle(T("credential.title"));
        SetWindowIcon(TrayIcons.App);
        Resize(520, 460);
        SetMinimumSize(440, 360);

        var root = new QVBoxLayout(this);
        root.SetContentsMargins(24, 22, 24, 20);
        root.SetSpacing(12);
        var heading = new QLabel();
        I18n.Bind(heading.SetText, "credential.title");
        heading.SetObjectName("detailTitle");
        root.AddWidget(heading);

        _search = new QLineEdit();
        _search.SetClearButtonEnabled(true);
        I18n.Bind(_search.SetPlaceholderText, "credential.search");
        _search.TextChanged += _ => Rebuild();
        root.AddWidget(_search);

        _list = ContinuousScrolling.List();
        _list.SetSelectionMode(QAbstractItemView.SelectionMode.SingleSelection);
        _list.SetHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff);
        _list.CurrentRowChanged += _ => UpdateActions();
        _list.ItemActivated += _ => EditSelected();
        root.AddWidget(_list, 1);
        _empty = new QLabel();
        I18n.Bind(_empty.SetText, "credential.empty");
        _empty.SetWordWrap(true);
        _empty.SetObjectName("emptyState");
        root.AddWidget(_empty);

        var buttons = new QHBoxLayout();
        var add = new QPushButton();
        I18n.Bind(add.SetText, "credential.add");
        add.Clicked += _ => AddNew();
        buttons.AddWidget(add);
        _edit = new QPushButton();
        I18n.Bind(_edit.SetText, "credential.edit");
        _edit.Clicked += _ => EditSelected();
        buttons.AddWidget(_edit);
        _remove = new QPushButton();
        I18n.Bind(_remove.SetText, "credential.remove");
        _remove.Clicked += _ => RemoveSelected();
        buttons.AddWidget(_remove);
        buttons.AddStretch(1);
        _close = new QPushButton();
        I18n.Bind(_close.SetText, "credential.close");
        _close.Clicked += _ => Reject();
        buttons.AddWidget(_close);
        root.AddLayout(buttons);

        // The address-keyed entries are a different thing: not chosen by anybody, remembered by the
        // product. They are shown so they can be seen and forgotten, never edited into a credential.
        var savedTitle = new QLabel();
        I18n.Bind(savedTitle.SetText, "credential.automatic");
        savedTitle.SetObjectName("muted");
        root.AddWidget(savedTitle);
        _origins = ContinuousScrolling.List();
        _origins.SetSelectionMode(QAbstractItemView.SelectionMode.SingleSelection);
        _origins.SetMaximumHeight(96);
        _origins.CurrentRowChanged += _ => UpdateActions();
        root.AddWidget(_origins);
        var forget = new QPushButton();
        I18n.Bind(forget.SetText, "credential.forget");
        forget.Clicked += _ => ForgetSelectedOrigin();
        root.AddWidget(forget);

        LoadAll();
    }

    private void LoadAll()
    {
        try
        {
            var response = SupervisorClient.Default.Credentials();
            if (!response.Ok) throw new InvalidOperationException(response.Error);
            _items = response.Credentials;
            _saved = response.SavedOrigins;
            CountUsage();
            Rebuild();
        }
        catch (Exception ex)
        {
            _empty.SetText(T("credential.offline") + " " + ex.Message);
            _empty.SetVisible(true);
        }
    }

    /// <summary>How many layers name each credential, so deleting one can say what it affects.</summary>
    private void CountUsage()
    {
        _usage.Clear();
        try
        {
            var response = SupervisorClient.Default.GetConfig();
            if (response.Config == null) return;
            foreach (var mount in AppConfig.FromJson(response.Config).Mounts)
                foreach (var layer in mount.GetLayers())
                    if (!string.IsNullOrWhiteSpace(layer.Source.CredentialId))
                        _usage[layer.Source.CredentialId] =
                            _usage.GetValueOrDefault(layer.Source.CredentialId) + 1;
        }
        catch (Exception) { /* the list still works; it just cannot say what uses what */ }
    }

    private void Rebuild()
    {
        // The row is a card, the way a password manager draws an account: the name first, what it
        // signs in as and what uses it underneath. Clicking anywhere selects it.
        string filter = _search.Text.Trim();
        _list.Clear();
        foreach (var item in _items)
        {
            if (filter.Length > 0 &&
                item.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                item.Username.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var row = new QListWidgetItem();
            row.SetData((int)Qt.ItemDataRole.UserRole, new QVariant(item.Id));
            var card = new QWidget();
            card.SetAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents);
            var layout = new QVBoxLayout(card);
            layout.SetContentsMargins(12, 10, 12, 10);
            layout.SetSpacing(2);
            var title = new QLabel(item.Name);
            title.SetObjectName("detailValue");
            layout.AddWidget(title);
            int usage = _usage.GetValueOrDefault(item.Id);
            var detail = new QLabel(item.Username + (usage > 0 ? "  ·  " + I18n.F("credential.used", usage) : ""));
            detail.SetObjectName("muted");
            layout.AddWidget(detail);
            _list.AddItem(row);
            _list.SetItemWidget(row, card);
        }
        bool none = _items.Count == 0;
        _empty.SetText(I18n.T(none ? "credential.empty" : "credential.noMatch"));
        _empty.SetVisible(_list.Count == 0);
        if (_list.Count > 0) _list.SetCurrentRow(0);

        _origins.Clear();
        foreach (var saved in _saved)
        {
            var row = new QListWidgetItem(saved.Origin + " · " + saved.Username);
            row.SetData((int)Qt.ItemDataRole.UserRole, new QVariant(saved.Origin));
            _origins.AddItem(row);
        }
        UpdateActions();
    }

    private void UpdateActions()
    {
        bool hasItem = SelectedId() != null;
        _edit.SetEnabled(hasItem);
        _remove.SetEnabled(hasItem);
    }

    private string? SelectedId() =>
        _list.CurrentItem()?.Data((int)Qt.ItemDataRole.UserRole).ToString();

    private void AddNew() => Edit(null);

    private void EditSelected()
    {
        if (SelectedId() is { } id && _items.FirstOrDefault(item => item.Id == id) is { } item) Edit(item);
    }

    private void Edit(CredentialRecord? existing)
    {
        using var form = new CredentialForm(this, existing);
        if (form.Exec() != 1) return;
        try
        {
            var response = SupervisorClient.Default.SaveCredential(new CredentialPayload
            {
                Id = existing?.Id,
                Name = form.CredentialName,
                Username = form.Username,
                Password = form.Password
            });
            if (!response.Ok) throw new InvalidOperationException(response.Error);
            LoadAll();
        }
        catch (Exception ex)
        {
            QMessageBox.Information(this, T("credential.title"), I18n.F("credential.failed", ex.Message), T("credential.close"));
        }
    }

    private void RemoveSelected()
    {
        if (SelectedId() is not { } id) return;
        int usage = _usage.GetValueOrDefault(id);
        string question = usage > 0
            ? I18n.F("credential.delete.confirm", usage)
            : T("credential.delete.plain");
        if (QMessageBox.Question(this, T("credential.title"), question, T("credential.remove"), T("cancel"), null, 1, 1) != 0) return;
        try { SupervisorClient.Default.DeleteCredential(id); } catch (Exception) { }
        LoadAll();
    }

    private void ForgetSelectedOrigin()
    {
        if (_origins.CurrentItem()?.Data((int)Qt.ItemDataRole.UserRole).ToString() is not { Length: > 0 } origin) return;
        if (QMessageBox.Question(this, T("credential.title"), T("credential.delete.origin"), T("credential.forget"), T("cancel"), null, 1, 1) != 0) return;
        try { SupervisorClient.Default.ForgetOrigin(origin); } catch (Exception) { }
        LoadAll();
    }

    /// <summary>Builds the list with a fixture instead of asking the service, so a self-check can
    /// look at the empty state and the usage note without a running host.</summary>
    internal void ShowForCheck(List<CredentialRecord> items, List<OriginRecord> saved, Dictionary<string, int> usage)
    {
        _items = items;
        _saved = saved;
        _usage.Clear();
        foreach (var (key, value) in usage) _usage[key] = value;
        Rebuild();
    }
}

/// <summary>Name, user, and a password that is only sent when it is typed.</summary>
internal sealed class CredentialForm : QDialog
{
    private readonly QLineEdit _name, _username, _password;
    private readonly QLabel _error;
    private readonly bool _isNew;

    public string CredentialName => _name.Text.Trim();
    public string Username => _username.Text.Trim();
    public string? Password => _password.Text.Length > 0 ? _password.Text : null;

    public CredentialForm(QWidget? parent, CredentialRecord? existing) : base(parent)
    {
        SetWindowTitle(I18n.T(existing == null ? "credential.add.title" : "credential.edit.title"));
        SetWindowIcon(TrayIcons.App);
        SetMinimumWidth(420);

        var root = new QVBoxLayout(this);
        root.SetContentsMargins(24, 22, 24, 20);
        root.SetSpacing(12);
        var form = new QFormLayout();
        form.SetRowWrapPolicy(QFormLayout.RowWrapPolicy.DontWrapRows);
        form.SetFieldGrowthPolicy(QFormLayout.FieldGrowthPolicy.AllNonFixedFieldsGrow);

        _isNew = existing == null;
        _name = new QLineEdit();
        _name.SetText(existing?.Name ?? "");
        _username = new QLineEdit();
        _username.SetText(existing?.Username ?? "");
        _password = new QLineEdit();
        _password.SetEchoMode(QLineEdit.EchoMode.Password);
        _password.SetPlaceholderText(existing == null ? "" : I18n.T("credential.password.keep"));
        QLabel Label(string key) { var label = new QLabel(); I18n.Bind(label.SetText, key); return label; }
        form.AddRow(Label("credential.name"), _name);
        form.AddRow(Label("credential.username"), _username);
        form.AddRow(Label("credential.password"), _password);
        root.AddLayout(form);

        _error = new QLabel("");
        _error.SetWordWrap(true);
        _error.SetObjectName("errorBox");
        _error.SetVisible(false);
        root.AddWidget(_error);

        var footer = new QHBoxLayout();
        footer.AddStretch(1);
        var cancel = new QPushButton();
        I18n.Bind(cancel.SetText, "cancel");
        cancel.Clicked += _ => Reject();
        footer.AddWidget(cancel);
        var save = new QPushButton();
        I18n.Bind(save.SetText, "credential.save");
        save.SetObjectName("primary");
        save.SetDefault(true);
        save.Clicked += _ => Submit();
        footer.AddWidget(save);
        root.AddLayout(footer);
    }

    private void Submit()
    {
        if (CredentialName.Length == 0) { Explain("credential.name.required"); return; }
        if (Username.Length == 0) { Explain("credential.username.required"); return; }
        if (Password == null && _isNew) { Explain("credential.password.required"); return; }
        Accept();
    }

    private void Explain(string key)
    {
        _error.SetText(I18n.T(key));
        _error.SetVisible(true);
    }
}
