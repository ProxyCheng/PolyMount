using QtSharp.Api;

namespace PolyMount.Tray;

/// <summary>A page that has something to lose when its tab is closed: it is asked, and it decides
/// whether the close happens. The workspace only supplies the way to finish it.</summary>
internal interface ICloseGuard
{
    void RequestClose(Action proceed);
}

/// <summary>A page whose answer to "may I close" is whatever a check sets it to. The real pages ask
/// a reader — about a draft, about a recording — and a smoke run has nobody to answer, so what those
/// answer is checked by their own steps and what the *window* does with an answer is checked with
/// this: a page that would rather stay, and then one that does not.</summary>
internal sealed class CheckPage : QWidget, ICloseGuard
{
    /// <summary>Whether this page asks to stay when it is asked to go.</summary>
    public bool Refuses { get; set; }

    void ICloseGuard.RequestClose(Action proceed)
    {
        if (Refuses) return;
        proceed();
    }
}

/// <summary>Native tab groups and splitter trees; moving a page always reparents the same widget.</summary>
internal sealed class TabWorkspace : QWidget
{
    private readonly QVBoxLayout _layout;
    private readonly QWidget _parking;
    private readonly Dictionary<string, Page> _pages = new();
    private readonly List<Group> _groups = new();
    private readonly List<QSplitter> _splitters = new();
    private readonly List<FloatingWindow> _windows = new();
    private readonly TabEvents _events;
    private readonly TabDragVisuals _visuals;
    private QWidget _root;
    private Group _active;
    private Page? _pressed;
    private Group? _source;
    private int _pressX, _pressY;
    private bool _dragging;
    private enum Zone { Center, Left, Right, Top, Bottom }
    private sealed class Page(string key, QWidget widget, Func<string> title, bool pinned)
    {
        public string Key { get; } = key;
        public QWidget Widget { get; } = widget;
        public Func<string> Title { get; } = title;
        public bool Pinned { get; } = pinned;
        public Group? Group { get; set; }
    }
    private sealed class Group(TabWorkspace owner, FloatingWindow? window) : QTabWidget
    {
        public TabWorkspace Owner { get; } = owner;
        public FloatingWindow? Host { get; } = window;
    }

    public TabWorkspace(QWidget parent) : base(parent)
    {
        _layout = new QVBoxLayout(this);
        _layout.SetContentsMargins(0, 0, 0, 0);
        _parking = new QWidget(this);
        _parking.Hide();
        _active = NewGroup(null);
        _root = _active;
        _layout.AddWidget(_root);
        _events = new TabEvents(this);
        QCoreApplication.Instance().InstallEventFilter(_events);
        _visuals = new TabDragVisuals(this);
        I18n.Changed += Translate;
        Destroyed += _ =>
        {
            I18n.Changed -= Translate;
            QCoreApplication.Instance().RemoveEventFilter(_events);
        };
    }

    public QWidget Open(string key, Func<QWidget> create, Func<string> title, bool pinned = false)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = new Page(key, create(), title, pinned);
            page.Widget.SetMinimumSize(0, 0);
            page.Widget.SetSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Ignored);
            _pages.Add(key, page);
            if (page.Widget is ConfigEditor)
                page.Widget.WindowTitleChanged += _ =>
                {
                    if (page.Group == null) return;
                    page.Group.SetTabText(page.Group.IndexOf(page.Widget), PageTitle(page));
                    UpdateWindowTitle(page.Group);
                };
        }
        if (page.Group == null) Attach(page, _active);
        page.Group!.SetCurrentWidget(page.Widget);
        if (page.Group.Host is { } host) { host.Show(); host.Raise(); host.ActivateWindow(); }
        return page.Widget;
    }

    private Group NewGroup(FloatingWindow? window)
    {
        var group = new Group(this, window);
        group.SetObjectName("workspaceTabs");
        group.SetDocumentMode(true);
        group.SetMovable(true);
        group.SetTabsClosable(false);
        group.SetUsesScrollButtons(true);
        group.SetMinimumSize(160, 120);
        group.SetSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Ignored);
        group.TabBar().SetExpanding(false);
        group.TabBar().SetToolTip(I18n.T("tabs.hint"));
        group.TabCloseRequested += index =>
        {
            var page = PageAt(group, index);
            if (page is not { Pinned: false }) return;
            ClosePage(page);
        };
        group.CurrentChanged += _ => { if (group.Host == null) _active = group; UpdateWindowTitle(group); };
        _groups.Add(group);
        return group;
    }

    private Page? PageAt(Group group, int index) => index < 0 ? null :
        _pages.Values.FirstOrDefault(p => p.Group == group && p.Widget.Handle == group.Widget(index)?.Handle);

    private void Attach(Page page, Group group, int index = -1)
    {
        page.Group = group;
        int added = index < 0 ? group.AddTab(page.Widget, page.Widget.WindowIcon, PageTitle(page)) :
            group.InsertTab(index, page.Widget, page.Widget.WindowIcon, PageTitle(page));
        if (page.Pinned)
            group.TabBar().SetTabButton(added, QTabBar.ButtonPosition.RightSide, null);
        else
        {
            var slot = new QWidget(group.TabBar());
            slot.SetFixedSize(30, 24);
            var inset = new QHBoxLayout(slot);
            inset.SetContentsMargins(2, 3, 10, 3);
            inset.SetSpacing(0);
            var close = new QPushButton(slot);
            close.SetObjectName("tabClose");
            close.SetFixedSize(18, 18);
            close.SetIcon(TrayIcons.Get("close"));
            close.SetIconSize(new QSize(14, 14));
            close.SetFocusPolicy(Qt.FocusPolicy.NoFocus);
            I18n.Bind(close.SetToolTip, "tabs.close");
            close.Clicked += _ => ClosePage(page);
            inset.AddWidget(close);
            group.TabBar().SetTabButton(added, QTabBar.ButtonPosition.RightSide, slot);
        }
        group.SetCurrentWidget(page.Widget);
        page.Widget.Show();
        UpdateWindowTitle(group);
    }

    private static void Remove(Page page)
    {
        var group = page.Group!;
        group.RemoveTab(group.IndexOf(page.Widget));
        page.Group = null;
    }

    private void ClosePage(Page page)
    {
        if (page.Pinned || page.Group == null) return;
        // A page that cannot simply be put away — one holding a draft, or a recording — is asked
        // first, and it is the page that knows what to ask.
        if (page.Widget is ICloseGuard guard) { guard.RequestClose(() => FinishClosePage(page)); return; }
        FinishClosePage(page);
    }
    private void FinishClosePage(Page page)
    {
        if (page.Group == null) return;
        if (_pressed == page) CancelDrag();
        var group = page.Group;
        Remove(page);
        // Closing a tab takes the page with it: the workspace keeps no page back to be reopened, so
        // what was on it is gone and opening that tab again builds a new one. That is what makes the
        // life of a page the whole of what being open means for it — a performance page watches its
        // instance for as long as it exists and no longer, without having to ask whether it is the
        // tab on show. (The one page that is never closed is the pinned home page, which has no
        // close button to press.)
        page.Widget.SetParent(_parking);
        page.Widget.Hide();
        _pages.Remove(page.Key);
        page.Widget.DeleteLater();
        Prune(group);
    }
    /// <summary>Puts the close question to each of those pages in turn and calls
    /// <paramref name="proceed"/> once they have all said yes. A page answers by calling what it was
    /// given; one that would rather stay simply does not, and nothing further is asked — so a reader
    /// who cancels the last question has cancelled the whole thing, whichever window it was.</summary>
    private static void Ask(IReadOnlyList<Page> pages, Action proceed)
    {
        var guards = pages.Where(page => page.Widget is ICloseGuard)
            .Select(page => (ICloseGuard)page.Widget).ToList();
        int next = 0;
        void Once()
        {
            if (next >= guards.Count) { proceed(); return; }
            guards[next++].RequestClose(Once);
        }
        Once();
    }

    /// <summary>The pages this workspace holds, which is what a window of it asks before it goes.</summary>
    internal void RequestCloseAll(Action proceed) =>
        Ask(_pages.Values.Where(page => page.Group != null).ToList(), proceed);

    /// <summary>Closing one of the detached windows closes what is in it. Sending its pages back to
    /// the window they were pulled out of was the other answer, and it is not what a reader closing
    /// a window means: the things in it go, which is what every other close on this workspace does.
    /// Each page is asked first, the way a tab close asks it; one that would rather stay keeps the
    /// window it is in, and the window itself follows its last page out (see Prune).</summary>
    private void CloseWindow(FloatingWindow host)
    {
        var pages = _pages.Values.Where(page => page.Group?.Host == host).ToList();
        var closeable = pages.Where(page => !page.Pinned).ToList();
        // A window holding nothing that may be closed — the pinned home page, which has no close
        // button and no way of being got rid of — is the one case that still goes back.
        if (closeable.Count == 0) { ReturnWindow(host); return; }
        Ask(closeable, () => { foreach (var page in closeable) FinishClosePage(page); });
    }

    /// <summary>The window this workspace is in is going away, and the pages that were detached into
    /// windows of their own go with it. A floating window is a window precisely because it has no
    /// parent in the widget tree, so nothing else would take it down, and what it would be left
    /// showing is pages whose workspace no longer exists. The pages have been asked already, all of
    /// them, by whoever is taking the window down.</summary>
    internal void ShutDown()
    {
        foreach (var host in _windows) { host.Hide(); host.DeleteLater(); }
        _windows.Clear();
    }


    /// <summary>Lets Qt do what it deferred: a page that is closed is deleted when the event loop
    /// next gets a turn, so a check that closes one has to give it that turn before it looks.</summary>
    private static void Settle()
    {
        for (int round = 0; round < 10; round++) QCoreApplication.ProcessEvents();
    }

    private static string PageTitle(Page page) => page.Widget is ConfigEditor ? page.Widget.WindowTitle : page.Title();

    private void MovePage(Page page, Group target, Zone zone, int index = -1)
    {
        var source = page.Group!;
        if (source == target && zone != Zone.Center && source.Count == 1) return;
        if (source == target && zone == Zone.Center)
        {
            if (index >= 0) source.TabBar().MoveTab(source.IndexOf(page.Widget), Math.Min(index, source.Count - 1));
            source.SetCurrentWidget(page.Widget);
            return;
        }
        if (zone != Zone.Center) target = Split(target, zone);
        Remove(page);
        Attach(page, target, index);
        Prune(source);
    }

    private Group Split(Group target, Zone zone)
    {
        var group = NewGroup(target.Host);
        var orientation = zone is Zone.Left or Zone.Right ? Qt.Orientation.Horizontal : Qt.Orientation.Vertical;
        var parent = ParentSplitter(target);
        var split = new QSplitter(orientation);
        split.SetChildrenCollapsible(false);
        split.SetHandleWidth(8);
        split.SetSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Ignored);
        _splitters.Add(split);
        if (parent != null) parent.ReplaceWidget(parent.IndexOf(target), split);
        else ReplaceRoot(target.Host, target, split);
        if (zone is Zone.Left or Zone.Top) { split.AddWidget(group); split.AddWidget(target); }
        else { split.AddWidget(target); split.AddWidget(group); }
        split.SetStretchFactor(0, 1);
        split.SetStretchFactor(1, 1);
        int extent = Math.Max(320, orientation == Qt.Orientation.Horizontal ? target.Width : target.Height);
        split.SetSizes(new[] { extent / 2, extent / 2 });
        split.Show();
        group.Show();
        return group;
    }

    private QSplitter? ParentSplitter(QWidget widget) =>
        _splitters.FirstOrDefault(s => s.Handle == widget.ParentWidget()?.Handle);

    private void ReplaceRoot(FloatingWindow? host, QWidget old, QWidget replacement)
    {
        var layout = host?.Body ?? _layout;
        layout.RemoveWidget(old);
        old.SetParent(_parking);
        layout.AddWidget(replacement);
        if (host == null) _root = replacement;
        else host.Root = replacement;
    }

    private void Prune(Group group)
    {
        if (group.Count != 0) return;
        var parent = ParentSplitter(group);
        if (parent == null)
        {
            if (group.Host is { } host)
            {
                _groups.Remove(group);
                _windows.Remove(host);
                host.Hide();
                host.DeleteLater();
            }
            return;
        }
        group.SetParent(_parking);
        _groups.Remove(group);
        group.DeleteLater();
        QWidget sibling = parent.Widget(0);
        var grandparent = ParentSplitter(parent);
        sibling.SetParent(_parking);
        if (grandparent != null) grandparent.ReplaceWidget(grandparent.IndexOf(parent), sibling);
        else ReplaceRoot(group.Host, parent, sibling);
        _splitters.Remove(parent);
        parent.DeleteLater();
        sibling.Show();
        if (!_groups.Contains(_active)) _active = _groups.First(g => g.Host == null);
    }

    private void Detach(Page page, int x, int y)
    {
        var source = page.Group!;
        if (source.Host != null && source.Count == 1 && ParentSplitter(source) == null)
        { source.Host.Move(x - 80, y - 16); return; }
        var host = new FloatingWindow(this);
        _windows.Add(host);
        var group = NewGroup(host);
        host.Root = group;
        host.Body.AddWidget(group);
        Remove(page);
        Attach(page, group);
        Prune(source);
        host.Resize(Math.Max(640, Width * 3 / 4), Math.Max(480, Height * 3 / 4));
        host.Move(x - 80, y - 16);
        host.Show();
    }

    private void ReturnWindow(FloatingWindow host)
    {
        foreach (var page in _pages.Values.Where(p => p.Group?.Host == host).ToArray())
            MovePage(page, _active, Zone.Center);
    }

    private (Group? Group, Zone Zone, int Index) Target(int x, int y)
    {
        using var global = new QPoint(x, y);
        foreach (var group in _groups.Where(g => g.Host != null).Concat(_groups.Where(g => g.Host == null)))
        {
            if (!group.IsVisible()) continue;
            using var local = group.MapFromGlobal(global);
            int px = local.X(), py = local.Y();
            if (px < 0 || py < 0 || px >= group.Width || py >= group.Height) continue;
            using var barPos = group.TabBar().MapFromGlobal(global);
            if (barPos.Y() >= 0 && barPos.Y() < group.TabBar().Height)
                return (group, Zone.Center, group.TabBar().TabAt(barPos));
            int edge = Math.Min(72, Math.Min(group.Width, group.Height) / 4);
            Zone zone = px < edge ? Zone.Left : px >= group.Width - edge ? Zone.Right :
                py < group.TabBar().Height + edge ? Zone.Top : py >= group.Height - edge ? Zone.Bottom : Zone.Center;
            return (group, zone, -1);
        }
        return (null, Zone.Center, -1);
    }

    private void Preview(int x, int y)
    {
        var (group, zone, _) = Target(x, y);
        _visuals.Move(x, y, group == null);
        if (group == null) { _visuals.ClearTarget(); return; }
        using var zero = new QPoint(0, 0);
        using var origin = group.MapToGlobal(zero);
        int left = origin.X(), top = origin.Y(), width = group.Width, height = group.Height;
        if (zone is Zone.Left or Zone.Right) { width /= 2; if (zone == Zone.Right) left += width; }
        if (zone is Zone.Top or Zone.Bottom) { height /= 2; if (zone == Zone.Bottom) top += height; }
        _visuals.DropTarget(left, top, width, height, I18n.T("tabs." + zone.ToString().ToLowerInvariant()));
    }

    private void CancelDrag()
    {
        if (_pressed != null) _source?.TabBar().ReleaseMouse();
        if (_dragging) _source?.TabBar().ReleaseKeyboard();
        _visuals.End();
        _pressed = null;
        _source = null;
        _dragging = false;
    }

    private void Translate()
    {
        foreach (var page in _pages.Values.Where(p => p.Group != null))
            page.Group!.SetTabText(page.Group.IndexOf(page.Widget), PageTitle(page));
        foreach (var group in _groups)
        {
            group.TabBar().SetToolTip(I18n.T("tabs.hint"));
            UpdateWindowTitle(group);
        }
    }

    private static void UpdateWindowTitle(Group group)
    {
        if (group.Host != null && group.CurrentIndex >= 0)
            group.Host.SetWindowTitle(group.TabText(group.CurrentIndex) + " — PolyMount");
    }

    private sealed class TabEvents(TabWorkspace owner) : QObject(owner)
    {
        public override bool EventFilter(QObject watched, QEvent e)
        {
            if (owner._dragging && e.type() == QEvent.Type.KeyPress && QKeyEvent.FromHandle(e.Handle).Key() == (int)Qt.Key.Key_Escape)
            { owner.CancelDrag(); return true; }
            var group = owner._groups.FirstOrDefault(g => g.TabBar().Handle == watched.Handle);
            if (group == null) return false;
            if (e.type() == QEvent.Type.MouseButtonPress)
            {
                var mouse = QMouseEvent.FromHandle(e.Handle);
                if (mouse.Button() != Qt.MouseButton.LeftButton) return false;
                using var pos = mouse.Pos();
                var page = owner.PageAt(group, group.TabBar().TabAt(pos));
                if (page == null) return false;
                owner._pressed = page;
                owner._source = group;
                owner._pressX = mouse.GlobalX(); owner._pressY = mouse.GlobalY();
                return false;
            }
            if (e.type() == QEvent.Type.MouseMove && owner._pressed != null)
            {
                var mouse = QMouseEvent.FromHandle(e.Handle);
                if (!owner._dragging)
                {
                    if (Math.Abs(mouse.GlobalX() - owner._pressX) + Math.Abs(mouse.GlobalY() - owner._pressY) < 12) return false;
                    using var pos = mouse.Pos();
                    // Native QTabBar owns in-strip reordering, including its slide and settle animation.
                    if (pos.Y() >= -16 && pos.Y() < group.TabBar().Height + 16 && pos.X() >= -16 && pos.X() < group.TabBar().Width + 16)
                        return false;
                    owner._dragging = true;
                    owner._visuals.Begin(owner._pressed.Widget, owner._pressed.Title(), mouse.GlobalX(), mouse.GlobalY());
                    group.TabBar().GrabMouse();
                    group.TabBar().GrabKeyboard();
                }
                owner.Preview(mouse.GlobalX(), mouse.GlobalY());
                return true;
            }
            if (e.type() == QEvent.Type.MouseButtonRelease && owner._pressed != null)
            {
                var mouse = QMouseEvent.FromHandle(e.Handle);
                if (mouse.Button() != Qt.MouseButton.LeftButton) return false;
                var page = owner._pressed;
                bool dragging = owner._dragging;
                if (dragging) group.TabBar().MouseReleaseEvent(mouse);
                owner.CancelDrag();
                if (dragging)
                {
                    var (target, zone, index) = owner.Target(mouse.GlobalX(), mouse.GlobalY());
                    if (target == null) owner.Detach(page, mouse.GlobalX(), mouse.GlobalY());
                    else owner.MovePage(page, target, zone, index);
                }
                return dragging;
            }
            return false;
        }
    }

    private sealed class FloatingWindow : QWidget
    {
        private readonly TabWorkspace _owner;
        public QVBoxLayout Body { get; }
        public QWidget Root { get; set; } = null!;
        public FloatingWindow(TabWorkspace owner)
        {
            _owner = owner;
            SetWindowIcon(TrayIcons.App);
            
            SetMinimumSize(480, 360);
            Body = new QVBoxLayout(this);
            Body.SetContentsMargins(0, 0, 0, 0);
        }
        public override void CloseEvent(QCloseEvent e)
        {
            // Not accepted here and not closed here either: what closing this window means is that
            // the pages in it go, they are asked one at a time, and the window itself is taken down
            // by the last of them leaving (Prune). Nothing re-enters a close, which Qt refuses to do
            // twice over anyway — a close already in progress makes a nested one do nothing.
            e.Ignore();
            _owner.CloseWindow(this);
        }
    }

    internal void SmokeInteractions(string editorKey, string statsKey, Func<QWidget> newStats)
    {
        var editor = _pages[editorKey];
        var stats = _pages[statsKey];
        var home = _pages["overview"];
        var originalEditor = editor.Widget.Handle;
        var originalStats = stats.Widget.Handle;
        foreach (var zone in new[] { Zone.Right, Zone.Bottom, Zone.Left, Zone.Top })
        {
            MovePage(editor, home.Group!, zone);
            MovePage(stats, editor.Group!, Zone.Bottom);
            MovePage(stats, home.Group!, Zone.Center);
            MovePage(editor, home.Group!, Zone.Center);
            if (_splitters.Count != 0 || _groups.Count != 1)
                throw new InvalidOperationException($"Empty tab groups/splitters were not collapsed: {zone}, groups={_groups.Count}, splits={_splitters.Count}");
        }
        // A page pulled out into a window of its own gets back by being dragged back — the page
        // itself never left. Closing that window is the other thing a reader can do to it, and that
        // closes what is in it (see CloseWindow), which is checked just below.
        Detach(editor, 120, 120);
        MovePage(stats, editor.Group!, Zone.Bottom);
        MovePage(editor, home.Group!, Zone.Center);
        MovePage(stats, home.Group!, Zone.Center);
        if (_windows.Count != 0 || _groups.Count != 1 || editor.Group != home.Group || stats.Group != home.Group)
            throw new InvalidOperationException("Floating window pages were not returned to the workspace");
        // Closing one closes the pages that were in it: a page that asks to stay keeps the window
        // open, and one that agrees takes it down with them. The page asked here is a check page,
        // which answers by itself — a page a reader has puts its question to *them*, and a smoke run
        // is nobody — so what the editor and the performance page answer is left to their own steps.
        var stubborn = new CheckPage { Refuses = true };
        bool destroyed = false;
        Open("check-detached", () => stubborn, () => "check-detached");
        stubborn.Destroyed += _ => destroyed = true;
        Detach(_pages["check-detached"], 120, 120);
        var window = _windows[^1];
        window.Close();
        if (_windows.Count != 1 || !_pages.ContainsKey("check-detached"))
            throw new InvalidOperationException("A window closed a page that had asked to stay");
        stubborn.Refuses = false;
        window.Close();
        Settle();
        if (!destroyed)
            throw new InvalidOperationException("Closing a window left the page that was in it alive");
        if (_windows.Count != 0 || _groups.Count != 1 || _pages.ContainsKey("check-detached"))
            throw new InvalidOperationException("Closing a window left the page or the window itself behind");
        home.Group!.TabBar().MoveTab(home.Group.IndexOf(stats.Widget), 0);
        if (editor.Widget.Handle != originalEditor || stats.Widget.Handle != originalStats)
            throw new InvalidOperationException("Moving a tab recreated its page");
        ClosePage(stats);
        if (stats.Group != null) throw new InvalidOperationException("Tab close button did not close its page");
        // A closed tab is destroyed, so the page that was watching an instance goes with it — a page
        // that kept existing would go on asking the instance for readings nobody is looking at — and
        // opening the tab again is a new page, watching from now rather than remembering.
        if (_pages.ContainsKey(statsKey))
            throw new InvalidOperationException("A closed performance tab was kept rather than destroyed");
        var reopened = Open(statsKey, newStats, stats.Title);
        if (reopened.Handle == originalStats)
            throw new InvalidOperationException("A reopened performance tab was the page that was closed");
        if (reopened is not StatsWindow fresh || !fresh.Watching)
            throw new InvalidOperationException("A reopened performance tab did not start watching");
        stats = _pages[statsKey];
        MovePage(editor, home.Group!, Zone.Right);
        MovePage(stats, editor.Group!, Zone.Bottom);
        Translate();
    }
}
