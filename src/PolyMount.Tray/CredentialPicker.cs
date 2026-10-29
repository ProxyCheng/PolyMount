using PolyMount.Core;
using PolyMount.Core.Ipc;
using QtSharp.QtCore;
using QtSharp.QtGui;
using QtSharp.QtWidgets;

namespace PolyMount.Tray;

/// <summary>
/// Chooses the one credential a layer uses: the store's list with a "no credential" row first,
/// because a source may resolve its own account (for SVN, the one its client saved). A
/// credential can be made here, so choosing one never means leaving the layer being edited.
///
/// The list itself — rows, search, create, edit, delete — is the base class; this subclass only
/// answers what the list means: which kinds the consumer accepts, which row stands for none,
/// and what a double click does.
/// </summary>
internal sealed class CredentialPicker : CredentialListDialog
{
    private readonly string _defaultText, _defaultDetail;
    private readonly string _consumer;
    private string _selectedId;
    private QPushButton _use = null!;

    /// <summary>The list after any credential made or removed here, so the caller can keep it.</summary>
    public List<CredentialRecord> Credentials => Items;

    public string SelectedId => _selectedId;

    public CredentialPicker(QWidget? parent, List<CredentialRecord> items, string selectedId,
        string defaultText, string defaultDetail, bool refresh = true,
        string? consumer = null) : base(parent, load: false)
    {
        _selectedId = selectedId;
        _defaultText = defaultText;
        _defaultDetail = defaultDetail;
        _consumer = consumer ?? "";
        Items = items;
        // The store can change while the editor that opened this is idle (a credential deleted
        // from the tray manager, say). Read it now so no row offers something that is already gone.
        if (refresh) Reload(null);
        else { CountUsage(); Rebuild(); }
    }

    protected override string TitleKey => "credential.pick.title";
    protected override bool ShowIntro => false;

    /// <summary>What the layer can present is the filter, and the answer comes with each record:
    /// the service asked the layer itself when the list was read, so this window needs no list of
    /// kinds and a kind added after it was built is offered like any other. Offering a record the
    /// layer cannot present would only fail when the layer was built.</summary>
    protected override bool Accept(CredentialRecord item) =>
        _consumer.Length == 0 || item.UseFor.Contains(_consumer, StringComparer.OrdinalIgnoreCase);

    /// <summary>The row that stands for "the source provides its own account".</summary>
    protected override void AddLeadingRows() => AddRow(string.Empty, _defaultText, _defaultDetail);

    /// <summary>A layer can name a credential that has since been removed; show that state
    /// rather than selecting the default row, which would quietly change the layer on save.</summary>
    protected override void AddTrailingRows()
    {
        if (!string.IsNullOrWhiteSpace(_selectedId) && Items.All(item => item.Id != _selectedId))
            AddRow(_selectedId, T("credential.missing"), T("credential.missing.hint"),
                "alert", TrayTheme.Tokens.Warning);
    }

    protected override string? SelectedRowId => _selectedId;

    protected override void Activate() => UseSelected();

    protected override void BuildFooter(QHBoxLayout footer)
    {
        var cancel = new QPushButton();
        I18n.Bind(cancel.SetText, "cancel");
        cancel.Clicked += _ => Reject();
        footer.AddWidget(cancel);
        _use = new QPushButton();
        I18n.Bind(_use.SetText, "credential.pick.use");
        _use.SetObjectName("primary");
        _use.SetDefault(true);
        _use.Clicked += _ => UseSelected();
        footer.AddWidget(_use);
    }

    /// <summary>Removing is a management action, but it belongs here too: choosing an account
    /// and finding it obsolete should not mean opening another window first.</summary>
    protected override void UpdateActions()
    {
        base.UpdateActions();
        _remove.SetEnabled(CurrentId() is { Length: > 0 } id && Items.Any(item => item.Id == id));
    }

    protected override void Reload(string? selectName)
    {
        try
        {
            var response = SupervisorClient.Default.Credentials();
            // The whole answer, not just the records: the window this picker opens to make a new
            // account draws its kinds from the same reply.
            if (response.Ok) Apply(response);
        }
        catch (Exception) { }
        if (selectName != null)
            _selectedId = Items.FirstOrDefault(item =>
                string.Equals(item.Name, selectName, StringComparison.OrdinalIgnoreCase))?.Id ?? _selectedId;
        CountUsage();
        Rebuild();
    }

    internal void UseSelected()
    {
        _selectedId = CurrentId() ?? string.Empty;
        Accept();
    }

    internal static void Smoke(QWidget parent, string? capture = null)
    {
        var items = new List<CredentialRecord>
        {
            new("a", "Work SVN", "alice"),
            new("b", "Personal", "bob")
        };
        var picker = new CredentialPicker(parent, items, "a", "None",
            "The source provides its own account.", refresh: false);
        picker.Show();
        try
        {
            if (picker._list.Count != items.Count + 1)
                throw new InvalidOperationException(
                    $"Credential picker shows {picker._list.Count} rows, expected {items.Count + 1}");
            int line = picker._list.FontMetrics().Height();
            for (int i = 0; i < picker._list.Count; i++)
            {
                var item = picker._list.Item(i)!;
                if (string.IsNullOrWhiteSpace(item.Text()))
                    throw new InvalidOperationException($"Credential row {i} has no text");
                // The first row is not a credential, and it still carries an empty mark: without
                // one its text would start left of every row below it.
                if (item.Icon().IsNull())
                    throw new InvalidOperationException($"Credential row {i} has no mark");
                int height = picker._list.VisualItemRect(item).Height();
                if (height < line)
                    throw new InvalidOperationException(
                        $"Credential row {i} is {height}px tall, shorter than its {line}px line");
            }
            picker._list.SetCurrentRow(1);
            QCoreApplication.ProcessEvents();
            if (!picker._remove.IsEnabled())
                throw new InvalidOperationException("A saved credential row cannot be removed");
            picker._list.SetCurrentRow(0);
            QCoreApplication.ProcessEvents();
            if (picker._remove.IsEnabled())
                throw new InvalidOperationException("The 'none' row can be removed");
            picker._list.SetCurrentRow(1);
            QCoreApplication.ProcessEvents();
            if (capture != null) { using var image = picker.Grab(); image.Save(capture); }
            picker.UseSelected();
            if (picker.Result() != 1 || picker.SelectedId != "a")
                throw new InvalidOperationException("Credential selection was not accepted");
        }
        finally { picker.Hide(); picker.DeleteLater(); }

        // What decides whether a row is offered is the layer's own answer, which travels with each
        // record — both directions are pinned here, because a picker that forgets the filter offers
        // an account that fails when the layer is built, and one that applies it too strictly
        // hides the credential a reader just made in this window.
        var mixed = new List<CredentialRecord>
        {
            new("svn-only", "TortoiseSVN", "alice", "svn-cached") { UseFor = new[] { "svn" } },
            new("both", "Work SVN", "bob", CredentialTypes.Password) { UseFor = new[] { "svn", "smb" } },
            new("smb-only", "Share", "carol", CredentialTypes.Password) { UseFor = new[] { "smb" } }
        };
        var filtered = new CredentialPicker(parent, mixed, "", "None", "", refresh: false,
            consumer: "svn");
        filtered.Show();
        try
        {
            QCoreApplication.ProcessEvents();
            // The "none" row plus the two records this layer can present, in the store's order.
            if (filtered._list.Count != 3)
                throw new InvalidOperationException(
                    $"The picker offered {filtered._list.Count - 1} credentials to a layer that can present 2");
            string first = filtered._list.Item(1)!.Data((int)Qt.ItemDataRole.UserRole)?.ToString() ?? "";
            string second = filtered._list.Item(2)!.Data((int)Qt.ItemDataRole.UserRole)?.ToString() ?? "";
            if (first != "svn-only" || second != "both")
                throw new InvalidOperationException(
                    "The picker did not keep exactly the records the layer can present: " + first + ", " + second);

            filtered._list.SetCurrentRow(1);
            QCoreApplication.ProcessEvents();
            filtered.UseSelected();
            if (filtered.SelectedId != "svn-only")
                throw new InvalidOperationException("The offered record could not be chosen: " + filtered.SelectedId);
        }
        finally { filtered.Hide(); filtered.DeleteLater(); }

        // When a service answers, the window it opens to make a new account has to know the kinds
        // the store knows — a picker that refreshed only the records left that form offering the
        // two kinds built into the tray, which is a plugin kind nobody could ever create here.
        ControlResponse live;
        try { live = SupervisorClient.Default.Credentials(); }
        catch (Exception) { return; }   // no service: the rest of this check needs none
        if (!live.Ok) return;

        var refreshed = new CredentialPicker(parent, new List<CredentialRecord>(), "", "None", "", refresh: true);
        refreshed.Show();
        try
        {
            QCoreApplication.ProcessEvents();
            if (refreshed.Kinds.Count != live.CredentialKinds.Count)
                throw new InvalidOperationException(
                    $"The picker knows {refreshed.Kinds.Count} credential kinds where the store reports {live.CredentialKinds.Count}");
        }
        finally { refreshed.Hide(); refreshed.DeleteLater(); }
    }
}
