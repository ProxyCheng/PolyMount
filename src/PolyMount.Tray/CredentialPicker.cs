using PolyMount.Core;
using PolyMount.Core.Ipc;
using QtSharp.Api;

namespace PolyMount.Tray;

/// <summary>
/// Chooses the one credential a layer uses. The first row is "no credential", because a source
/// may resolve its own account (for SVN, the one its client saved); the named credentials follow.
/// A credential can be made here, so choosing one never means leaving the layer being edited.
/// </summary>
internal sealed class CredentialPicker : QDialog
{
    private readonly QListWidget _list;
    private readonly QPushButton _use, _remove;
    private readonly string _defaultText, _defaultDetail;
    private List<CredentialRecord> _items;
    private string _selectedId;

    /// <summary>The list after any credential made or removed here, so the caller can keep it.</summary>
    public List<CredentialRecord> Credentials => _items;
    public string SelectedId => _selectedId;

    public CredentialPicker(QWidget? parent, List<CredentialRecord> items, string selectedId,
        string defaultText, string defaultDetail) : base(parent)
    {
        _items = items;
        _selectedId = selectedId;
        _defaultText = defaultText;
        _defaultDetail = defaultDetail;
        // The store can change while the editor that opened this is idle (a credential deleted
        // from the tray manager, say). Read it now so no row offers something that is already gone.
        try
        {
            var current = SupervisorClient.Default.Credentials();
            if (current.Ok) _items = current.Credentials;
        }
        catch (Exception) { }

        SetObjectName("credentialPicker");
        SetWindowTitle(I18n.T("credential.pick.title"));
        SetWindowIcon(TrayIcons.App);
        Resize(460, 430);
        SetMinimumSize(400, 320);

        var root = new QVBoxLayout(this);
        root.SetContentsMargins(24, 22, 24, 20);
        root.SetSpacing(12);
        var heading = new QLabel();
        I18n.Bind(heading.SetText, "credential.pick.title");
        heading.SetObjectName("detailTitle");
        root.AddWidget(heading);

        _list = ContinuousScrolling.List();
        _list.SetSelectionMode(QAbstractItemView.SelectionMode.SingleSelection);
        _list.SetHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff);
        _list.ItemActivated += _ => UseSelected();
        _list.CurrentRowChanged += _ => UpdateActions();
        root.AddWidget(_list, 1);

        var buttons = new QHBoxLayout();
        var add = new QPushButton();
        I18n.Bind(add.SetText, "credential.pick.new");
        add.Clicked += _ => NewCredential();
        buttons.AddWidget(add);
        var manage = new QPushButton();
        I18n.Bind(manage.SetText, "credential.manage");
        manage.Clicked += _ => Manage();
        buttons.AddWidget(manage);
        // Removing one is a management action, but it belongs here too: choosing an account and
        // finding it obsolete should not mean opening another dialog first.
        _remove = new QPushButton();
        I18n.Bind(_remove.SetText, "credential.remove");
        _remove.Clicked += _ => RemoveSelected();
        buttons.AddWidget(_remove);
        buttons.AddStretch(1);
        var cancel = new QPushButton();
        I18n.Bind(cancel.SetText, "cancel");
        cancel.Clicked += _ => Reject();
        buttons.AddWidget(cancel);
        _use = new QPushButton();
        I18n.Bind(_use.SetText, "credential.pick.use");
        _use.SetObjectName("primary");
        _use.SetDefault(true);
        _use.Clicked += _ => UseSelected();
        buttons.AddWidget(_use);
        root.AddLayout(buttons);

        Rebuild();
    }

    private void Rebuild()
    {
        _list.Clear();
        AddRow(string.Empty, _defaultText, "", _defaultDetail);
        foreach (var item in _items) AddRow(item.Id, item.Name, "· " + item.Username, item.Name + " · " + item.Username);
        // A layer can name a credential that has since been removed. Show that state rather than
        // selecting the default row, which would quietly change the layer on the next save.
        int selected = 0;
        if (!string.IsNullOrWhiteSpace(_selectedId) && _items.All(item => item.Id != _selectedId))
            selected = AddRow(_selectedId, I18n.T("credential.missing"), "", I18n.T("credential.missing.hint"));
        else
            for (int i = 0; i < _list.Count; i++)
                if (_list.Item(i)!.Data((int)Qt.ItemDataRole.UserRole).ToString() == _selectedId)
                { selected = i; break; }
        if (_list.Count > 0) _list.SetCurrentRow(selected);
        UpdateActions();
    }

    /// <summary>Delete applies to a saved credential, never to the "none" row or to a reference
    /// whose credential is already gone.</summary>
    private void UpdateActions()
    {
        string? id = CurrentId();
        _remove.SetEnabled(!string.IsNullOrWhiteSpace(id) && _items.Any(item => item.Id == id));
    }

    private int AddRow(string id, string title, string detail, string? tooltip = null)
    {
        // A plain item, not a widget: the delegate draws one line of text at the right height on
        // every style, where a widget item kept being clipped regardless of the size hint.
        var row = new QListWidgetItem(string.IsNullOrEmpty(detail) ? title : title + " " + detail);
        row.SetData((int)Qt.ItemDataRole.UserRole, new QVariant(id));
        if (!string.IsNullOrEmpty(tooltip)) row.SetToolTip(tooltip);
        _list.AddItem(row);
        return _list.Count - 1;
    }

    private string? CurrentId() =>
        _list.CurrentItem()?.Data((int)Qt.ItemDataRole.UserRole).ToString();

    private void UseSelected()
    {
        _selectedId = CurrentId() ?? string.Empty;
        Accept();
    }

    /// <summary>Removes one from the store, here. A reference to it that this layer held is kept
    /// visible as "missing" rather than silently turned into "none".</summary>
    private void RemoveSelected()
    {
        string? id = CurrentId();
        if (string.IsNullOrWhiteSpace(id) || _items.All(item => item.Id != id)) return;
        var usedBy = CredentialUsage.Of().GetValueOrDefault(id);
        string question = usedBy is { Count: > 0 }
            ? I18n.F("credential.delete.confirm", string.Join(", ", usedBy))
            : I18n.T("credential.delete.plain");
        if (QMessageBox.Question(this, I18n.T("credential.title"), question,
                I18n.T("credential.remove"), I18n.T("cancel"), null, 1, 1) != 0) return;
        try
        {
            var response = SupervisorClient.Default.DeleteCredential(id);
            if (!response.Ok) throw new InvalidOperationException(response.Error);
            _items = response.Credentials;
        }
        catch (Exception ex)
        {
            QMessageBox.Information(this, I18n.T("credential.title"),
                I18n.F("credential.delete.failed", ex.Message), I18n.T("credential.close"));
            return;
        }
        Rebuild();
    }

    /// <summary>Creates one without leaving the picker, then selects it: the reader can see what
    /// was made and still cancel before the layer takes it.</summary>
    private void NewCredential()
    {
        using var form = new CredentialForm(this, null, _items);
        if (form.Exec() != 1) return;
        try
        {
            var response = SupervisorClient.Default.SaveCredential(new CredentialPayload
            {
                Name = form.CredentialName,
                Username = form.Username,
                Password = form.Password
            });
            if (!response.Ok) throw new InvalidOperationException(response.Error);
            _items = response.Credentials;
            // Names are unique, so the service's answer names the new record without a second call.
            _selectedId = _items.FirstOrDefault(item =>
                string.Equals(item.Name, form.CredentialName, StringComparison.OrdinalIgnoreCase))?.Id ?? _selectedId;
            Rebuild();
        }
        catch (Exception ex)
        {
            QMessageBox.Information(this, I18n.T("credential.title"),
                I18n.F("credential.failed", ex.Message), I18n.T("credential.close"));
        }
    }

    /// <summary>The full manager, for editing or removing one. Whatever it left behind is what
    /// this picker then offers.</summary>
    private void Manage()
    {
        using (var manager = new CredentialManager(this)) manager.Exec();
        try
        {
            var response = SupervisorClient.Default.Credentials();
            if (response.Ok) _items = response.Credentials;
        }
        catch (Exception) { }
        Rebuild();
    }
}
