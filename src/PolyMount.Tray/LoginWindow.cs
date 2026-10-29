using Microsoft.Web.WebView2.Core;
using QtSharp.QtCore;
using QtSharp.QtGui;
using QtSharp.QtWidgets;
using QtSharp.WebView;

namespace PolyMount.Tray;

/// <summary>
/// A sign-in page, opened where a browser can exist.
///
/// The service runs in session 0, where no window — and so no sign-in — can be shown. A credential
/// that authenticates by a web session is therefore obtained here, in the tray, the way CloudDisk
/// and Sunshine do it: the page is loaded in a view that belongs to the reader's session, and what
/// it leaves behind is read out of the view's own cookie store. Nothing is parsed out of the page
/// and nothing is injected into it.
///
/// The cookies are read, never the page: a token cookie is usually `HttpOnly`, which is exactly
/// what a script inside the page cannot see, and why this has to be the browser's own store.
///
/// The window is generic on purpose. Which page to show and which cookies mean "signed in" comes
/// from the plugin that needs the login — this class knows only that a page was asked for and
/// some values have to come back.
/// </summary>
internal sealed class LoginWindow : QDialog
{
    /// <summary>How often the cookie store is re-read while the window is open. Cookies are set
    /// on navigations the page may never finish (a redirect chain, a popup that closes), so the
    /// signal is a hint and this is the truth.</summary>
    private static readonly int PollMilliseconds = 400;

    private readonly WebView2Widget _view;
    private readonly QLabel _status;
    private readonly string _url;
    private readonly IReadOnlyList<string> _wanted;
    private readonly QTimer _poll;
    private readonly Dictionary<string, string> _found = new(StringComparer.OrdinalIgnoreCase);
    private Task<List<CoreWebView2Cookie>>? _reading;
    private bool _navigated;

    /// <summary>What the page left behind, when it asked for the window to close by itself.</summary>
    public IReadOnlyDictionary<string, string> Cookies => _found;

    public LoginWindow(QWidget? parent, string title, string url, IReadOnlyList<string> cookieNames) : base(parent)
    {
        _url = url;
        _wanted = cookieNames;
        SetWindowTitle(title);
        SetWindowIcon(TrayIcons.App);
        Resize(560, 720);
        SetMinimumSize(420, 520);

        var root = new QVBoxLayout(this);
        root.SetContentsMargins(0, 0, 0, 0);
        root.SetSpacing(0);

        var heading = new QLabel(title);
        heading.SetObjectName("detailTitle");
        heading.SetContentsMargins(18, 14, 18, 10);
        root.AddWidget(heading);

        _view = new WebView2Widget(this);
        // A profile of this product's own: a login made here must not land in the reader's own
        // browser profile, and signing out is then a matter of deleting this folder. It is a
        // setting of the binding rather than of one view, so it is set before any view exists —
        // which is what constructing this window is.
        WebView2Widget.UserDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PolyMount", "webview");
        _view.Ready += (_, _) =>
        {
            if (_navigated) return;
            _navigated = true;
            _view.Navigate(_url);
        };
        root.AddWidget(_view, 1);

        var footer = new QHBoxLayout();
        footer.SetContentsMargins(18, 10, 18, 12);
        footer.SetSpacing(10);
        _status = new QLabel();
        _status.SetObjectName("muted");
        _status.SetWordWrap(true);
        footer.AddWidget(_status, 1);
        var cancel = new QPushButton();
        I18n.Bind(cancel.SetText, "cancel");
        cancel.Clicked += _ => Reject();
        footer.AddWidget(cancel);
        root.AddLayout(footer);

        _poll = new QTimer(this);
        _poll.SetInterval(PollMilliseconds);
        _poll.Timeout += Poll;
    }

    public override void ShowEvent(QShowEvent e)
    {
        base.ShowEvent(e);
        if (!WebView2Widget.IsRuntimeAvailable)
        {
            // Said here rather than at the first navigation: an empty white view reads as a page
            // that failed, and the reason is a runtime that is not installed.
            _status.SetText(I18n.T("login.noRuntime"));
            _status.SetStyleSheet("color: " + TrayTheme.Tokens.DangerStrong + ";");
            return;
        }
        _view.Initialize();
        _poll.Start();
    }

    public override void HideEvent(QHideEvent e)
    {
        _poll.Stop();
        base.HideEvent(e);
    }

    /// <summary>One look at the cookie store. A read already in flight is left alone: the store is
    /// asked no faster than the browser can answer, and a slow answer must not become a queue.</summary>
    private void Poll()
    {
        if (_reading is { IsCompleted: false }) return;
        if (_reading is { IsCompleted: true } finished) Apply(finished);
        _reading = null;
        try { _reading = _view.Browser?.CookieManager.GetCookiesAsync(_url); }
        catch (Exception) { /* the view is not up yet; the next tick asks again */ }
    }

    private void Apply(Task<List<CoreWebView2Cookie>> finished)
    {
        List<CoreWebView2Cookie>? cookies;
        try { cookies = finished.Result; }
        catch (Exception) { return; }
        foreach (var cookie in cookies)
        {
            if (!_wanted.Contains(cookie.Name, StringComparer.OrdinalIgnoreCase)) continue;
            _found[cookie.Name] = cookie.Value;
        }
        if (_found.Count < _wanted.Count)
        {
            _status.SetText(I18n.T("login.waiting"));
            return;
        }
        // Everything the caller named is there: the page is done, whether or not it said so.
        Accept();
    }

    /// <summary>What the caller collects when the window is accepted. Exposed for the smoke check,
    /// which reads it without a browser.</summary>
    internal IReadOnlyDictionary<string, string> Captured => _found;
}
