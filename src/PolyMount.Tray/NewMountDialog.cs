using PolyMount.Core;
using PolyMount.Core.Ipc;
using QtSharp.QtCore;
using QtSharp.QtGui;
using QtSharp.QtWidgets;

namespace PolyMount.Tray;

/// <summary>Creating an instance, in the two decisions it actually takes: what it mounts, and the
/// few values that choice cannot know.
///
/// The first page is a gallery of presets rather than an empty layer stack, because "what do I do
/// now?" is the question an empty stack asks and a preset answers: each card says what it connects
/// to and which values it will want. The second page asks only those values — the source's
/// address and account — and derives the rest (a name from the address, a place to mount) so a
/// first instance is a matter of pasting a repository URL. Everything else is the preset's
/// default, and the editor remains where the whole chain is adjusted; a reader who would rather
/// build one themselves takes the last card, which is the old path.
///
/// Nothing is written until the commit button, and what is written is one profile appended to the
/// configuration the service holds now — read at that moment, so an instance added in another
/// window meanwhile is kept rather than overwritten.</summary>
internal sealed class NewMountDialog : QDialog
{
    private readonly QLabel _step, _subtitle, _status;
    private readonly QWidget _galleryPage, _formPage;
    private readonly QVBoxLayout _formPageLayout;
    private readonly QListWidget _cards;
    private readonly QPushButton _back, _cancel, _next;
    private readonly QTimer _timer;
    private readonly List<(LayerPreset? Preset, QListWidgetItem Item, QWidget Card, QLabel Mark)> _choices = new();
    private readonly List<QLabel> _errors = new();

    private AppConfig _config;
    private List<LayerDescriptor> _descriptors;
    private List<string> _sourceTypes;
    private List<LayerPreset> _presets;
    private List<CredentialRecord> _credentials = new();

    private LayerPreset? _selected;
    private LayerDescriptor? _source;
    private string _sourceType = "";
    private List<string> _consumers = new();
    private string? _loadError;
    private QWidget? _formBody;
    private LayerPreset? _formPreset;
    private QLineEdit? _name, _location, _driver;
    private QLabel? _nameError, _locationError;
    private readonly List<(LayerFieldDescriptor Field, QLineEdit Edit, QLabel Error)> _values = new();
    private readonly Dictionary<string, (QPushButton Button, QLabel Error, LayerFieldDescriptor Field)> _credentialRows = new();
    private readonly Dictionary<QPushButton, string> _credentialValues = new();
    // What asks a source whether the account beside it works, and where its answer is written.
    private readonly Dictionary<QPushButton, (QPushButton Ask, QLabel Answer)> _credentialChecks = new();
    private readonly List<(QWidget Editor, QLabel Error, string Message)> _problems = new();
    private bool _nameTyped, _locationTyped, _filling, _form, _saving, _loading;
    private Task<ControlResponse>? _pending;
    private Task<ControlResponse>? _check;
    private QPushButton? _checkTarget;
    private MountProfile? _profile;
    private string _root = NewMountDraft.DefaultRoot;

    /// <summary>Whether the last card was taken — the reader asking for the editor instead of a
    /// preset. The window that opened this one then hands over to it.</summary>
    internal bool Custom { get; private set; }

    /// <summary>The instance that was written, once it has been.</summary>
    internal string? Created { get; private set; }

    private static string T(string key) => I18n.T(key);

    public NewMountDialog(QWidget? parent) : this(parent, new AppConfig(), new(), new(), new(), load: true) { }

    private NewMountDialog(QWidget? parent, AppConfig config, List<LayerPreset> presets,
        List<LayerDescriptor> descriptors, List<string> sourceTypes, bool load) : base(parent)
    {
        _config = config;
        _presets = presets;
        _descriptors = descriptors;
        _sourceTypes = sourceTypes;
        SetObjectName("newMount");
        SetWindowIcon(TrayIcons.App);
        I18n.Bind(SetWindowTitle, "mount.new");
        Resize(760, 620);
        SetMinimumSize(620, 480);

        var root = new QVBoxLayout(this);
        root.SetContentsMargins(24, 22, 24, 20);
        root.SetSpacing(10);
        var heading = new QLabel();
        I18n.Bind(heading.SetText, "mount.new");
        heading.SetObjectName("detailTitle");
        root.AddWidget(heading);
        // Where this page sits, in words rather than a progress ornament: two steps is not a
        // journey that needs one, and the numbering is read at a glance.
        _step = new QLabel();
        _step.SetObjectName("eyebrow");
        root.AddWidget(_step);
        _subtitle = new QLabel();
        _subtitle.SetObjectName("muted");
        _subtitle.SetWordWrap(true);
        root.AddWidget(_subtitle);

        _galleryPage = new QWidget();
        var gallery = new QVBoxLayout(_galleryPage);
        gallery.SetContentsMargins(0, 6, 0, 0);
        gallery.SetSpacing(10);
        _cards = ContinuousScrolling.List();
        _cards.SetObjectName("layerCatalog");
        _cards.SetSpacing(6);
        _cards.SetSelectionMode(QAbstractItemView.SelectionMode.SingleSelection);
        _cards.SetHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff);
        _cards.CurrentRowChanged += _ => SelectionChanged();
        _cards.ItemActivated += _ => Advance();
        gallery.AddWidget(_cards, 1);
        root.AddWidget(_galleryPage, 1);

        _formPage = new QWidget();
        _formPageLayout = new QVBoxLayout(_formPage);
        _formPageLayout.SetContentsMargins(0, 6, 0, 0);
        _formPageLayout.SetSpacing(0);
        _formPage.Hide();
        root.AddWidget(_formPage, 1);

        _status = new QLabel();
        _status.SetWordWrap(true);
        _status.SetVisible(false);
        root.AddWidget(_status);

        var footer = new QHBoxLayout();
        _back = new QPushButton();
        I18n.Bind(_back.SetText, "newmount.back");
        _back.Clicked += _ => ShowStep(form: false);
        footer.AddWidget(_back);
        footer.AddStretch(1);
        _cancel = new QPushButton();
        I18n.Bind(_cancel.SetText, "cancel");
        _cancel.Clicked += _ => Reject();
        footer.AddWidget(_cancel);
        _next = new QPushButton();
        _next.SetObjectName("primary");
        _next.SetDefault(true);
        _next.Clicked += _ => Advance();
        footer.AddWidget(_next);
        root.AddLayout(footer);

        _timer = new QTimer(this);
        _timer.Timeout += Complete;

        ShowStep(form: false);
        if (load) Load();
        else Ready();
    }

    private void Load()
    {
        _loading = true;
        UpdateSubtitle();
        UpdateButtons();
        _timer.Start(100);
        _pending = Task.Run(() =>
        {
            var response = SupervisorClient.Default.GetConfig();
            // The accounts every credential row offers. A service that cannot answer leaves the
            // list empty rather than failing the whole window.
            try
            {
                var credentials = SupervisorClient.Default.Credentials();
                _credentials = credentials.Credentials;
                CredentialMarks.Learn(credentials.CredentialKinds);
            }
            catch (Exception) { _credentials = new(); }
            return response;
        });
    }

    private void Ready()
    {
        _loading = false;
        BuildGallery();
        ShowStep(form: false);
        UpdateButtons();
    }

    private void Complete()
    {
        // The check and the commit are different questions and can both be in flight; each is
        // answered where it was asked, and the tick keeps running until neither is.
        if (_check is { IsCompleted: true } check)
        {
            var target = _checkTarget;
            _check = null;
            _checkTarget = null;
            FinishCheck(check, target);
        }
        if (_pending is { IsCompleted: true } task)
        {
            _pending = null;
            // The tick stops here rather than at the end: the load answers by returning, and a
            // timer that outlived its work would go on asking an idle page every 100 ms.
            if (_check == null) _timer.Stop();
            try
            {
                var response = task.GetAwaiter().GetResult();
                if (!response.Ok) throw new InvalidOperationException(response.Error);
                if (_loading)
                {
                    if (response.Config == null) throw new InvalidOperationException("Empty configuration");
                    _config = AppConfig.FromJson(response.Config);
                    _descriptors = response.LayerDescriptors;
                    _presets = response.Presets;
                    _sourceTypes = response.SourceTypes;
                    Ready();
                    return;
                }
                // The service accepted the configuration and is starting the instance.
                if (_profile is { } profile)
                {
                    string? parent = Path.GetDirectoryName(profile.MountPoint);
                    if (!string.IsNullOrWhiteSpace(parent)) UiSettings.Set("MountRoot", parent);
                    Created = profile.Name;
                }
                Accept();
            }
            catch (Exception ex)
            {
                bool loading = _loading;
                _loading = false;
                _saving = false;
                if (loading) _loadError = I18n.F("newmount.loadFailed", ex.Message);
                else ShowStatus(I18n.F("newmount.createFailed", ex.Message), true);
                UpdateSubtitle();
                UpdateButtons();
            }
        }
        if (_pending == null && _check == null) _timer.Stop();
    }

    /// <summary>The answer to "does this account work here?", written under the row that asked.
    /// A check that never reached the service is a sentence of its own rather than an exception:
    /// the question was optional, and the page has to be usable either way.</summary>
    private void FinishCheck(Task<ControlResponse> task, QPushButton? target)
    {
        if (target == null) return;
        SourceCheckResult result;
        try
        {
            var response = task.GetAwaiter().GetResult();
            result = response.Check ?? new SourceCheckResult(false, SourceCheckKinds.Unreachable,
                response.Error ?? I18n.T("credential.check.noAnswer"), 0, 0);
        }
        catch (Exception ex)
        {
            result = new SourceCheckResult(false, SourceCheckKinds.Unreachable, ex.Message, 0, 0);
        }
        ShowCheck(target, result);
    }

    /// <summary>The first page: one card per preset, and last the way to the editor for a reader
    /// who wants to build the stack by hand. Each card says what it will ask for, because that is
    /// what the next page does and what a reader choosing between them wants to know.</summary>
    private void BuildGallery()
    {
        _choices.Clear();
        foreach (var preset in _presets)
        {
            var descriptor = NewMountDraft.SourceDescriptor(preset, _descriptors, _sourceTypes);
            var (glyph, color) = Mark(preset);
            AddCard(preset,
                preset.Titles.GetValueOrDefault(I18n.Language) ?? preset.Titles.GetValueOrDefault("en") ?? preset.Id,
                descriptor?.Description.Resolve(I18n.Language) ?? "",
                Needs(NewMountDraft.Essentials(descriptor)), Chain(preset), glyph, color);
        }
        var (customGlyph, customColor) = Mark(null);
        AddCard(null, T("newmount.custom.title"), T("newmount.custom.description"), T("newmount.needs.none"), "",
            customGlyph, customColor);
        // The first preset is the one the dialog recommends by standing behind it: a reader who
        // agrees can press Next, and one who does not clicks another card.
        if (_cards.Count > 0) _cards.SetCurrentRow(0);
        SelectionChanged();
    }

    /// <summary>What a card is marked with: the glyph the source it mounts declares, in the colour
    /// that type is drawn in everywhere else in the window. Three presets whose only difference was
    /// a sentence of description read as one card repeated — the mark is what tells them apart
    /// before any of the words do.
    ///
    /// A preset that mounts no source is a chain and is marked as one; the card that is not a
    /// preset at all — the way to the editor — is marked in the muted ink, because a protocol's
    /// colour there would claim it mounts something.</summary>
    private (string Glyph, string Color) Mark(LayerPreset? preset)
    {
        const string fallback = "layers";
        if (preset == null) return (fallback, TrayTheme.Tokens.Muted);
        if (NewMountDraft.SourceLayer(preset.Layers, _sourceTypes) is not { } source)
            return (fallback, LayerColors.Resolve(preset.Id, null));
        string icon = NewMountDraft.Descriptor(source.Type, _descriptors)?.Icon ?? "";
        return (icon.Length > 0 ? icon : fallback, LayerColors.Resolve(source.Type, null));
    }

    private void AddCard(LayerPreset? preset, string title, string description, string needs, string chain,
        string glyph, string color)
    {
        var item = new QListWidgetItem();
        item.SetData((int)Qt.ItemDataRole.UserRole, preset?.Id ?? "");
        _cards.AddItem(item);
        var card = new QWidget();
        card.SetObjectName("presetCard");
        // The card is the row's painting, not a control of its own: clicks belong to the list, so
        // one click selects and the keyboard still works.
        card.SetAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents);
        var layout = new QHBoxLayout(card);
        layout.SetContentsMargins(14, 11, 14, 11);
        layout.SetSpacing(12);
        // The mark is laid on a wash of its own colour rather than left as a bare stroke: at this
        // size a 22-pixel line drawing on a card is a smudge, and the tint is what a reader sees
        // from across the list. The colour is the one the type has everywhere else, lifted for a
        // dark scheme the way every other drawing of it is.
        var ink = TrayTheme.Series(color);
        var mark = new QLabel();
        mark.SetObjectName("presetMark");
        mark.SetPixmap(TrayIcons.Get(glyph, ink.Name()).Pixmap(new QSize(22, 22)));
        mark.SetFixedSize(34, 34);
        // Centred, because a QLabel draws its pixmap at the alignment it was born with —
        // AlignLeft — so the glyph otherwise sits against the left edge of its own square.
        mark.SetAlignment(Qt.Alignment.AlignCenter);
        mark.SetStyleSheet("background: rgba(" + ink.Red() + "," + ink.Green() + "," + ink.Blue() +
            ",42); border-radius: 9px;");
        layout.AddWidget(mark);
        var copy = new QVBoxLayout();
        copy.SetSpacing(3);
        var head = new QLabel(title);
        head.SetObjectName("pickerCardTitle");
        copy.AddWidget(head);
        foreach (string line in new[] { description, needs })
        {
            if (line.Length == 0) continue;
            var label = new QLabel(line);
            label.SetObjectName("muted");
            label.SetWordWrap(true);
            copy.AddWidget(label);
        }
        layout.AddLayout(copy, 1);
        if (chain.Length > 0) card.SetToolTip(I18n.F("newmount.chain", chain));
        _cards.SetItemWidget(item, card);
        // A row with a widget is not sized from that widget; without a hint its height comes from
        // the item's empty text and the card is clipped.
        item.SetSizeHint(new QSize(0, Math.Max(card.SizeHint.Height(), preset == null ? 62 : 84)));
        _choices.Add((preset, item, card, mark));
    }

    private string Needs(List<LayerFieldDescriptor> fields) => fields.Count == 0
        ? T("newmount.needs.none")
        : I18n.F("newmount.needs", string.Join(I18n.Language == "en" ? ", " : "、",
            fields.Select(field => field.Title.Resolve(I18n.Language))));

    private string Chain(LayerPreset preset) => string.Join(" → ", preset.Layers
        .Select(layer => NewMountDraft.Descriptor(layer.Type, _descriptors)?.Title.Resolve(I18n.Language) ?? layer.Type));

    private void SelectionChanged()
    {
        int row = _cards.CurrentRow;
        bool chosen = row >= 0 && row < _choices.Count;
        _selected = chosen ? _choices[row].Preset : null;
        // The custom card is the one that names no preset.
        Custom = chosen && _selected == null;
        for (int i = 0; i < _choices.Count; i++)
            _choices[i].Card.SetStyleSheet(i == row
                ? "QWidget#presetCard { background: " + TrayTheme.Tokens.PrimarySoft + "; border: 1px solid " + TrayTheme.Tokens.AccentBorder + "; border-radius: 9px; }"
                : "QWidget#presetCard { background: " + TrayTheme.Tokens.Surface + "; border: 1px solid " + TrayTheme.Tokens.Border + "; border-radius: 9px; }");
        UpdateButtons();
    }

    /// <summary>What the commit button does on this page: on the gallery it is "next" — or, on the
    /// custom card, handing over to the editor; on the form it writes.</summary>
    private void Advance()
    {
        if (_loading || _saving) return;
        if (!_form)
        {
            if (_cards.CurrentRow < 0) return;
            if (Custom) { Accept(); return; }
            ShowStep(form: true);
            return;
        }
        Commit();
    }

    private void ShowStep(bool form)
    {
        _form = form;
        _galleryPage.SetVisible(!form);
        _formPage.SetVisible(form);
        _back.SetVisible(form);
        _status.SetVisible(false);
        _step.SetText(I18n.F("newmount.step", form ? 2 : 1, 2));
        UpdateSubtitle();
        // Going back to the gallery keeps what was typed: returning to the same preset must not
        // ask for the address again.
        if (form && (_formBody == null || _formPreset != _selected)) BuildForm();
        UpdateButtons();
    }

    private void UpdateSubtitle() => _subtitle.SetText(
        _loadError ?? (_loading ? T("newmount.loading")
            : _form ? T("newmount.form.subtitle")
            : _presets.Count == 0 ? T("newmount.noPresets")
            : T("newmount.preset.subtitle")));

    private void UpdateButtons()
    {
        bool idle = !_loading && !_saving;
        _next.SetText(T(_form ? (_saving ? "newmount.creating" : "newmount.create") : "newmount.next"));
        _next.SetEnabled(idle && (_form || _cards.CurrentRow >= 0));
        _back.SetEnabled(idle);
        _cancel.SetEnabled(!_saving);
    }

    /// <summary>The second page: a name and a place, which are proposed from what is typed below
    /// them, then only the values the preset cannot know.</summary>
    private void BuildForm()
    {
        if (_formBody != null)
        {
            _formPageLayout.RemoveWidget(_formBody);
            _formBody.Hide();
            _formBody.DeleteLater();
        }
        _values.Clear();
        _credentialRows.Clear();
        _credentialValues.Clear();
        _credentialChecks.Clear();
        _errors.Clear();
        _nameTyped = _locationTyped = false;
        _name = _location = _driver = null;
        _formPreset = _selected;
        _root = NewMountDraft.SuggestRoot(UiSettings.Get("MountRoot"), _config.Mounts.Select(m => m.MountPoint));

        var body = new QWidget();
        _formBody = body;
        var column = new QVBoxLayout(body);
        column.SetContentsMargins(0, 0, 0, 0);
        column.SetSpacing(12);
        var form = new QFormLayout();
        form.SetRowWrapPolicy(QFormLayout.RowWrapPolicy.DontWrapRows);
        form.SetFieldGrowthPolicy(QFormLayout.FieldGrowthPolicy.AllNonFixedFieldsGrow);
        form.SetVerticalSpacing(12);
        column.AddLayout(form);

        // The source layer of the chosen preset: the values below belong to it, and its descriptor
        // decides which ones are asked.
        var sourceLayer = NewMountDraft.SourceLayer(_selected?.Layers ?? new(), _sourceTypes);
        _sourceType = sourceLayer?.Type ?? "";
        _source = NewMountDraft.Descriptor(_sourceType, _descriptors);

        // What the reader came to do comes first — paste the address, pick the account — and the
        // name and the place below it fill themselves in from it, in the order they are read.
        foreach (var field in NewMountDraft.Essentials(_source))
        {
            var title = Muted(field.Title.Resolve(I18n.Language));
            if (field.Kind == LayerFieldKind.Credential)
            {
                var button = new QPushButton();
                button.SetObjectName("credentialButton");
                _credentialValues[button] = "";
                ShowCredential(button, field, "");
                button.Clicked += _ => ChooseCredential(button, field);
                // Beside the account, the one question a first instance cannot answer any other
                // way: whether the account the reader just picked is accepted by the address they
                // just typed. The alternative is to save the instance and read a failure on the
                // dashboard a minute later.
                var ask = new QPushButton();
                I18n.Bind(ask.SetText, "credential.check");
                ask.SetToolTip(T("credential.check.tip"));
                ask.Clicked += _ => CheckCredential(button);
                var line = new QWidget();
                var lineLayout = new QHBoxLayout(line);
                lineLayout.SetContentsMargins(0, 0, 0, 0);
                lineLayout.SetSpacing(8);
                lineLayout.AddWidget(button, 1);
                lineLayout.AddWidget(ask);
                var answer = new QLabel();
                answer.SetWordWrap(true);
                answer.SetVisible(false);
                form.AddRow(title, Field(line, out var credentialError,
                    hint: field.Description.Resolve(I18n.Language), extra: answer));
                _credentialRows[field.Path] = (button, credentialError, field);
                _credentialChecks[button] = (ask, answer);
                continue;
            }
            var edit = new QLineEdit();
            edit.SetPlaceholderText(field.Placeholder.Resolve(I18n.Language));
            edit.SetToolTip(field.Description.Resolve(I18n.Language));
            edit.TextChanged += _ => ValueChanged(edit);
            QWidget editor = edit;
            if (IsFolderSource(field))
            {
                // A folder is chosen, not typed: the source is on this machine's disk, so the
                // dialog that already knows how to pick a folder is one click away.
                var row = new QWidget();
                var rowLayout = new QHBoxLayout(row);
                rowLayout.SetContentsMargins(0, 0, 0, 0);
                rowLayout.SetSpacing(8);
                rowLayout.AddWidget(edit, 1);
                var browse = new QPushButton();
                I18n.Bind(browse.SetText, "newmount.browse");
                browse.Clicked += _ => ChooseSourceFolder(edit);
                rowLayout.AddWidget(browse);
                editor = row;
            }
            form.AddRow(title, Field(editor, out var error));
            _values.Add((field, edit, error));
            // The first required value is what names the instance: the address is typed there, and
            // the name and the place below it follow until the reader takes them over.
            if (_driver == null && field.Required) _driver = edit;
        }

        _name = new QLineEdit();
        _name.SetText(FreeName(NewMountDraft.NameFromSource(_values.Count > 0 ? _values[0].Edit.Text : "")));
        _name.TextChanged += _ => NameChanged();
        form.AddRow(MutedKey("newmount.name"), Field(_name, out _nameError));

        _location = new QLineEdit();
        _location.SetText(NewMountDraft.SuggestMountPoint(_root, _name.Text, TakenPoints()));
        _location.TextChanged += _ => { if (!_filling) _locationTyped = true; };
        var locationRow = new QWidget();
        var locationLayout = new QHBoxLayout(locationRow);
        locationLayout.SetContentsMargins(0, 0, 0, 0);
        locationLayout.SetSpacing(8);
        locationLayout.AddWidget(_location, 1);
        var browseLocation = new QPushButton();
        I18n.Bind(browseLocation.SetText, "newmount.browse");
        browseLocation.Clicked += _ => ChooseMountPoint(_location);
        locationLayout.AddWidget(browseLocation);
        form.AddRow(MutedKey("newmount.location"), Field(locationRow, out _locationError));

        if (_source?.Description.Resolve(I18n.Language) is { Length: > 0 } description)
        {
            var note = new QLabel(description);
            note.SetObjectName("muted");
            note.SetWordWrap(true);
            column.AddWidget(note);
        }
        column.AddStretch(1);
        _formPageLayout.AddWidget(body);
        (_driver ?? _name)?.SetFocus();
    }

    private bool IsFolderSource(LayerFieldDescriptor field) =>
        string.Equals(_sourceType, "dir", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(field.Path, "Source.Path", StringComparison.OrdinalIgnoreCase);

    private void ChooseSourceFolder(QLineEdit edit)
    {
        string start = edit.Text.Trim();
        if (start.Length == 0 || !Directory.Exists(start)) start = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? chosen = QFileDialog.GetExistingDirectory(this, T("newmount.source.folder"), start);
        if (!string.IsNullOrWhiteSpace(chosen)) edit.SetText(chosen);
    }

    /// <summary>A label for a catalog key, bound so that a language change reaches it.</summary>
    private static QLabel MutedKey(string key)
    {
        var label = new QLabel();
        I18n.Bind(label.SetText, key);
        label.SetObjectName("muted");
        return label;
    }

    /// <summary>A label for text that is already resolved — a field's title, which came from the
    /// descriptor in the reader's language. Binding it would send it back through the catalog,
    /// where a title that happened to match a key would become another window's words.</summary>
    private static QLabel Muted(string text)
    {
        var label = new QLabel(text);
        label.SetObjectName("muted");
        return label;
    }

    /// <summary>An editor and the line that says what is wrong with it, which is where the reader
    /// is looking rather than at the bottom of the window. <paramref name="extra"/> is a line of
    /// the field's own — today, what a source said about the account — and sits above the error so
    /// that a refusal from the source and a refusal from this page do not share one line.</summary>
    private QWidget Field(QWidget editor, out QLabel error, string? hint = null, QLabel? extra = null)
    {
        var column = new QWidget();
        var layout = new QVBoxLayout(column);
        layout.SetContentsMargins(0, 0, 0, 0);
        layout.SetSpacing(3);
        layout.AddWidget(editor);
        // A hint stays visible where the editor says nothing: an empty credential row is a choice
        // ("the source supplies the account"), and that is not visible in the word it shows.
        if (!string.IsNullOrWhiteSpace(hint))
        {
            var note = new QLabel(hint);
            note.SetObjectName("muted");
            note.SetWordWrap(true);
            layout.AddWidget(note);
        }
        if (extra != null) layout.AddWidget(extra);
        error = new QLabel();
        error.SetObjectName("fieldError");
        error.SetWordWrap(true);
        error.SetVisible(false);
        layout.AddWidget(error);
        _errors.Add(error);
        return column;
    }

    private void ValueChanged(QLineEdit edit)
    {
        if (_filling) return;
        if (_driver == null || edit.Handle != _driver.Handle) return;
        // An answer was about one address; another address is another question.
        ClearChecks();
        if (!_nameTyped) SetName(NewMountDraft.NameFromSource(edit.Text));
        else if (!_locationTyped) SetLocation(NewMountDraft.SuggestMountPoint(_root, _name?.Text ?? "", TakenPoints()));
    }

    private void NameChanged()
    {
        if (_filling) return;
        _nameTyped = true;
        // The place follows the name while the reader has not chosen one: "demo" becoming "demo-2"
        // to free the name must not leave the path pointing at the first one's.
        if (!_locationTyped) SetLocation(NewMountDraft.SuggestMountPoint(_root, _name?.Text ?? "", TakenPoints()));
    }

    private void SetName(string name)
    {
        if (_name == null) return;
        _filling = true;
        _name.SetText(FreeName(name));
        _filling = false;
        if (!_locationTyped) SetLocation(NewMountDraft.SuggestMountPoint(_root, _name.Text, TakenPoints()));
    }

    private void SetLocation(string path)
    {
        if (_location == null) return;
        _filling = true;
        _location.SetText(path);
        _filling = false;
    }

    /// <summary>The chosen folder is the mount point itself, not a place to put one: a reader who
    /// picks "E:\Work\demo" means to mount at E:\Work\demo, which is what every checkout dialog
    /// means by its own folder field.</summary>
    private void ChooseMountPoint(QLineEdit? location)
    {
        if (location == null) return;
        string start = location.Text.Trim();
        if (start.Length == 0 || !Directory.Exists(start)) start = _root;
        string? chosen = QFileDialog.GetExistingDirectory(this, T("newmount.location"), start);
        if (string.IsNullOrWhiteSpace(chosen)) return;
        SetLocation(chosen);
        _locationTyped = true;
    }

    /// <summary>Paints a credential field as the account it uses, the way the editor's field does:
    /// the row is the answer, and the picker behind it is where a choice — or a new account — is
    /// made.</summary>
    private void ShowCredential(QPushButton button, LayerFieldDescriptor field, string id)
    {
        var record = string.IsNullOrWhiteSpace(id)
            ? null
            : _credentials.FirstOrDefault(item => item.Id == id);
        string text = T("credential.none");
        if (!string.IsNullOrWhiteSpace(id))
            text = record == null ? T("credential.missing") : CredentialMarks.Label(record);
        else if (field.Placeholder.Resolve(I18n.Language) is { Length: > 0 } placeholder) text = placeholder;
        button.SetText(text);
        // The mark the picker drew beside the account this row stands for.
        CredentialMarks.Show(button, record);
        button.SetToolTip(field.Description.Resolve(I18n.Language));
    }

    private void ChooseCredential(QPushButton button, LayerFieldDescriptor field)
    {
        try
        {
            var response = SupervisorClient.Default.Credentials();
            if (response.Ok)
            {
                _credentials = response.Credentials;
                _consumers = response.CredentialConsumers;
                CredentialMarks.Learn(response.CredentialKinds);
            }
        }
        catch (Exception) { }
        string current = _credentialValues.GetValueOrDefault(button, "");
        ShowCredential(button, field, current);
        string defaultText = field.Placeholder.Resolve(I18n.Language);
        if (string.IsNullOrWhiteSpace(defaultText)) defaultText = T("credential.none");
        // Every field on this page belongs to the source layer, so what that layer can present is
        // the filter; a type that never said is unconstrained.
        string? consumer = _consumers.Contains(_sourceType, StringComparer.OrdinalIgnoreCase) ? _sourceType : null;
        var picker = new CredentialPicker(this, _credentials, current, defaultText, T("credential.none.hint"),
            consumer: consumer);
        try
        {
            if (picker.Exec() != 1) return;
            _credentials = picker.Credentials;
            _credentialValues[button] = picker.SelectedId;
            ShowCredential(button, field, picker.SelectedId);
            // The answer under the row was about the account that was chosen before; it is not
            // about this one, so it goes.
            ClearChecks();
        }
        finally { picker.DeleteLater(); }
    }

    /// <summary>Asks the service whether the source this page describes will serve with the account
    /// beside it. It runs off the UI thread — a source on the far end of a network answers in its
    /// own time, and a window that waited for it would look hung — and the answer is collected by
    /// the same tick that collects a save, so nothing here touches a widget after the page is gone.
    /// </summary>
    private void CheckCredential(QPushButton button)
    {
        if (_check != null || _saving || _loading) return;
        var (source, problem) = PageSource();
        if (source == null)
        {
            ShowCheck(button, new SourceCheckResult(false, SourceCheckKinds.Invalid,
                problem ?? T("credential.check.incomplete"), 0, 0));
            return;
        }
        _checkTarget = button;
        if (_credentialChecks.TryGetValue(button, out var row))
        {
            row.Ask.SetEnabled(false);
            row.Answer.SetText(T("credential.check.running"));
            row.Answer.SetStyleSheet("color: " + TrayTheme.Tokens.Muted + ";");
            row.Answer.SetVisible(true);
        }
        _check = Task.Run(() => SupervisorClient.Default.CheckSource(source));
        _timer.Start(100);
    }

    private void ClearChecks()
    {
        foreach (var (_, answer) in _credentialChecks.Values) answer.SetVisible(false);
    }

    private void ShowCheck(QPushButton button, SourceCheckResult result)
    {
        if (!_credentialChecks.TryGetValue(button, out var row)) return;
        var (text, color) = SourceCheckText.Describe(result);
        row.Ask.SetEnabled(true);
        row.Answer.SetText(text);
        row.Answer.SetStyleSheet("color: " + color + ";");
        row.Answer.SetVisible(true);
    }

    /// <summary>The source the page describes right now: the same composition the commit button
    /// writes, so what is checked is what would be saved. Null with a sentence when the page cannot
    /// describe one yet — the address is still empty, or the source's own validation refuses it.
    /// </summary>
    private (SourceConfig? Source, string? Problem) PageSource()
    {
        if (_selected == null) return (null, T("credential.check.incomplete"));
        MountProfile profile;
        try
        {
            profile = NewMountDraft.Create(_selected, _name?.Text.Trim() ?? "", _location?.Text.Trim() ?? "",
                _values.Select(value => (value.Field, value.Edit.Text))
                    .Concat(_credentialRows.Values.Select(row => (row.Field, _credentialValues.GetValueOrDefault(row.Button, "")))),
                _descriptors, _sourceTypes);
        }
        catch (ArgumentException)
        {
            // A required value is missing, and a source cannot be asked anything without it. The
            // page says which one under that field when the reader presses Create; here the
            // sentence below the account is the one that fits.
            return (null, T("credential.check.incomplete"));
        }
        if (NewMountDraft.SourceLayer(profile.GetLayers(), _sourceTypes) is not { } layer)
            return (null, T("credential.check.incomplete"));
        var source = layer.Source;
        source.Type = layer.Type;
        return (source, null);
    }

    /// <summary>Every field this page put in its form — the values the preset asked for, the
    /// accounts, the name and the place — for the column check a window's self-check makes
    /// (see <see cref="FormColumns"/>).</summary>
    internal IEnumerable<(string Title, QWidget Field)> FormRows =>
        _values.Select(value => (value.Field.Title.Resolve(I18n.Language), (QWidget)value.Edit))
            .Concat(_credentialRows.Values.Select(row => (row.Field.Title.Resolve(I18n.Language), (QWidget)row.Button)))
            .Concat(new (string, QWidget)[] { (T("newmount.name"), _name!), (T("newmount.location"), _location!) });

    private IEnumerable<string> TakenNames() => _config.Mounts.Select(m => m.Name);
    private IEnumerable<string> TakenPoints() => _config.Mounts.Select(m => m.MountPoint);
    private string FreeName(string name) => NewMountDraft.UniqueName(name, TakenNames());

    /// <summary>Checks what the page asked and composes the profile. Every problem is said under
    /// the field it belongs to, and the first one takes the keyboard: a button that answers with
    /// one sentence at the bottom of the window leaves the reader to find which of four fields it
    /// was about.</summary>
    internal bool Check()
    {
        ClearProblems();
        string name = _name?.Text.Trim() ?? "";
        string location = _location?.Text.Trim() ?? "";
        if (name.Length == 0) Problem(_name, _nameError, T("newmount.error.name"));
        else if (TakenNames().Contains(name, StringComparer.OrdinalIgnoreCase))
            Problem(_name, _nameError, I18n.F("newmount.error.name.taken", name));
        if (location.Length == 0) Problem(_location, _locationError, T("newmount.error.location"));
        else CheckLocation(location);

        foreach (var (field, edit, error) in _values)
        {
            string value = edit.Text.Trim();
            if (field.Required && value.Length == 0)
                Problem(edit, error, I18n.F("newmount.error.required", field.Title.Resolve(I18n.Language)));
            else if (value.Length > 0) CheckShape(field, value, edit, error);
        }

        if (_problems.Count > 0)
        {
            foreach (var (_, error, message) in _problems)
            {
                error.SetText(message);
                error.SetVisible(true);
            }
            _problems[0].Editor.SetFocus();
            return false;
        }
        if (_selected == null) return false;
        try
        {
            _profile = NewMountDraft.Create(_selected, name, location,
                _values.Select(value => (value.Field, value.Edit.Text))
                    .Concat(_credentialRows.Values.Select(row => (row.Field, _credentialValues.GetValueOrDefault(row.Button, "")))),
                _descriptors, _sourceTypes);
            return true;
        }
        catch (Exception ex)
        {
            ShowStatus(I18n.F("newmount.createFailed", ex.Message), true);
            return false;
        }
    }

    private void CheckLocation(string location)
    {
        // A drive letter mounts a whole volume and is not a path to examine. One already in use is
        // refused here rather than by a mount that fails a second after the button.
        if (location.Length == 2 && location[1] == ':' && char.IsAsciiLetter(location[0]))
        {
            bool inUse = DriveInfo.GetDrives().Any(drive =>
                string.Equals(drive.Name.TrimEnd('\\'), location, StringComparison.OrdinalIgnoreCase));
            if (inUse) Problem(_location, _locationError, I18n.F("newmount.error.location.drive", location));
            return;
        }
        if (!Path.IsPathFullyQualified(location))
        {
            Problem(_location, _locationError, T("newmount.error.location.relative"));
            return;
        }
        string normalized = NewMountDraft.NormalizeMountPoint(location);
        if (_config.Mounts.FirstOrDefault(mount => !string.IsNullOrWhiteSpace(mount.MountPoint) &&
            string.Equals(NewMountDraft.NormalizeMountPoint(mount.MountPoint), normalized, StringComparison.OrdinalIgnoreCase)) is { } other)
        {
            Problem(_location, _locationError, I18n.F("newmount.error.location.taken", other.Name));
            return;
        }
        // The service clears an empty folder and refuses a full one; saying so here is better than
        // a mount that fails after the button.
        try
        {
            if (Directory.Exists(location) && Directory.EnumerateFileSystemEntries(location).Any())
                Problem(_location, _locationError, T("newmount.error.location.busy"));
        }
        catch (Exception) { /* an unreadable folder is for the mount to report */ }
    }

    private void CheckShape(LayerFieldDescriptor field, string value, QLineEdit edit, QLabel error)
    {
        if (string.Equals(field.Path, "Source.Url", StringComparison.OrdinalIgnoreCase))
        {
            bool address = Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.Host.Length > 0;
            if (!address) Problem(edit, error, T("newmount.error.url"));
        }
        else if (string.Equals(field.Path, "Source.Path", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(_sourceType, "smb", StringComparison.OrdinalIgnoreCase))
            {
                if (!value.StartsWith(@"\\", StringComparison.Ordinal) || value.TrimEnd('\\').Split('\\').Length < 4)
                    Problem(edit, error, T("newmount.error.share"));
            }
            else if (!Path.IsPathFullyQualified(value) || !Directory.Exists(value))
                Problem(edit, error, T("newmount.error.folder"));
        }
    }

    private void Problem(QWidget? editor, QLabel? error, string message)
    {
        if (editor == null || error == null) return;
        _problems.Add((editor, error, message));
    }

    private void ClearProblems()
    {
        _problems.Clear();
        foreach (var error in _errors) error.SetVisible(false);
        ShowStatus("", false);
    }

    /// <summary>Writes the instance: one profile appended to the configuration the service holds
    /// at this moment. The window stays open until the answer is in, because a refusal — an
    /// address the source will not take — belongs beside the field that caused it, not on a
    /// dashboard behind a dialog that has already closed.</summary>
    private void Commit()
    {
        if (_saving || !Check() || _profile == null) return;
        _saving = true;
        UpdateButtons();
        ShowStatus(T("newmount.creating"), false);
        var profile = _profile;
        _timer.Start(100);
        _pending = Task.Run(() =>
        {
            var latest = SupervisorClient.Default.GetConfig();
            if (!latest.Ok || latest.Config == null) return latest;
            var config = AppConfig.FromJson(latest.Config);
            if (config.Mounts.Any(mount => string.Equals(mount.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(I18n.F("newmount.error.name.taken", profile.Name));
            config.Mounts.Add(profile);
            var saved = SupervisorClient.Default.SaveConfig(config.ToJson());
            if (saved.Ok) InstanceDirectory.Invalidate();
            return saved;
        });
    }

    private void ShowStatus(string text, bool error)
    {
        _status.SetText(text);
        _status.SetVisible(text.Length > 0);
        _status.SetStyleSheet(error
            ? "color: " + TrayTheme.Tokens.Danger + ";"
            : "color: " + TrayTheme.Tokens.Muted + ";");
    }

    /// <summary>The dialog driven the way a reader drives it: the gallery's cards, the second page
    /// asking only for what the preset needs, the name and the place following a pasted address,
    /// and a commit that refuses an empty name — and stops there, so the check never writes a
    /// configuration.
    ///
    /// The presets are the service's own when it is running, so the check walks the preset a reader
    /// would really be offered (SVN's address and account, a share's path); a machine with no
    /// service drives the fixtures Core holds instead, and the pages are the same either way.</summary>
    internal static void Smoke(QWidget parent, IEnumerable<LayerPreset> presets, IEnumerable<LayerDescriptor> descriptors,
        IEnumerable<string> sourceTypes, string? capture = null)
    {
        var config = new AppConfig
        {
            Mounts = new() { new() { Name = "Taken", MountPoint = @"D:\PolyMountTests\taken" } }
        };
        try
        {
            var live = SupervisorClient.Default.GetConfig();
            if (live is { Ok: true, Config: { Length: > 0 }, Presets.Count: > 0 })
            {
                config = AppConfig.FromJson(live.Config);
                presets = live.Presets;
                descriptors = live.LayerDescriptors;
                sourceTypes = live.SourceTypes;
            }
        }
        catch (Exception) { /* no service: the fixtures below are enough to drive the pages */ }
        var dialog = new NewMountDialog(parent, config, presets.ToList(), descriptors.ToList(), sourceTypes.ToList(), load: false);
        using var source = new TempFolder();
        dialog.Show();
        try
        {
            if (dialog._choices.Count < 2)
                throw new InvalidOperationException("The gallery offered no preset to choose");
            // Every card carries a mark, and the marks tell the cards apart: three presets whose
            // only difference was a sentence of description read as one card repeated, which is
            // what the marks are for.
            foreach (var choice in dialog._choices)
            {
                var pixmap = choice.Mark.Pixmap;
                if (pixmap == null) throw new InvalidOperationException("A card in the gallery carries no mark");
            }
            if (dialog._choices.Select(choice => choice.Mark.StyleSheet).Distinct().Count() <
                Math.Min(2, dialog._choices.Count))
                throw new InvalidOperationException("Every card in the gallery is marked the same way");
            // The last card is the way to the editor, not a preset.
            if (dialog._choices[^1].Preset != null)
                throw new InvalidOperationException("The gallery's last card is a preset, not the way to the editor");
            // The preset whose source is asked for by address, when this machine has one: that is
            // the page that asks the most, and it is recognised by what the descriptor declares
            // rather than by any one plugin's name.
            int row = dialog._choices.FindIndex(choice => choice.Preset != null &&
                NewMountDraft.Essentials(NewMountDraft.SourceDescriptor(choice.Preset!,
                    dialog._descriptors, dialog._sourceTypes))
                    .Any(field => string.Equals(field.Path, "Source.Url", StringComparison.OrdinalIgnoreCase)));
            dialog._cards.SetCurrentRow(row >= 0 ? row : 0);
            QCoreApplication.ProcessEvents();
            if (!dialog._next.IsEnabled())
                throw new InvalidOperationException("Choosing a preset left the next button flat");
            if (capture != null) { using var image = dialog.Grab(); image.Save(capture); }
            dialog.Advance();
            QCoreApplication.ProcessEvents();
            if (!dialog._form || dialog._name == null || dialog._location == null)
                throw new InvalidOperationException("The second page did not ask for a name and a place");
            // The two pages are the feature: the second one's picture is kept beside the first.
            if (capture != null)
            {
                using var image = dialog.Grab();
                image.Save(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(capture) ?? "",
                    System.IO.Path.GetFileNameWithoutExtension(capture) + "-form.png"));
            }
            if (dialog._source != null && dialog._driver == null)
                throw new InvalidOperationException("The second page asked nothing about the source");
            // One label column and one field column: a field that starts somewhere else is what
            // "the text and the box do not line up" looks like.
            FormColumns.Verify(dialog.FormRows);
            // The address names the instance and the place while the reader leaves both alone.
            if (dialog._driver != null)
            {
                string probe = dialog._values.FirstOrDefault(value => value.Field.Required).Field?.Path switch
                {
                    "Source.Url" => "https://svn.example.com/svn/demo/trunk",
                    _ when string.Equals(dialog._sourceType, "smb", StringComparison.OrdinalIgnoreCase) => @"\\server\share\folder",
                    _ => source.Path
                };
                dialog._driver.SetText(probe);
                QCoreApplication.ProcessEvents();
                // A name this machine already uses takes the next spelling, which is what the
                // field is asked to be rather than the bare address's last component.
                string expected = NewMountDraft.UniqueName(NewMountDraft.NameFromSource(probe),
                    dialog._config.Mounts.Select(mount => mount.Name));
                if (dialog._name.Text != expected)
                    throw new InvalidOperationException(
                        $"Pasting an address named the instance '{dialog._name.Text}', not '{expected}'");
                if (!dialog._location.Text.EndsWith(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"The mount point '{dialog._location.Text}' does not follow the name '{expected}'");
            }
            // The account is asked about where it is chosen: the button beside the row, and the
            // line under it the answer goes to. The answer is rendered from a made-up one — a
            // real check is a network round trip to somewhere this run has no business reaching —
            // and the sentence it produces is the service's own, unaltered.
            if (dialog._credentialRows.Count > 0)
            {
                var credentialButton = dialog._credentialRows.Values.First().Button;
                var credentialField = dialog._credentialRows.Values.First().Field;
                // A row that names an account wears that account's mark, the one the picker drew.
                // The picture the reader checks is the capture below; this is the fact the picture
                // would only show if it were taken.
                if (credentialButton.Icon.IsNull())
                    throw new InvalidOperationException(
                        "A credential field with no account has no mark, so its text moves when one is chosen");
                dialog._credentials.Add(new CredentialRecord("fixture", "TortoiseSVN (alice)", "alice", "svn-cached"));
                CredentialMarks.Learn(new[]
                {
                    new CredentialKindDescriptor
                    {
                        Kind = "svn-cached", Title = new LocalizedText("Subversion cache", "Subversion 缓存"),
                        Icon = "tortoise"
                    }
                });
                dialog.ShowCredential(credentialButton, credentialField, "fixture");
                QCoreApplication.ProcessEvents();
                if (capture != null)
                {
                    using var image = dialog.Grab();
                    image.Save(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(capture) ?? "",
                        System.IO.Path.GetFileNameWithoutExtension(capture) + "-credential.png"));
                }
                dialog.ShowCredential(credentialButton, credentialField, "");
                if (!dialog._credentialChecks.TryGetValue(credentialButton, out var checkRow))
                    throw new InvalidOperationException("A credential row offers no way to verify it");
                dialog.ShowCheck(credentialButton,
                    new SourceCheckResult(false, SourceCheckKinds.Auth, "the server refused this account", 0, 12));
                if (!checkRow.Answer.IsVisible() || !checkRow.Answer.Text.Contains("the server refused this account"))
                    throw new InvalidOperationException("The source's answer was not written under the credential");
                if (!checkRow.Ask.IsEnabled())
                    throw new InvalidOperationException("The verify button stayed flat after asking");
                // A page with nothing to ask says so instead of asking: an address is what a source
                // check is a question about.
                var address = dialog._values
                    .First(value => string.Equals(value.Field.Path, "Source.Url", StringComparison.OrdinalIgnoreCase));
                string kept = address.Edit.Text;
                string name = dialog._name!.Text;
                address.Edit.SetText("");
                dialog.CheckCredential(credentialButton);
                string expected = I18n.F("credential.check.error", I18n.T("credential.check.incomplete"));
                if (dialog._check != null)
                    throw new InvalidOperationException("A check with no address still went to the service");
                if (!checkRow.Answer.IsVisible() || checkRow.Answer.Text != expected)
                    throw new InvalidOperationException(
                        "A page with no address did not say it could not be checked: '" + checkRow.Answer.Text + "'");
                address.Edit.SetText(kept);
                if (dialog._name!.Text != name)
                    throw new InvalidOperationException("Putting the address back named the instance something else");
            }
            if (!dialog.Check() || dialog._profile == null)
                throw new InvalidOperationException("A filled page did not compose a profile");
            if (dialog._profile.Layers!.Count != dialog._selected!.Layers.Count)
                throw new InvalidOperationException("The composed instance does not have the preset's chain");
            if (dialog._source != null &&
                dialog._profile.Layers!.FirstOrDefault(layer => string.Equals(layer.Type, dialog._sourceType,
                    StringComparison.OrdinalIgnoreCase)) is { } mounted &&
                dialog._values.FirstOrDefault() is { Edit: { } } first)
            {
                // The typed value landed in the layer that mounts the source, not somewhere else.
                string expected = first.Edit.Text.Trim();
                if (!new[] { mounted.Source.Url, mounted.Source.Path }.Contains(expected, StringComparer.Ordinal))
                    throw new InvalidOperationException(
                        $"The address '{expected}' was not written into the '{mounted.Id}' layer");
            }
            // An empty name is refused where it is typed, not by the service after a save.
            dialog._name.SetText("");
            if (dialog.Check())
                throw new InvalidOperationException("An instance with no name was accepted");
            if (dialog._nameError is not { } error || !error.IsVisible())
                throw new InvalidOperationException("The empty name was refused without saying so under the field");
            dialog.Reject();
            if (dialog.Result() != 0) throw new InvalidOperationException("Cancelling did not close the dialog");
        }
        finally { dialog.Hide(); dialog.DeleteLater(); }
    }

    /// <summary>A scratch directory for the smoke run: an existing folder is what a folder source
    /// is checked against, and the run removes it again.</summary>
    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "PolyMountNewMount-" + Guid.NewGuid().ToString("N"));
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
