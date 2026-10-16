using QtSharp.Api;

namespace PolyMount.Tray;

/// <summary>A part of a page that folds away, keeping one line of itself on screen while it is
/// folded.
///
/// The performance page's subject is its charts, and they stay where they are. What sits under
/// them is what a reader turns to once a chart has raised a question, and it costs one click to
/// keep that out of the way the rest of the time. The header keeps a line of summary because a
/// folded section that says nothing at all is a section with no reason to be opened — and because
/// that line is where the numbers that would make someone open it can be read without opening it.
///
/// Qt has no widget for this. Its QToolBox is an accordion — one page visible at a time — which is
/// a different thing from several sections that fold independently of one another, and the theme
/// carries no rules for QGroupBox or QToolBox either, so this is built from the parts the page
/// already styles: a flat button for the header, and the page's own background behind the body. It
/// is a section rather than a card because the charts inside it are cards already, and a card
/// inside a card reads as a box.
/// </summary>
internal sealed class CollapsibleSection : QWidget
{
    private readonly string _key;
    private readonly string _titleKey;
    private readonly QPushButton _toggle;
    private readonly QLabel _summary;
    private readonly QWidget _body;
    private bool _expanded;

    /// <summary>Raised when the reader folds or unfolds it, so the page can write the choice down.
    /// Deliberately not raised by <see cref="SetExpanded(bool)"/>: that is how a smoke run opens
    /// everything to photograph it without leaving the reader's own page rearranged.</summary>
    internal event Action<CollapsibleSection>? Toggled;

    public CollapsibleSection(string key, string titleKey, QWidget body, bool expanded)
    {
        _key = key;
        _titleKey = titleKey;
        _body = body;
        _expanded = expanded;
        var root = new QVBoxLayout(this);
        root.SetContentsMargins(0, 0, 0, 0);
        root.SetSpacing(10);
        var head = new QHBoxLayout();
        head.SetSpacing(12);
        _toggle = new QPushButton();
        _toggle.SetObjectName("sectionToggle");
        _toggle.SetCursor(new QCursor(Qt.CursorShape.PointingHandCursor));
        _toggle.Clicked += _ => SetExpanded(!_expanded, notify: true);
        // The button takes the room so the whole title behaves like the one control it reads as.
        head.AddWidget(_toggle, 1);
        _summary = new QLabel();
        _summary.SetObjectName("muted");
        _summary.SetTextFormat(Qt.TextFormat.PlainText);
        head.AddWidget(_summary);
        root.AddLayout(head);
        root.AddWidget(_body);
        Translate();
        Apply();
    }

    /// <summary>Which section this is, as the page names it when it writes the choice down.</summary>
    internal string Key => _key;

    internal bool Expanded => _expanded;

    /// <summary>The few numbers the header carries while the section is folded, so that folding it
    /// away does not also hide whether anything is worth looking at. Hidden once it is open, where
    /// the body says the same thing at length: a second copy of a number is one more thing to read
    /// past, and the point of the line was to stand in for the body, not to accompany it.</summary>
    internal void SetSummary(string text)
    {
        if (_summary.Text != text) _summary.SetText(text);
    }

    /// <summary>Opens or folds the section without telling anyone, for a caller that is arranging
    /// the page rather than a reader who is using it.</summary>
    internal void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        Apply();
    }

    private void SetExpanded(bool expanded, bool notify)
    {
        _expanded = expanded;
        Apply();
        if (notify) Toggled?.Invoke(this);
    }

    private void Apply()
    {
        _body.SetVisible(_expanded);
        _summary.SetVisible(!_expanded);
        Translate();
    }

    /// <summary>The title again in the language the page is in now, behind the arrow that says
    /// which way the section is facing. The arrow is not part of the translation: which way a
    /// section is facing is a state of this reader's page, not a word in anyone's language.</summary>
    internal void Translate() => _toggle.SetText((_expanded ? "▾ " : "▸ ") + I18n.T(_titleKey));
}
