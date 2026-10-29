using PolyMount.Core;
using PolyMount.Core.Ipc;
using QtSharp.QtCore;
using QtSharp.QtGui;
using QtSharp.QtWidgets;

namespace PolyMount.Tray;

/// <summary>
/// The credential list both windows are: search, rows, create, edit, delete. A credential
/// belongs to a person, not to a server — the same account usually opens several hosts — and a
/// layer names the one it uses.
///
/// The dialog never sees a secret: the list is metadata, a secret only travels one way, and an
/// edit that leaves the field empty keeps the stored one.
///
/// What the list is *for* is the subclass: managing the store (<see cref="CredentialManager"/>)
/// or choosing one for a layer (<see cref="CredentialPicker"/>). Everything they would otherwise
/// duplicate — the rows, the search, the form, the save and delete paths — lives here once.
/// </summary>
internal abstract class CredentialListDialog : QDialog
{
    protected readonly QListWidget _list;
    protected readonly QLineEdit _search;
    protected readonly QLabel _empty;
    protected readonly QPushButton _add, _edit, _remove;
    protected List<CredentialRecord> Items = new();
    protected readonly Dictionary<string, List<string>> UsedBy = new(StringComparer.OrdinalIgnoreCase);

    protected static string T(string key) => I18n.T(key);

    protected CredentialListDialog(QWidget? parent, bool load) : base(parent)
    {
        SetObjectName("credentialList");
        SetWindowTitle(T(TitleKey));
        SetWindowIcon(TrayIcons.App);
        Resize(520, 460);
        SetMinimumSize(440, 360);

        var root = new QVBoxLayout(this);
        root.SetContentsMargins(24, 22, 24, 20);
        root.SetSpacing(12);
        var heading = new QLabel();
        I18n.Bind(heading.SetText, TitleKey);
        heading.SetObjectName("detailTitle");
        root.AddWidget(heading);

        if (ShowIntro)
        {
            // One sentence, so the list needs no legend: what these are, and that the secret does
            // not come back out once saved.
            var intro = new QLabel();
            I18n.Bind(intro.SetText, "credential.intro");
            intro.SetObjectName("muted");
            intro.SetWordWrap(true);
            root.AddWidget(intro);
        }

        _search = new QLineEdit();
        _search.SetClearButtonEnabled(true);
        I18n.Bind(_search.SetPlaceholderText, "credential.search");
        _search.TextChanged += _ => Rebuild();
        root.AddWidget(_search);

        _list = ContinuousScrolling.List();
        _list.SetSelectionMode(QAbstractItemView.SelectionMode.SingleSelection);
        _list.SetHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff);
        _list.CurrentRowChanged += _ => UpdateActions();
        _list.ItemActivated += _ => Activate();
        root.AddWidget(_list, 1);
        _empty = new QLabel();
        I18n.Bind(_empty.SetText, "credential.empty");
        _empty.SetWordWrap(true);
        _empty.SetAlignment(Qt.Alignment.AlignCenter);
        _empty.SetObjectName("emptyState");
        root.AddWidget(_empty);

        var footer = new QHBoxLayout();
        _add = new QPushButton();
        I18n.Bind(_add.SetText, "credential.add");
        _add.Clicked += _ => AddNew();
        footer.AddWidget(_add);
        _edit = new QPushButton();
        I18n.Bind(_edit.SetText, "credential.edit");
        _edit.Clicked += _ => EditSelected();
        footer.AddWidget(_edit);
        _remove = new QPushButton();
        I18n.Bind(_remove.SetText, "credential.remove");
        _remove.Clicked += _ => RemoveSelected();
        footer.AddWidget(_remove);
        footer.AddStretch(1);
        BuildFooter(footer);
        root.AddLayout(footer);

        if (load) LoadAll();
    }

    /// <summary>What the window is called. A constant in a subclass: the base constructor reads
    /// it before the subclass has a body of its own.</summary>
    protected abstract string TitleKey { get; }

    /// <summary>The one-line explanation of what the list holds; a picker's title says enough.</summary>
    protected virtual bool ShowIntro => true;

    /// <summary>The buttons after the shared add/edit/remove. Called while the base constructor
    /// runs, so an override may use constants and controls, not its own state.</summary>
    protected virtual void BuildFooter(QHBoxLayout footer)
    {
        var close = new QPushButton();
        I18n.Bind(close.SetText, "credential.close");
        close.Clicked += _ => Reject();
        footer.AddWidget(close);
    }

    /// <summary>Whether this window offers the credential at all — a consumer's accepted kinds.</summary>
    protected virtual bool Accept(CredentialRecord item) => true;

    /// <summary>A row before the credentials, for what "no credential" means to this window.</summary>
    protected virtual void AddLeadingRows() { }

    /// <summary>A row after them, for a reference this store no longer holds.</summary>
    protected virtual void AddTrailingRows() { }

    /// <summary>The row to select when the list is rebuilt, when the window has one.</summary>
    protected virtual string? SelectedRowId => null;

    /// <summary>What a double click does; managing edits, choosing takes the row.</summary>
    protected virtual void Activate() => EditSelected();

    protected void LoadAll()
    {
        try
        {
            var response = SupervisorClient.Default.Credentials();
            if (!response.Ok) throw new InvalidOperationException(response.Error);
            Items = response.Credentials;
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
    protected void CountUsage()
    {
        UsedBy.Clear();
        foreach (var (id, names) in CredentialUsage.Of()) UsedBy[id] = names;
    }

    protected void Rebuild()
    {
        // The row is a card, the way a password manager draws an account: the name first, what it
        // signs in as underneath. Clicking anywhere selects it.
        string filter = _search.Text.Trim();
        _list.Clear();
        AddLeadingRows();
        foreach (var item in Items)
        {
            if (!Accept(item)) continue;
            if (filter.Length > 0 &&
                item.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                item.Username.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var row = new QListWidgetItem(Describe(item));
            row.SetData((int)Qt.ItemDataRole.UserRole, item.Id);
            UsedBy.TryGetValue(item.Id, out var usedBy);
            string detail = Describe(item);
            // Where it may be presented, spelled out: the count on the row is what makes a reader
            // look here, and the addresses are what they have to compare against a failing source.
            string origins = item.Origins is { Count: > 0 } list
                ? string.Join(", ", list)
                : T("credential.origins.none");
            detail += Environment.NewLine + T("credential.origins.tip") + " " + origins;
            if (!item.Usable) detail += Environment.NewLine + T("credential.unusable.hint");
            if (usedBy is { Count: > 0 })
                detail += Environment.NewLine + string.Join(Environment.NewLine, usedBy);
            row.SetToolTip(detail);
            _list.AddItem(row);
        }
        AddTrailingRows();
        bool none = Items.Count == 0;
        _empty.SetText(I18n.T(none ? "credential.empty" : "credential.noMatch"));
        _empty.SetVisible(_list.Count == 0);
        _search.SetEnabled(!none);

        int selected = 0;
        string? wanted = SelectedRowId;
        if (!string.IsNullOrEmpty(wanted))
            for (int i = 0; i < _list.Count; i++)
                if (_list.Item(i)!.Data((int)Qt.ItemDataRole.UserRole).ToString() == wanted)
                { selected = i; break; }
        if (_list.Count > 0) _list.SetCurrentRow(selected);

        UpdateActions();
    }

    /// <summary>The row's one line: the name, what kind of secret it is, and who it signs in as.
    /// A record this account cannot use is marked here, with the reason in the tooltip. How many
    /// servers it may be presented to is the one fact about a credential that explains a mount
    /// failing, so it is counted here and listed in the tooltip.</summary>
    protected static string Describe(CredentialRecord item)
    {
        string identity = item.Kind == CredentialTypes.Certificate
            ? I18n.T("credential.kind.certificate")
            : item.Username;
        string text = item.Name + " · " + identity;
        if (item.Origins is { Count: > 0 } origins)
            text += " · " + I18n.F("credential.origins.count", origins.Count);
        return item.Usable ? text : text + "  ⚠";
    }

    protected void AddRow(string id, string text, string? tooltip = null)
    {
        // A plain item, not a widget: the delegate draws one line of text at the right height on
        // every style, where a widget item kept being clipped regardless of the size hint.
        var row = new QListWidgetItem(text);
        row.SetData((int)Qt.ItemDataRole.UserRole, id);
        if (!string.IsNullOrEmpty(tooltip)) row.SetToolTip(tooltip);
        _list.AddItem(row);
    }

    protected virtual void UpdateActions()
    {
        string? id = CurrentId();
        bool saved = !string.IsNullOrWhiteSpace(id) && Items.Any(item => item.Id == id);
        _edit.SetEnabled(saved);
        _remove.SetEnabled(saved);
    }

    protected string? CurrentId() =>
        _list.CurrentItem()?.Data((int)Qt.ItemDataRole.UserRole).ToString();

    protected void AddNew() => Edit(null);

    protected void EditSelected()
    {
        if (CurrentId() is { } id && Items.FirstOrDefault(item => item.Id == id) is { } item) Edit(item);
    }

    private void Edit(CredentialRecord? existing)
    {
        using var form = new CredentialForm(this, existing, Items);
        if (form.Exec() != 1) return;
        try
        {
            var response = SupervisorClient.Default.SaveCredential(form.ToPayload(existing?.Id));
            if (!response.Ok) throw new InvalidOperationException(response.Error);
            Reload(form.CredentialName);
        }
        catch (Exception ex)
        {
            QMessageBox.Information(this, T(TitleKey), I18n.F("credential.failed", ex.Message), T("credential.close"));
        }
    }

    protected void RemoveSelected()
    {
        if (CurrentId() is not { } id) return;
        string question = UsedBy.GetValueOrDefault(id) is { Count: > 0 } usedBy
            ? I18n.F("credential.delete.confirm", string.Join(", ", usedBy))
            : T("credential.delete.plain");
        if (QMessageBox.Question(this, T(TitleKey), question, T("credential.remove"), T("cancel"), null, 1, 1) != 0) return;
        try { SupervisorClient.Default.DeleteCredential(id); } catch (Exception) { }
        Reload(null);
    }

    /// <summary>Reads the store again after a save or a delete. A window that holds a selection
    /// says what to keep selected; the store itself is the answer either way.</summary>
    protected virtual void Reload(string? selectName) => LoadAll();

    /// <summary>Builds the list with a fixture instead of asking the service, so a self-check can
    /// look at the rows and the actions without a running host.</summary>
    internal void ShowItemsForCheck(List<CredentialRecord> items, Dictionary<string, int> usage)
    {
        Items = items;
        UsedBy.Clear();
        foreach (var (key, value) in usage)
            UsedBy[key] = Enumerable.Range(1, value).Select(n => "instance / layer " + n).ToList();
        Rebuild();
    }
}

/// <summary>The store's own window: every credential, with create, edit and delete.</summary>
internal sealed class CredentialManager : CredentialListDialog
{
    public CredentialManager(QWidget? parent, bool load = true) : base(parent, load) { }

    protected override string TitleKey => "credential.title";

    internal void ShowForCheck(List<CredentialRecord> items, Dictionary<string, int> usage) =>
        ShowItemsForCheck(items, usage);

    /// <summary>Builds the manager with fixtures and checks that each row is one complete line and
    /// that the actions follow the selection; a capture can be taken when one is asked for.</summary>
    internal static void Smoke(QWidget parent, string? capture = null)
    {
        var items = new List<CredentialRecord>
        {
            new("a", "Work SVN", "alice"),
            new("b", "Personal", "bob")
        };
        var usage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["a"] = 2 };
        var manager = new CredentialManager(parent, load: false);
        manager.ShowForCheck(items, usage);
        manager.Show();
        QCoreApplication.ProcessEvents();
        // The two buttons open this form; constructing it here keeps a field that cannot be
        // built from turning into a click that does nothing. It is deleted with the manager,
        // not before, so a pending event is not dispatched into a destroyed wrapper.
        var form = new CredentialForm(manager, null, items);
        var certificate = new CredentialForm(manager,
            new CredentialRecord("c", "Client", "", CredentialTypes.Certificate), items);
        // The kind is what makes the form a certificate form or an account form; a form opened
        // for one kind that reports the other would save the wrong shape without saying so.
        if (form.Kind != CredentialTypes.Password)
            throw new InvalidOperationException("a new credential form does not report the password kind");
        if (certificate.Kind != CredentialTypes.Certificate)
            throw new InvalidOperationException("a certificate form reports kind '" + certificate.Kind + "'");
        certificate.SelectKind(CredentialTypes.Password);
        QCoreApplication.ProcessEvents();
        if (certificate.Kind != CredentialTypes.Password)
            throw new InvalidOperationException("switching the kind picker does not change the form's kind");
        certificate.SelectKind(CredentialTypes.Certificate);
        QCoreApplication.ProcessEvents();
        if (certificate.Kind != CredentialTypes.Certificate)
            throw new InvalidOperationException("switching back to the certificate kind was lost");
        // The servers field: shown as stored, and carried by the save after an edit. A form that
        // dropped it would send nothing, which the store reads as "keep" — so a reader who removed
        // one address would watch the mount keep failing at it.
        var shared = new CredentialForm(manager, new CredentialRecord("d", "Shared", "carol",
            CredentialTypes.Password, true, new[] { "https://svn.example.invalid" }), items);
        if (!shared.OriginsField.Contains("https://svn.example.invalid", StringComparison.Ordinal))
            throw new InvalidOperationException("A stored credential's servers are not shown in the form: " + shared.OriginsField);
        shared.OriginsField = "https://a.example.invalid; https://b.example.invalid";
        var edited = shared.ToPayload("d");
        if (edited.Origins is not { Count: 2 } || edited.Origins[1] != "https://b.example.invalid")
            throw new InvalidOperationException(
                "An edited servers field does not reach the save: " + string.Join(", ", edited.Origins ?? new List<string>()));
        shared.Hide();
        form.Hide();
        certificate.Hide();
        try
        {
            if (manager._list.Count != items.Count)
                throw new InvalidOperationException(
                    $"Credential manager shows {manager._list.Count} rows, expected {items.Count}");
            int line = manager._list.FontMetrics().Height();
            for (int i = 0; i < manager._list.Count; i++)
            {
                var item = manager._list.Item(i)!;
                if (string.IsNullOrWhiteSpace(item.Text()))
                    throw new InvalidOperationException($"Credential manager row {i} has no text");
                int height = manager._list.VisualItemRect(item).Height();
                if (height < line)
                    throw new InvalidOperationException(
                        $"Credential manager row {i} is {height}px tall, shorter than its {line}px line");
            }
            manager._list.SetCurrentRow(0);
            QCoreApplication.ProcessEvents();
            if (!manager._edit.IsEnabled() || !manager._remove.IsEnabled())
                throw new InvalidOperationException("A selected credential cannot be edited or removed");
            if (capture != null) { using var image = manager.Grab(); image.Save(capture); }
        }
        finally { shared.DeleteLater(); form.DeleteLater(); certificate.DeleteLater(); manager.Hide(); manager.DeleteLater(); }
    }
}

/// <summary>Name, user, and a password that is only sent when it is typed.</summary>
internal sealed class CredentialForm : QDialog
{
    private readonly QComboBox _kind;
    private readonly QLineEdit _name, _username, _password, _origins, _certificatePath, _certificatePassword;
    private readonly QWidget _certificateRows;
    private readonly QLabel _error;
    private readonly bool _isNew;
    private readonly string _originalKind;
    private readonly List<string> _takenNames;
    private byte[]? _certificate;

    public string CredentialName => _name.Text.Trim();
    public string Kind => _kind.CurrentIndex == 0 ? CredentialTypes.Password : CredentialTypes.Certificate;
    public string Username => _username.Text.Trim();
    public string? Password => _password.Text.Length > 0 ? _password.Text : null;
    /// <summary>The file chosen in this form, or null to keep the one the record holds.</summary>
    public byte[]? Certificate => _certificate;
    public string? CertificatePassword => _certificatePassword.Text.Length > 0 ? _certificatePassword.Text : null;

    public CredentialForm(QWidget? parent, CredentialRecord? existing, IReadOnlyList<CredentialRecord> all) : base(parent)
    {
        SetWindowTitle(I18n.T(existing == null ? "credential.add.title" : "credential.edit.title"));
        SetWindowIcon(TrayIcons.App);
        SetMinimumWidth(460);
        _takenNames = all.Where(item => item.Id != existing?.Id).Select(item => item.Name).ToList();
        _isNew = existing == null;
        _originalKind = existing?.Kind ?? CredentialTypes.Password;

        var root = new QVBoxLayout(this);
        root.SetContentsMargins(24, 22, 24, 20);
        root.SetSpacing(12);

        // What the secret is comes first: a certificate credential is not an account with a
        // different password, and the fields below it follow from the answer.
        _kind = ContinuousScrolling.Combo();
        _kind.AddItem(I18n.T("credential.kind.password"));
        _kind.AddItem(I18n.T("credential.kind.certificate"));
        _kind.SetCurrentIndex(_originalKind == CredentialTypes.Certificate ? 1 : 0);

        var form = new QFormLayout();
        form.SetRowWrapPolicy(QFormLayout.RowWrapPolicy.DontWrapRows);
        form.SetFieldGrowthPolicy(QFormLayout.FieldGrowthPolicy.AllNonFixedFieldsGrow);

        _name = new QLineEdit();
        _name.SetText(existing?.Name ?? "");
        _name.SetPlaceholderText(I18n.T("credential.name.hint"));
        _username = new QLineEdit();
        _username.SetText(existing?.Username ?? "");
        _username.SetPlaceholderText(I18n.T("credential.username.hint"));
        // The servers this account may be presented to. It is the credential's own record of where
        // it belongs: the first source that uses it writes its origin down, and a second one is
        // refused until it is named here. That refusal is what a mount reports as an error, so this
        // field is the one place its remedy can be carried out.
        _origins = new QLineEdit();
        _origins.SetText(existing?.Origins is { Count: > 0 } origins ? string.Join(", ", origins) : "");
        _origins.SetPlaceholderText(I18n.T("credential.origins.placeholder"));
        I18n.Bind(_origins.SetToolTip, "credential.origins.hint");
        _password = new QLineEdit();
        _password.SetEchoMode(QLineEdit.EchoMode.Password);
        _password.SetPlaceholderText(existing == null ? "" : I18n.T("credential.password.keep"));
        QLabel Label(string key) { var label = new QLabel(); I18n.Bind(label.SetText, key); return label; }

        form.AddRow(Label("credential.kind"), _kind);
        form.AddRow(Label("credential.name"), _name);
        form.AddRow(Label("credential.username"), _username);
        form.AddRow(Label("credential.origins"), _origins);
        form.AddRow(Label("credential.password"), Reveal(_password));

        // The certificate lives in its own block: a password credential leaves it hidden, and a
        // certificate credential may still name the account the server asks for.
        _certificateRows = new QWidget();
        var certificateForm = new QFormLayout(_certificateRows);
        certificateForm.SetContentsMargins(0, 0, 0, 0);
        certificateForm.SetRowWrapPolicy(QFormLayout.RowWrapPolicy.DontWrapRows);
        certificateForm.SetFieldGrowthPolicy(QFormLayout.FieldGrowthPolicy.AllNonFixedFieldsGrow);

        _certificatePath = new QLineEdit();
        _certificatePath.SetReadOnly(true);
        _certificatePath.SetPlaceholderText(I18n.T(_isNew || _originalKind != CredentialTypes.Certificate
            ? "credential.certificate.hint" : "credential.certificate.keep"));
        var browse = new QPushButton();
        I18n.Bind(browse.SetText, "credential.certificate.browse");
        browse.Clicked += _ => ChooseCertificate();
        var fileRow = new QWidget();
        var fileLayout = new QHBoxLayout(fileRow);
        fileLayout.SetContentsMargins(0, 0, 0, 0);
        fileLayout.SetSpacing(8);
        fileLayout.AddWidget(_certificatePath, 1);
        fileLayout.AddWidget(browse);
        certificateForm.AddRow(Label("credential.certificate"), fileRow);

        _certificatePassword = new QLineEdit();
        _certificatePassword.SetEchoMode(QLineEdit.EchoMode.Password);
        _certificatePassword.SetPlaceholderText(_isNew || _originalKind != CredentialTypes.Certificate
            ? "" : I18n.T("credential.password.keep"));
        certificateForm.AddRow(Label("credential.certificate.password"), Reveal(_certificatePassword));
        form.AddRow(_certificateRows);
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

        // After the error label exists: the rows were built above, but the first refresh hides
        // it, and doing that before it is constructed is a null dereference in a click handler —
        // which Qt reports nowhere, so the button would simply do nothing.
        _kind.CurrentIndexChanged += _ => RefreshKind();
        RefreshKind();

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

    /// <summary>A password field and the checkbox that reveals it, which is where every password
    /// manager puts it: a reader checking a long generated secret should be able to see it.</summary>
    private static QWidget Reveal(QLineEdit field)
    {
        var row = new QWidget();
        var layout = new QHBoxLayout(row);
        layout.SetContentsMargins(0, 0, 0, 0);
        layout.SetSpacing(8);
        layout.AddWidget(field, 1);
        var reveal = new QCheckBox();
        I18n.Bind(reveal.SetText, "credential.password.show");
        reveal.Toggled += shown => field.SetEchoMode(shown ? QLineEdit.EchoMode.Normal : QLineEdit.EchoMode.Password);
        layout.AddWidget(reveal);
        return row;
    }

    /// <summary>Moves the type picker. The control is private to the form; the smoke uses this
    /// to check that moving it changes what the form reports and will save.</summary>
    internal void SelectKind(string kind) =>
        _kind.SetCurrentIndex(kind == CredentialTypes.Certificate ? 1 : 0);

    /// <summary>The request a save sends. Both places that can save — the credential manager and
    /// the picker a layer opens — take it from here: a second builder that forgets a field saves
    /// a credential of the wrong shape, which is invisible until the mount fails.</summary>
    internal CredentialPayload ToPayload(string? id = null) => new()
    {
        Id = id,
        Name = CredentialName,
        Kind = Kind,
        Username = Username,
        Password = Password,
        Certificate = Certificate is { Length: > 0 } certificate ? Convert.ToBase64String(certificate) : null,
        CertificatePassword = CertificatePassword,
        // Always the list the form shows, not null: null means "keep what the record has", and a
        // reader who removed an address would then watch it come back. Empty is a real answer — it
        // says this account is bound to nothing, and the next source to use it writes its origin.
        Origins = Origins()
    };

    /// <summary>The addresses in the field, split on whatever separates them. The store checks each
    /// one and refuses anything that is not an origin, so a typo is reported rather than stored as
    /// an entry that can never match.</summary>
    private List<string> Origins() => _origins.Text
        .Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    /// <summary>The servers field, for a self-check: what the form shows when a record is opened,
    /// and what a save carries after it is edited. This field is the remedy a mount's error names,
    /// so it has to round-trip.</summary>
    internal string OriginsField
    {
        get => _origins.Text;
        set => _origins.SetText(value);
    }

    private void RefreshKind()
    {
        _certificateRows.SetVisible(Kind == CredentialTypes.Certificate);
        _error.SetVisible(false);
    }

    private void ChooseCertificate()
    {
        string path = QFileDialog.GetOpenFileName(this, I18n.T("credential.certificate.browse"), "",
            I18n.T("credential.certificate.filter"));
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            _certificate = File.ReadAllBytes(path);
            _certificatePath.SetText(path);
            _error.SetVisible(false);
        }
        catch (Exception ex)
        {
            _certificate = null;
            _certificatePath.SetText("");
            Explain(I18n.F("credential.certificate.unreadable", ex.Message));
        }
    }

    private void Submit()
    {
        if (CredentialName.Length == 0) { ExplainKey("credential.name.required"); return; }
        // Two rows reading the same thing would leave a picker with no way to tell which account
        // a layer means, so the form refuses the second one where it can say so.
        if (_takenNames.Any(name => string.Equals(name, CredentialName, StringComparison.OrdinalIgnoreCase)))
        { ExplainKey("credential.name.duplicate"); return; }

        if (Kind == CredentialTypes.Password)
        {
            if (Username.Length == 0) { ExplainKey("credential.username.required"); return; }
            bool keepsPassword = !_isNew && _originalKind == CredentialTypes.Password;
            if (Password == null && !keepsPassword) { ExplainKey("credential.password.required"); return; }
        }
        else
        {
            // The account is optional for a certificate credential: some servers authenticate on
            // the certificate alone.
            bool keepsCertificate = !_isNew && _originalKind == CredentialTypes.Certificate;
            if (_certificate == null && !keepsCertificate) { ExplainKey("credential.certificate.required"); return; }
            if (CertificatePassword == null && !keepsCertificate) { ExplainKey("credential.certificate.password.required"); return; }
        }
        Accept();
    }

    private void ExplainKey(string key) => Explain(I18n.T(key));

    private void Explain(string text)
    {
        _error.SetText(text);
        _error.SetVisible(true);
    }
}
