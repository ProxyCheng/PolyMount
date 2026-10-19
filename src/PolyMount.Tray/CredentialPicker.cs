using PolyMount.Core;
using PolyMount.Core.Ipc;
using QtSharp.Api;

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
    private readonly IReadOnlyList<string> _accepted;
    private string _selectedId;
    private QPushButton _use = null!;

    /// <summary>The list after any credential made or removed here, so the caller can keep it.</summary>
    public List<CredentialRecord> Credentials => Items;

    public string SelectedId => _selectedId;

    public CredentialPicker(QWidget? parent, List<CredentialRecord> items, string selectedId,
        string defaultText, string defaultDetail, bool refresh = true,
        IReadOnlyList<string>? accepted = null) : base(parent, load: false)
    {
        _selectedId = selectedId;
        _defaultText = defaultText;
        _defaultDetail = defaultDetail;
        _accepted = accepted ?? Array.Empty<string>();
        Items = items;
        // The store can change while the editor that opened this is idle (a credential deleted
        // from the tray manager, say). Read it now so no row offers something that is already gone.
        if (refresh) Reload(null);
        else { CountUsage(); Rebuild(); }
    }

    protected override string TitleKey => "credential.pick.title";
    protected override bool ShowIntro => false;

    /// <summary>What the consumer accepts is the filter: a password credential cannot fill a
    /// slot that asks for a certificate, and offering it would only fail when the layer was
    /// built.</summary>
    protected override bool Accept(CredentialRecord item) =>
        _accepted.Count == 0 || _accepted.Contains(item.Kind);

    /// <summary>The row that stands for "the source provides its own account".</summary>
    protected override void AddLeadingRows() => AddRow(string.Empty, _defaultText, _defaultDetail);

    /// <summary>A layer can name a credential that has since been removed; show that state
    /// rather than selecting the default row, which would quietly change the layer on save.</summary>
    protected override void AddTrailingRows()
    {
        if (!string.IsNullOrWhiteSpace(_selectedId) && Items.All(item => item.Id != _selectedId))
            AddRow(_selectedId, T("credential.missing"), T("credential.missing.hint"));
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
            if (response.Ok) Items = response.Credentials;
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
    }
}
