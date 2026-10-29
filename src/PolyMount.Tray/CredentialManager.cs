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
    /// <summary>The credential kinds loaded plugins describe, as the service last reported them.
    /// The two the product ships are not in here: the window knows them without being told.</summary>
    protected List<CredentialKindDescriptor> Kinds = new();

    /// <summary>The layer types that say which credentials they can present, as the service
    /// reported them with the list. A picker is unconstrained by a type that is not here: it never
    /// said, and refusing everything is not the answer to a question nobody was asked.</summary>
    protected List<string> Consumers = new();
    protected readonly Dictionary<string, List<string>> UsedBy = new(StringComparer.OrdinalIgnoreCase);

    protected static string T(string key) => I18n.T(key);

    /// <summary>What the list holds, for a self-check: the rows it drew, and what it says when
    /// there are none. A section that shows neither is a section nobody can read.</summary>
    internal int RowCount => _list.Count;
    internal string EmptyReason => _empty.Text;

    /// <summary>Whether this is a section of the workbench rather than a window. A page has no
    /// close button — there is nothing to close — and does not take the dialog's window flags, so
    /// the dashboard can put it beside the mounts it belongs to.</summary>
    protected readonly bool IsPage;

    protected CredentialListDialog(QWidget? parent, bool load, bool page = false) : base(parent)
    {
        IsPage = page;
        // A QDialog is a QWidget: with the widget flags it lays out as a child of the page and
        // stops behaving like a window (no title bar, no modality, no Escape-to-close).
        if (page) SetWindowFlags((Qt.WindowFlags)Qt.WindowType.Widget);
        SetWindowTitle(T(TitleKey));
        SetWindowIcon(TrayIcons.App);
        if (!page)
        {
            Resize(520, 460);
            SetMinimumSize(440, 360);
        }

        // A page wears the frame every section of the workbench wears (PageChrome); a dialog
        // keeps the frame it has always had. The one that looked like a different program was
        // this one, because it was assembled from a window's parts and given a page's job.
        QVBoxLayout root;
        if (page)
        {
            root = PageChrome.Apply(this, TitleKey, ShowIntro ? "credential.intro" : "", out _);
        }
        else
        {
            SetObjectName("credentialList");
            root = new QVBoxLayout(this);
            root.SetContentsMargins(24, 22, 24, 20);
            root.SetSpacing(12);
            var heading = new QLabel();
            I18n.Bind(heading.SetText, TitleKey);
            heading.SetObjectName("detailTitle");
            root.AddWidget(heading);

            if (ShowIntro)
            {
                // One sentence, so the list needs no legend: what these are, and that the secret
                // does not come back out once saved.
                var intro = new QLabel();
                I18n.Bind(intro.SetText, "credential.intro");
                intro.SetObjectName("muted");
                intro.SetWordWrap(true);
                root.AddWidget(intro);
            }
        }

        _search = new QLineEdit();
        _search.SetClearButtonEnabled(true);
        I18n.Bind(_search.SetPlaceholderText, "credential.search");
        _search.TextChanged += _ => Rebuild();
        root.AddWidget(_search);

        _list = ContinuousScrolling.List();
        _list.SetIconSize(new QSize(CredentialMarks.Size, CredentialMarks.Size));
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
        if (IsPage) return;
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
            Apply(response);
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

    /// <summary>Everything a window draws from one answer from the store: the records, the kinds a
    /// new record may be, and the layer types that say which credentials they can present.
    ///
    /// It is one method because it is one answer. A window that refreshed in place used to take
    /// only the first of the three, so the form it opened to make an account offered the two kinds
    /// built into the tray while the store knew four.</summary>
    protected void Apply(ControlResponse response)
    {
        Items = response.Credentials;
        Kinds = response.CredentialKinds;
        Consumers = response.CredentialConsumers;
        CredentialMarks.Learn(Kinds);
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
        // The row is a card, the way a password manager draws an account: the mark of what kind of
        // secret it is, the name, then what it signs in as. Clicking anywhere selects it.
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
            // A record this account cannot use keeps its shape and takes the warning ink: the ⚠ at
            // the end of the line says so in words, the mark says it where the eye lands first.
            row.SetIcon(CredentialMarks.Of(item.Kind, item.Usable ? null : TrayTheme.Tokens.Warning));
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
    /// failing, so it is counted here and listed in the tooltip.
    ///
    /// A plugin kind is named by its own title; the two built-ins keep their own words, and a
    /// password credential shows the account, which is what a reader recognises it by.</summary>
    protected string Describe(CredentialRecord item)
    {
        string identity = KindTitle(item.Kind);
        if (identity.Length == 0) identity = item.Username;
        string text = item.Name + " · " + identity;
        // Where it is used, not where it may be presented. The allowed servers are a permission
        // that stays until somebody edits it, and a row that kept saying "2 servers" after both
        // layers had been pointed at another account read as "still in use" — which is what a
        // reader switching a mount over then went looking for. The addresses stay in the tooltip,
        // where a refused mount sends them anyway.
        if (UsedBy.GetValueOrDefault(item.Id) is { Count: > 0 } usedBy)
            text += " · " + I18n.F("credential.used.count", usedBy.Count);
        return item.Usable ? text : text + "  ⚠";
    }

    /// <summary>What a kind is called in this window: the built-in names it ships with, or the
    /// title the plugin gave it. Empty for a password credential, whose row shows the account.</summary>
    protected string KindTitle(string kind)
    {
        if (string.Equals(kind, CredentialTypes.Certificate, StringComparison.OrdinalIgnoreCase))
            return I18n.T("credential.kind.certificate");
        if (string.Equals(kind, CredentialTypes.Password, StringComparison.OrdinalIgnoreCase))
            return "";
        return Kinds.FirstOrDefault(item => string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase))
                   ?.Title.Resolve(I18n.Language)
               // A record whose plugin is not loaded has no title to show, and its own name for
               // the kind is still better than an empty row.
               ?? kind;
    }

    protected void AddRow(string id, string text, string? tooltip = null, string? glyph = null,
        string? color = null)
    {
        // A plain item, not a widget: the delegate draws one line of text at the right height on
        // every style, where a widget item kept being clipped regardless of the size hint.
        var row = new QListWidgetItem(text);
        row.SetData((int)Qt.ItemDataRole.UserRole, id);
        if (!string.IsNullOrEmpty(tooltip)) row.SetToolTip(tooltip);
        // A row that is not a credential still carries a mark's worth of empty space, so its text
        // starts in the same column as the rows below it.
        row.SetIcon(glyph is { Length: > 0 } ? TrayIcons.Get(glyph, color) : CredentialMarks.Blank);
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
        using var form = new CredentialForm(this, existing, Items, Kinds);
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
    internal void ShowItemsForCheck(List<CredentialRecord> items, Dictionary<string, int> usage,
        List<CredentialKindDescriptor>? kinds = null)
    {
        Items = items;
        Kinds = kinds ?? new List<CredentialKindDescriptor>();
        CredentialMarks.Learn(Kinds);
        UsedBy.Clear();
        foreach (var (key, value) in usage)
            UsedBy[key] = Enumerable.Range(1, value).Select(n => "instance / layer " + n).ToList();
        Rebuild();
    }
}

/// <summary>
/// The mark a credential wears in front of its name: what kind of secret it is, drawn in the same
/// line icons as the rest of the tray. The list used to say it only in words, and for an account
/// with a password the words are the account's own name — so a certificate, a cached Subversion
/// password and a single sign-on token all read as the same kind of thing until one was opened.
///
/// The two kinds the product ships have a picture of their own. A kind a plugin described names its
/// own (<see cref="CredentialKindDescriptor.Icon"/>) — that is how the Subversion cache credential
/// comes to wear a tortoise — and a kind that names none, or names something this set does not
/// draw, wears the generic pass. The names live here rather than in each window because the window
/// that shows a record is often not the window that read the kinds: the editor's credential field
/// shows an account it was handed, not a list it asked for.
/// </summary>
internal static class CredentialMarks
{
    /// <summary>The box a mark is drawn in, which is the list's icon size as well: a blank mark
    /// that is laid out at another size would not hold a row's text in the same column as its
    /// neighbours.</summary>
    internal const int Size = 18;

    /// <summary>The mark each plugin kind names, as the service last reported it.</summary>
    private static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Takes the kinds from an answer from the store — what a window does whenever it
    /// reads the credentials, because the records and the kinds they are described by are one
    /// answer.</summary>
    internal static void Learn(IReadOnlyList<CredentialKindDescriptor>? kinds)
    {
        if (kinds == null) return;
        foreach (var descriptor in kinds)
            if (!string.IsNullOrWhiteSpace(descriptor.Kind))
                Named[descriptor.Kind] = descriptor.Icon ?? "";
    }

    /// <summary>The glyph a kind is drawn with.</summary>
    internal static string GlyphOf(string kind) =>
        Named.TryGetValue(kind, out var named) && named.Length > 0
            // A kind that named a glyph the set does not draw gets the generic pass from the
            // caller of TrayIcons, not a blank: a badge is a summary.
            ? (TrayIcons.Names.Contains(named, StringComparer.OrdinalIgnoreCase) ? named : "credential")
            : string.Equals(kind, CredentialTypes.Certificate, StringComparison.OrdinalIgnoreCase) ? "certificate"
            : string.Equals(kind, CredentialTypes.Password, StringComparison.OrdinalIgnoreCase) ? "key"
            : "credential";

    /// <summary>The mark itself, in the scheme's ink unless a colour is given: a record this
    /// account cannot use is drawn in the warning one.</summary>
    internal static QIcon Of(string kind, string? color = null) => TrayIcons.Get(GlyphOf(kind), color);

    /// <summary>An empty mark, for a row that is not a credential — "none" in a picker, which
    /// still has to start its text where the credentials do.</summary>
    internal static QIcon Blank => _blank ??= Empty();

    /// <summary>Puts the mark on the field that names a credential: the row in a layer's settings
    /// that says which account its source uses. The field and the picker that fills it then wear
    /// the same mark for the same record — choosing an account and then reading the row back is one
    /// gesture, and it should not have to be recognised twice.
    ///
    /// A field naming nothing, or naming a record the store no longer holds, wears the empty mark
    /// rather than none at all: a field whose text starts somewhere else after a choice is a form
    /// that moves while it is being filled in.</summary>
    internal static void Show(QPushButton field, CredentialRecord? record)
    {
        field.SetIconSize(new QSize(Size, Size));
        field.SetIcon(record is null ? Blank : Of(record.Kind, record.Usable ? null : TrayTheme.Tokens.Warning));
    }

    private static QIcon? _blank;
    private static QIcon Empty()
    {
        using var image = new QImage(Size, Size, QImage.Format.Format_ARGB32_Premultiplied);
        image.Fill(0u);
        using var pixmap = QPixmap.FromImage(image);
        return new QIcon(pixmap);
    }
}

/// <summary>The store's own window: every credential, with create, edit and delete.</summary>
internal sealed class CredentialManager : CredentialListDialog
{
    public CredentialManager(QWidget? parent, bool load = true, bool page = false) : base(parent, load, page) { }

    /// <summary>Reads the store again. What a page does when it is opened: a sign-in can have
    /// added a credential while it was hidden.</summary>
    internal void ReloadList() => LoadAll();

    protected override string TitleKey => "credential.title";

    internal void ShowForCheck(List<CredentialRecord> items, Dictionary<string, int> usage) =>
        ShowItemsForCheck(items, usage);

    /// <summary>Builds the manager with fixtures and checks that each row is one complete line and
    /// that the actions follow the selection; a capture can be taken when one is asked for.</summary>
    internal static void Smoke(QWidget parent, string? capture = null)
    {
        // One row per shape the list draws a mark for — the two kinds the product ships, a kind that
        // names a glyph of its own, and one that names none — plus a record this account cannot use:
        // those five rows are what a capture shows, and what the checks at the end read back.
        var items = new List<CredentialRecord>
        {
            new("a", "Work SVN", "alice"),
            new("b", "Personal", "bob"),
            new("c", "Client certificate", "carol", CredentialTypes.Certificate),
            new("e", "TortoiseSVN (alice)", "alice", "svn-cached"),
            new("d", "Sign-in", "dave", "sso-check", false)
        };
        var usage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["a"] = 2 };
        var manager = new CredentialManager(parent, load: false);
        manager.Show();
        QCoreApplication.ProcessEvents();
        // The two buttons open this form; constructing it here keeps a field that cannot be
        // built from turning into a click that does nothing. It is deleted with the manager,
        // not before, so a pending event is not dispatched into a destroyed wrapper.
        var form = new CredentialForm(manager, null, items);
        var certificate = new CredentialForm(manager,
            new CredentialRecord("c", "Client", "", CredentialTypes.Certificate), items);

        // A kind a plugin describes: the form offers it, draws its fields and hands back the
        // values under the paths the kind named — that is the whole contract between a plugin and
        // a window it was never compiled into.
        var pluginKind = new CredentialKindDescriptor
        {
            Kind = "sso-check",
            Title = new LocalizedText("Single sign-on", "单点登录"),
            AccountField = "account",
            Fields = new()
            {
                new() { Path = "account", Title = new LocalizedText("Account", "账号") },
                new() { Path = "token", Kind = CredentialFieldKind.Secret, Required = true,
                    Title = new LocalizedText("Token", "令牌") }
            }
        };
        // A kind that names its own mark: the Subversion cache credential wears the tortoise of
        // the program that saved it. This is the contract in one line — the window was never
        // compiled with either the kind or its glyph.
        var tortoiseKind = new CredentialKindDescriptor
        {
            Kind = "svn-cached",
            Title = new LocalizedText("Subversion cache", "Subversion 缓存"),
            Icon = "tortoise"
        };
        var kinds = new List<CredentialKindDescriptor> { pluginKind, tortoiseKind };
        manager.ShowItemsForCheck(items, usage, kinds);
        var plugin = new CredentialForm(manager, null, items, kinds);
        plugin.SelectKind("sso-check");
        QCoreApplication.ProcessEvents();
        if (plugin.Kind != "sso-check")
            throw new InvalidOperationException("a plugin credential form reports kind '" + plugin.Kind + "'");
        var values = plugin.Values();
        if (!values.ContainsKey("account") || !values.ContainsKey("token"))
            throw new InvalidOperationException(
                "a plugin kind's fields are not on the form: " + string.Join(", ", values.Keys));
        if (plugin.ToPayload() is not { Values.Count: 2 } pluginPayload)
            throw new InvalidOperationException("a plugin kind's values do not reach the save");
        if (pluginPayload.Values!["token"] is not { Length: 0 })
            throw new InvalidOperationException("a plugin kind's token field arrives with a value");

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
        plugin.Hide();
        certificate.Hide();
        // The form is a window of its own, and it was never in the picture set: a label that sits in
        // another column than the field beside it is not visible from the list behind it. All three
        // shapes are checked, because which rows a kind shows is what changes the form's columns.
        foreach (var (window, name) in new[] { (form, "form"), (certificate, "form-certificate"), (plugin, "form-plugin") })
        {
            window.Show();
            QCoreApplication.ProcessEvents();
            if (capture != null)
            {
                using var image = window.Grab();
                image.Save(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(capture) ?? "",
                    System.IO.Path.GetFileNameWithoutExtension(capture) + "-" + name + ".png"));
            }
            FormColumns.Verify(window.Rows);
            window.Hide();
        }
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
                // Every row is a kind, and every kind is drawn: a row without a mark is the list
                // this check exists to keep from coming back.
                if (item.Icon().IsNull())
                    throw new InvalidOperationException($"Credential manager row {i} has no mark");
                int height = manager._list.VisualItemRect(item).Height();
                if (height < line)
                    throw new InvalidOperationException(
                        $"Credential manager row {i} is {height}px tall, shorter than its {line}px line");
            }
            // Two kinds that drew the same picture would leave a reader where they were before the
            // marks existed. The four the fixtures cover are the four shapes: an account, a
            // certificate, a kind that named its glyph (the tortoise), and one that named none.
            var kinds4 = new[] { CredentialTypes.Password, CredentialTypes.Certificate, "svn-cached", "sso-check" };
            var marks = kinds4.Select(CredentialMarks.GlyphOf).Distinct(StringComparer.Ordinal).Count();
            if (marks != kinds4.Length)
                throw new InvalidOperationException(
                    $"The credential kinds are drawn with {marks} marks, expected {kinds4.Length}");
            if (CredentialMarks.GlyphOf("svn-cached") != "tortoise")
                throw new InvalidOperationException(
                    "A kind that names a glyph does not wear it: " + CredentialMarks.GlyphOf("svn-cached"));
            foreach (var window in new[] { form, certificate, plugin })
                if (!window.KindPickerHasMarks())
                    throw new InvalidOperationException("A credential form's kind picker draws a kind without its mark");
            manager._list.SetCurrentRow(0);
            QCoreApplication.ProcessEvents();
            if (!manager._edit.IsEnabled() || !manager._remove.IsEnabled())
                throw new InvalidOperationException("A selected credential cannot be edited or removed");
            if (capture != null) { using var image = manager.Grab(); image.Save(capture); }
        }
        finally { shared.DeleteLater(); plugin.DeleteLater(); form.DeleteLater(); certificate.DeleteLater(); manager.Hide(); manager.DeleteLater(); }
    }
}

/// <summary>Name, user, and a password that is only sent when it is typed. A plugin kind adds its
/// own fields below the kind picker, drawn from its descriptor; the two the product ships keep the
/// rows they have always had.</summary>
internal sealed class CredentialForm : QDialog
{
    private readonly QComboBox _kind;
    private readonly List<string> _kindIds = new();
    private readonly Dictionary<string, CredentialKindDescriptor> _descriptors = new(StringComparer.OrdinalIgnoreCase);
    private readonly QLineEdit _name, _username, _password, _origins, _certificatePath, _certificatePassword;
    /// <summary>The one layout every row lives in, and the rows that come and go with the kind.
    ///
    /// The account, password and certificate rows used to be blocks with form layouts of their
    /// own, added to this form as spanning rows — which is how their fields ended up in a column of
    /// their own, 46 pixels left of the rest (measured 2026-10-29: x=78 against x=124). A nested
    /// form sizes its columns to its own labels, so the only way to keep one column is one form;
    /// a row that does not apply to the chosen kind is hidden by row index instead.</summary>
    private readonly QFormLayout _form;
    private readonly List<(string Title, QWidget Field)> _rows = new();
    private int _usernameRow, _passwordRow, _certificateFileRow, _certificatePasswordRow;
    private readonly Dictionary<string, List<int>> _kindRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, QWidget>> _kindEditors = new(StringComparer.OrdinalIgnoreCase);
    private readonly QLabel _error;
    private readonly bool _isNew;
    private readonly string _originalKind;
    private readonly List<string> _takenNames;
    private byte[]? _certificate;

    public string CredentialName => _name.Text.Trim();
    public string Kind => _kind.CurrentIndex >= 0 && _kind.CurrentIndex < _kindIds.Count
        ? _kindIds[_kind.CurrentIndex] : CredentialTypes.Password;
    public string Username => _username.Text.Trim();
    public string? Password => _password.Text.Length > 0 ? _password.Text : null;
    /// <summary>The file chosen in this form, or null to keep the one the record holds.</summary>
    public byte[]? Certificate => _certificate;
    public string? CertificatePassword => _certificatePassword.Text.Length > 0 ? _certificatePassword.Text : null;

    public CredentialForm(QWidget? parent, CredentialRecord? existing, IReadOnlyList<CredentialRecord> all,
        IReadOnlyList<CredentialKindDescriptor>? kinds = null) : base(parent)
    {
        SetWindowTitle(I18n.T(existing == null ? "credential.add.title" : "credential.edit.title"));
        SetWindowIcon(TrayIcons.App);
        SetMinimumWidth(460);
        CredentialMarks.Learn(kinds);
        _takenNames = all.Where(item => item.Id != existing?.Id).Select(item => item.Name).ToList();
        _isNew = existing == null;
        _originalKind = existing?.Kind ?? CredentialTypes.Password;

        var root = new QVBoxLayout(this);
        root.SetContentsMargins(24, 22, 24, 20);
        root.SetSpacing(12);

        // What the secret is comes first: a certificate credential is not an account with a
        // different password, and the fields below it follow from the answer. The kinds after the
        // two the product ships come from the plugins the service has loaded — a window built
        // before a plugin existed still offers it, because the list is not compiled in.
        _kind = ContinuousScrolling.Combo();
        _kind.SetIconSize(new QSize(CredentialMarks.Size, CredentialMarks.Size));
        _kindIds.Add(CredentialTypes.Password);
        _kind.AddItem(CredentialMarks.Of(CredentialTypes.Password), I18n.T("credential.kind.password"));
        _kindIds.Add(CredentialTypes.Certificate);
        _kind.AddItem(CredentialMarks.Of(CredentialTypes.Certificate), I18n.T("credential.kind.certificate"));
        foreach (var descriptor in kinds ?? Array.Empty<CredentialKindDescriptor>())
        {
            string kind = descriptor.Kind ?? "";
            if (CredentialKinds.IsBuiltIn(kind) || string.IsNullOrWhiteSpace(kind) || _descriptors.ContainsKey(kind))
                continue;
            _descriptors[kind] = descriptor;
            _kindIds.Add(kind);
            _kind.AddItem(CredentialMarks.Of(kind), descriptor.Title.Resolve(I18n.Language));
        }
        int original = _kindIds.FindIndex(kind => string.Equals(kind, _originalKind, StringComparison.OrdinalIgnoreCase));
        _kind.SetCurrentIndex(original >= 0 ? original : 0);

        _form = new QFormLayout();
        _form.SetRowWrapPolicy(QFormLayout.RowWrapPolicy.DontWrapRows);
        _form.SetFieldGrowthPolicy(QFormLayout.FieldGrowthPolicy.AllNonFixedFieldsGrow);

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
        // Every row goes through here, so the form's own checker can ask where a field ended up:
        // "the labels are one column and the fields the next" is otherwise only visible by eye.
        void Add(QLabel label, string title, QWidget field)
        {
            _form.AddRow(label, field);
            _rows.Add((title, field));
        }
        void Row(string key, QWidget field) => Add(Label(key), I18n.T(key), field);

        Row("credential.kind", _kind);
        Row("credential.name", _name);

        // The account and the password belong to the two kinds the product ships. A plugin kind
        // brings its own fields instead, so these rows step aside rather than asking about an
        // account the kind may not have — as rows of this one form, hidden by index, and not as
        // blocks with forms of their own (see _form).
        _usernameRow = _form.RowCount();
        Row("credential.username", _username);
        Row("credential.origins", _origins);
        _passwordRow = _form.RowCount();
        Row("credential.password", Reveal(_password));

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
        _certificateFileRow = _form.RowCount();
        Row("credential.certificate", fileRow);

        _certificatePassword = new QLineEdit();
        _certificatePassword.SetEchoMode(QLineEdit.EchoMode.Password);
        _certificatePassword.SetPlaceholderText(_isNew || _originalKind != CredentialTypes.Certificate
            ? "" : I18n.T("credential.password.keep"));
        _certificatePasswordRow = _form.RowCount();
        Row("credential.certificate.password", Reveal(_certificatePassword));

        // One row per field of every plugin kind, built now and shown when that kind is chosen: the
        // form is a single window for every kind, the way it has always been for the two built-ins.
        // The fields are described by the kind, so this window draws a kind it was never compiled
        // with — a plugin added after this binary shipped still gets a form that looks native, in
        // the same two columns as everything above it.
        foreach (var descriptor in _descriptors.Values)
        {
            var rows = new List<int>();
            var editors = new Dictionary<string, QWidget>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in descriptor.Fields)
            {
                var title = new QLabel();
                title.SetText(field.Title.Resolve(I18n.Language));
                if (field.Description.Values.Count > 0)
                    title.SetToolTip(field.Description.Resolve(I18n.Language));
                if (field.Kind == CredentialFieldKind.Boolean)
                {
                    var check = new QCheckBox();
                    check.SetChecked(string.Equals(field.DefaultValue, "true", StringComparison.OrdinalIgnoreCase));
                    editors[field.Path] = check;
                    rows.Add(_form.RowCount());
                    Add(title, field.Title.Resolve(I18n.Language), check);
                    continue;
                }
                var line = new QLineEdit();
                if (field.Kind == CredentialFieldKind.Secret)
                    line.SetEchoMode(QLineEdit.EchoMode.Password);
                line.SetPlaceholderText(field.Placeholder.Resolve(I18n.Language));
                // Expanded here rather than in the service: a default like %USERNAME% means the
                // person filling the form, and only this process knows who that is.
                if (field.DefaultValue is { } value) line.SetText(Environment.ExpandEnvironmentVariables(value));
                editors[field.Path] = line;
                rows.Add(_form.RowCount());
                Add(title, field.Title.Resolve(I18n.Language), field.Kind == CredentialFieldKind.Secret ? Reveal(line) : line);
            }
            _kindRows[descriptor.Kind] = rows;
            _kindEditors[descriptor.Kind] = editors;
        }
        root.AddLayout(_form);

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
    internal void SelectKind(string kind)
    {
        int index = _kindIds.FindIndex(item => string.Equals(item, kind, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _kind.SetCurrentIndex(index);
    }

    /// <summary>Whether the kind picker draws each kind's mark beside its name. The list and the
    /// form are the two places a kind is read, and the form is where a reader picks one — a name
    /// without its mark would leave the two disagreeing about what a certificate looks like.</summary>
    internal bool KindPickerHasMarks()
    {
        if (_kind.Count == 0) return false;
        for (int i = 0; i < _kind.Count; i++)
            if (_kind.ItemIcon(i) is not { } icon || icon.IsNull()) return false;
        return true;
    }

    /// <summary>Whether the kind is one a plugin described, which is what decides whether the
    /// built-in account and password rows apply at all.</summary>
    private bool IsPluginKind => _kindEditors.ContainsKey(Kind);

    /// <summary>The values of the selected plugin kind's fields, keyed by their paths. Empty for
    /// the two kinds the product ships, whose values travel in their own fields.</summary>
    internal Dictionary<string, string> Values()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!_kindEditors.TryGetValue(Kind, out var editors)) return values;
        foreach (var pair in editors) values[pair.Key] = ValueOf(pair.Value);
        return values;
    }

    private static string ValueOf(QWidget editor) => editor switch
    {
        QCheckBox check => check.IsChecked() ? "true" : "false",
        QLineEdit line => line.Text,
        _ => ""
    };

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
        Origins = Origins(),
        // A plugin kind's values go beside the built-in fields rather than instead of them: the
        // store reads the ones the kind has, and ignores the rest.
        Values = IsPluginKind ? Values() : null
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
        bool plugin = IsPluginKind;
        bool certificate = string.Equals(Kind, CredentialTypes.Certificate, StringComparison.OrdinalIgnoreCase);
        // The account and the password belong to the kinds the product ships; the certificate is
        // the one of those that also needs a file, and it may still name an account.
        _form.SetRowVisible(_usernameRow, !plugin);
        _form.SetRowVisible(_passwordRow, !plugin);
        _form.SetRowVisible(_certificateFileRow, !plugin && certificate);
        _form.SetRowVisible(_certificatePasswordRow, !plugin && certificate);
        // A plugin kind's own rows are hidden until that kind is the chosen one. Hidden rather
        // than removed: a row's index is what says where it is when the kind changes back.
        foreach (var pair in _kindRows)
            HideKind(pair.Key, !(plugin && string.Equals(pair.Key, Kind, StringComparison.OrdinalIgnoreCase)));
        _error.SetVisible(false);
    }

    /// <summary>Every row of the form in the order it was added, with the field it holds. The smoke
    /// uses it to check that all of them start in one column (see <see cref="FormColumns"/>).</summary>
    internal IReadOnlyList<(string Title, QWidget Field)> Rows => _rows;

    private void HideKind(string kind, bool hidden)
    {
        if (!_kindRows.TryGetValue(kind, out var rows)) return;
        foreach (int row in rows) _form.SetRowVisible(row, !hidden);
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
        else if (_kindEditors.TryGetValue(Kind, out var editors))
        {
            // A plugin kind says which of its fields it cannot do without. A required field left
            // empty on an edit is not empty: the form never shows a stored secret back, so it
            // cannot resend it either — the same rule the password field lives by.
            bool keeps = !_isNew && string.Equals(_originalKind, Kind, StringComparison.OrdinalIgnoreCase);
            if (!keeps)
                foreach (var field in _descriptors[Kind].Fields)
                    if (field.Required && ValueOf(editors[field.Path]).Length == 0)
                    {
                        Explain(I18n.F("credential.field.required", field.Title.Resolve(I18n.Language)));
                        return;
                    }
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
