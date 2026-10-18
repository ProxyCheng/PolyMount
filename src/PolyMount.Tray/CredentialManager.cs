using PolyMount.Core;
using PolyMount.Core.Ipc;
using QtSharp.Api;

namespace PolyMount.Tray;

/// <summary>
/// The one list of credentials this product keeps. A credential belongs to a person, not to a
/// server — the same account usually opens several hosts — and a layer names the one it uses.
///
/// The dialog never sees a secret: the list is metadata, a password only travels one way, and an
/// edit that leaves the field empty keeps the stored one.
/// </summary>
internal sealed class CredentialManager : QDialog
{
    private readonly QListWidget _list;
    private readonly QLineEdit _search;
    private readonly QLabel _empty;
    private readonly QPushButton _add, _edit, _remove, _close;
    private List<CredentialRecord> _items = new();
    private readonly Dictionary<string, List<string>> _usedBy = new(StringComparer.OrdinalIgnoreCase);

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

        // One sentence, so the list needs no legend: what these are, and that the secret does not
        // come back out once saved.
        var intro = new QLabel();
        I18n.Bind(intro.SetText, "credential.intro");
        intro.SetObjectName("muted");
        intro.SetWordWrap(true);
        root.AddWidget(intro);

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
        _empty.SetAlignment(Qt.Alignment.AlignCenter);
        _empty.SetObjectName("emptyState");
        root.AddWidget(_empty);

        var buttons = new QHBoxLayout();
        _add = new QPushButton();
        I18n.Bind(_add.SetText, "credential.add");
        _add.Clicked += _ => AddNew();
        buttons.AddWidget(_add);
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

        LoadAll();
    }

    private void LoadAll()
    {
        try
        {
            var response = SupervisorClient.Default.Credentials();
            if (!response.Ok) throw new InvalidOperationException(response.Error);
            _items = response.Credentials;
            _add.SetEnabled(true);
            CountUsage();
            Rebuild();
        }
        catch (Exception ex)
        {
            _empty.SetText(T("credential.offline") + " " + ex.Message);
            _empty.SetVisible(true);
            // Everything written here goes through the service, so there is nothing to add while
            // it is not answering; the form would only fail after the reader had typed it all in.
            _add.SetEnabled(false);
        }
    }

    /// <summary>How many layers name each credential, and which — so deleting one can say exactly
    /// what it affects instead of a bare count.</summary>
    private void CountUsage()
    {
        _usedBy.Clear();
        foreach (var (id, names) in CredentialUsage.Of()) _usedBy[id] = names;
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
            var row = new QListWidgetItem(item.Name + " · " + item.Username);
            row.SetData((int)Qt.ItemDataRole.UserRole, new QVariant(item.Id));
            _usedBy.TryGetValue(item.Id, out var usedBy);
            row.SetToolTip(usedBy is { Count: > 0 }
                ? item.Name + " · " + item.Username + Environment.NewLine + string.Join(Environment.NewLine, usedBy)
                : item.Name + " · " + item.Username);
            _list.AddItem(row);
        }
        bool none = _items.Count == 0;
        _empty.SetText(I18n.T(none ? "credential.empty" : "credential.noMatch"));
        _empty.SetVisible(_list.Count == 0);
        _search.SetEnabled(!none);
        if (_list.Count > 0) _list.SetCurrentRow(0);

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
        using var form = new CredentialForm(this, existing, _items);
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
        string question = _usedBy.GetValueOrDefault(id) is { Count: > 0 } usedBy
            ? I18n.F("credential.delete.confirm", string.Join(", ", usedBy))
            : T("credential.delete.plain");
        if (QMessageBox.Question(this, T("credential.title"), question, T("credential.remove"), T("cancel"), null, 1, 1) != 0) return;
        try { SupervisorClient.Default.DeleteCredential(id); } catch (Exception) { }
        LoadAll();
    }

    /// <summary>Builds the list with a fixture instead of asking the service, so a self-check can
    /// look at the empty state and the usage note without a running host.</summary>
    internal void ShowForCheck(List<CredentialRecord> items, Dictionary<string, int> usage)
    {
        _items = items;
        _usedBy.Clear();
        foreach (var (key, value) in usage)
            _usedBy[key] = Enumerable.Range(1, value).Select(n => "instance / layer " + n).ToList();
        Rebuild();
    }
}

/// <summary>Name, user, and a password that is only sent when it is typed.</summary>
internal sealed class CredentialForm : QDialog
{
    private readonly QLineEdit _name, _username, _password;
    private readonly QLabel _error;
    private readonly bool _isNew;
    private readonly List<string> _takenNames;

    public string CredentialName => _name.Text.Trim();
    public string Username => _username.Text.Trim();
    public string? Password => _password.Text.Length > 0 ? _password.Text : null;

    public CredentialForm(QWidget? parent, CredentialRecord? existing, IReadOnlyList<CredentialRecord> all) : base(parent)
    {
        SetWindowTitle(I18n.T(existing == null ? "credential.add.title" : "credential.edit.title"));
        SetWindowIcon(TrayIcons.App);
        SetMinimumWidth(420);
        _takenNames = all.Where(item => item.Id != existing?.Id).Select(item => item.Name).ToList();

        var root = new QVBoxLayout(this);
        root.SetContentsMargins(24, 22, 24, 20);
        root.SetSpacing(12);
        var form = new QFormLayout();
        form.SetRowWrapPolicy(QFormLayout.RowWrapPolicy.DontWrapRows);
        form.SetFieldGrowthPolicy(QFormLayout.FieldGrowthPolicy.AllNonFixedFieldsGrow);

        _isNew = existing == null;
        _name = new QLineEdit();
        _name.SetText(existing?.Name ?? "");
        _name.SetPlaceholderText(I18n.T("credential.name.hint"));
        _username = new QLineEdit();
        _username.SetText(existing?.Username ?? "");
        _username.SetPlaceholderText(I18n.T("credential.username.hint"));
        _password = new QLineEdit();
        _password.SetEchoMode(QLineEdit.EchoMode.Password);
        _password.SetPlaceholderText(existing == null ? "" : I18n.T("credential.password.keep"));
        QLabel Label(string key) { var label = new QLabel(); I18n.Bind(label.SetText, key); return label; }
        form.AddRow(Label("credential.name"), _name);
        form.AddRow(Label("credential.username"), _username);

        // The secret is typed blind; a reader checking a long generated password should be able to
        // see it, and a checkbox beside the field is where every password manager puts that.
        var passwordRow = new QWidget();
        var passwordLayout = new QHBoxLayout(passwordRow);
        passwordLayout.SetContentsMargins(0, 0, 0, 0);
        passwordLayout.SetSpacing(8);
        passwordLayout.AddWidget(_password, 1);
        var reveal = new QCheckBox();
        I18n.Bind(reveal.SetText, "credential.password.show");
        reveal.Toggled += shown => _password.SetEchoMode(shown ? QLineEdit.EchoMode.Normal : QLineEdit.EchoMode.Password);
        passwordLayout.AddWidget(reveal);
        form.AddRow(Label("credential.password"), passwordRow);
        root.AddLayout(form);

        // Where the secret goes is a fact the reader cannot infer from the window, and it is the
        // one a service account makes surprising.
        var note = new QLabel();
        I18n.Bind(note.SetText, "credential.storage.note");
        note.SetObjectName("muted");
        note.SetWordWrap(true);
        root.AddWidget(note);

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
        _name.SetFocus();
    }

    private void Submit()
    {
        if (CredentialName.Length == 0) { Explain("credential.name.required"); return; }
        // Two rows reading the same thing would leave a picker with no way to tell which account
        // a layer means, so the form refuses the second one where it can say so.
        if (_takenNames.Any(name => string.Equals(name, CredentialName, StringComparison.OrdinalIgnoreCase)))
        { Explain("credential.name.duplicate"); return; }
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
