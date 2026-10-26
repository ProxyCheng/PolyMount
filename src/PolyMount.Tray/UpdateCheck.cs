using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using PolyMount.Core.Update;
using QtSharp.QtCore;
using QtSharp.QtGui;
using QtSharp.QtWidgets;

namespace PolyMount.Tray;

/// <summary>Asks the release feed whether a newer version exists, and keeps the tray's menu in
/// step with the answer.
///
/// The tray is the only process that can ask this: it is the one in front of the reader, and the
/// one whose own file version is the "installed" version. A service making outbound requests on a
/// schedule would be a background process phoning home on behalf of somebody who is not there.
///
/// Nothing here is allowed to interrupt: an automatic check that fails, finds nothing, or finds
/// what it already knows stays silent. A version the reader asked not to be told about again is
/// not announced -- but it remains downloadable from the menu, because "stop telling me" is not
/// "stop letting me".
///
/// The feed is the GitHub releases API by default. A private repository answers that without
/// credentials with a 404, and an installation that cannot reach its feed reports that on a manual
/// check rather than pretending to be up to date. <c>update.feed</c> and <c>update.token</c> in
/// ui.json point it somewhere else.</summary>
internal sealed class UpdateCheck : IDisposable
{
    private const string DefaultFeed = "https://api.github.com/repos/ProxyCheng/PolyMount/releases/latest";
    private const string FeedKey = "update.feed", TokenKey = "update.token";
    private const string DismissedKey = "update.dismissed", CheckedKey = "update.checked";
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly QSystemTrayIcon _tray;
    private readonly Action _menuChanged;
    private readonly QTimer _timer = new();
    private Task<ReleaseInfo?>? _pending;
    private bool _manual;
    private string? _failure;
    private ReleaseInfo? _available;

    public UpdateCheck(QSystemTrayIcon tray, Action menuChanged)
    {
        _tray = tray;
        _menuChanged = menuChanged;
        _timer.Timeout += Poll;
    }

    /// <summary>The version this installation is. Read off the executable rather than an assembly
    /// attribute so what the feed is compared against is what the reader can see in the file's
    /// properties.</summary>
    public static Version Current { get; } = ReadCurrent();

    /// <summary>The newer release waiting to be downloaded, if the last check found one.</summary>
    public ReleaseInfo? Available => _available;

    public bool Busy => _pending != null;

    /// <summary>Asks on start-up, at most once a day. The answer is used only if it is newer and
    /// has not been dismissed; everything else is silence.</summary>
    public void StartAutomatic()
    {
        if (Due) Begin(manual: false);
    }

    public void CheckManually()
    {
        if (!Busy) Begin(manual: true);
    }

    public void Download()
    {
        if (_available is { } release) Open(release.Url);
    }

    /// <summary>Stops announcing this version. It stays downloadable: the menu keeps offering it,
    /// and a later version is announced again.</summary>
    public void Dismiss()
    {
        if (_available is not { } release) return;
        UiSettings.Set(DismissedKey, release.Version);
        _menuChanged();
    }

    private bool Due
    {
        get
        {
            var last = UiSettings.Get(CheckedKey);
            return !DateTimeOffset.TryParse(last, out var when) || DateTimeOffset.UtcNow - when >= Interval;
        }
    }

    private void Begin(bool manual)
    {
        _manual = manual;
        _failure = null;
        UiSettings.Set(CheckedKey, DateTimeOffset.UtcNow.ToString("o"));
        _pending = Task.Run(FetchAsync);
        _timer.Start(200);
    }

    /// <summary>The task is polled rather than awaited where it finishes: the answer has to be
    /// acted on by the thread that owns the tray icon and the menu, and a Qt widget touched from
    /// the pool thread is the kind of bug that appears once a week.</summary>
    private void Poll()
    {
        if (_pending is not { IsCompleted: true } task) return;
        _timer.Stop();
        _pending = null;

        ReleaseInfo? latest = null;
        try { latest = task.GetAwaiter().GetResult(); }
        catch (Exception ex) { _failure = ex.GetBaseException().Message; }

        if (_manual) Report(latest);
        else Announce(latest);
    }

    private void Announce(ReleaseInfo? latest)
    {
        if (_failure != null || latest == null) return;
        if (!ReleaseFeed.IsNewer(latest.Version, Current) || IsDismissed(latest)) return;
        _available = latest;
        _menuChanged();
        _tray.ShowMessage(I18n.T("update.balloon.title"), I18n.F("update.balloon.body", latest.Version),
            QSystemTrayIcon.MessageIcon.Information, 6000);
    }

    private void Report(ReleaseInfo? latest)
    {
        var title = I18n.T("update.title");
        if (_failure is { Length: > 0 } reason)
        {
            QMessageBox.Information(null, title, I18n.F("update.failed", reason), I18n.T("close"));
            return;
        }
        if (latest == null)
        {
            QMessageBox.Information(null, title, I18n.T("update.noRelease"), I18n.T("close"));
            return;
        }
        if (!ReleaseFeed.IsNewer(latest.Version, Current))
        {
            QMessageBox.Information(null, title, I18n.F("update.current", Current.ToString(3)), I18n.T("close"));
            return;
        }

        _available = latest;
        _menuChanged();
        if (QMessageBox.Question(null, title, I18n.F("update.confirm", latest.Version, Current.ToString(3)),
                I18n.T("update.download"), I18n.T("update.later"), null, 0, 1) == 0)
            Download();
    }

    private static bool IsDismissed(ReleaseInfo release) =>
        string.Equals(UiSettings.Get(DismissedKey), release.Version, StringComparison.OrdinalIgnoreCase);

    private static async Task<ReleaseInfo?> FetchAsync()
    {
        var feed = UiSettings.Get(FeedKey);
        if (string.IsNullOrWhiteSpace(feed)) feed = DefaultFeed;
        using var request = new HttpRequestMessage(HttpMethod.Get, feed);
        request.Headers.TryAddWithoutValidation("User-Agent", "PolyMount/" + Current.ToString(3));
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        if (UiSettings.Get(TokenKey) is { Length: > 0 } token)
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        using var response = await Client.SendAsync(request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}");
        return ReleaseFeed.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    private static Version ReadCurrent()
    {
        try
        {
            if (Environment.ProcessPath is { Length: > 0 } path &&
                FileVersionInfo.GetVersionInfo(path).ProductVersion is { Length: > 0 } product &&
                ReleaseFeed.ParseVersion(product) is { } fromFile)
                return fromFile;
        }
        catch (FileNotFoundException) { }
        return Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);
    }

    private static void Open(string url)
    {
        try
        {
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not open the release page");
        }
    }

    public void Dispose() => _timer.Dispose();
}
