using PolyMount.Core.Ipc;
using QtSharp.Api;

namespace PolyMount.Tray;

/// <summary>
/// Asks for the account that may read one origin, checks it against the server, and keeps it.
///
/// The dialog knows nothing about SVN: it is named after the origin, and the check itself is done
/// by whichever source type claims that instance — a Git or SSH plugin later reuses this as it is.
/// A refused account keeps the dialog open with the server's answer, because the next thing the
/// reader wants is to correct it.
/// </summary>
internal sealed class CredentialPrompt : QDialog
{
    private readonly string _instance;
    private readonly QLineEdit _user, _password;
    private readonly QLabel _status;
    private readonly QPushButton _signIn, _cancel;
    private readonly QTimer _timer = new();
    private Task<ControlResponse>? _pending;

    private static string T(string key) => I18n.T(key);

    public CredentialPrompt(QWidget? parent, string instance, string origin) : base(parent)
    {
        _instance = instance;
        SetObjectName("credentialPrompt");
        SetWindowTitle(T("auth.title"));
        SetWindowIcon(TrayIcons.App);
        Resize(440, 260);

        var root = new QVBoxLayout(this);
        root.SetContentsMargins(24, 22, 24, 20);
        root.SetSpacing(12);
        var heading = new QLabel();
        I18n.Bind(heading.SetText, "auth.title");
        heading.SetObjectName("detailTitle");
        root.AddWidget(heading);
        var body = new QLabel(I18n.F("auth.body", origin));
        body.SetWordWrap(true);
        body.SetObjectName("muted");
        root.AddWidget(body);

        var form = new QFormLayout();
        form.SetRowWrapPolicy(QFormLayout.RowWrapPolicy.DontWrapRows);
        form.SetFieldGrowthPolicy(QFormLayout.FieldGrowthPolicy.AllNonFixedFieldsGrow);
        _user = new QLineEdit();
        _password = new QLineEdit();
        _password.SetEchoMode(QLineEdit.EchoMode.Password);
        var userLabel = new QLabel();
        I18n.Bind(userLabel.SetText, "auth.username");
        var passwordLabel = new QLabel();
        I18n.Bind(passwordLabel.SetText, "auth.password");
        form.AddRow(userLabel, _user);
        form.AddRow(passwordLabel, _password);
        root.AddLayout(form);

        _status = new QLabel("");
        _status.SetWordWrap(true);
        _status.SetObjectName("errorBox");
        _status.SetVisible(false);
        root.AddWidget(_status);

        var footer = new QHBoxLayout();
        footer.AddStretch(1);
        _cancel = new QPushButton();
        I18n.Bind(_cancel.SetText, "cancel");
        _cancel.Clicked += _ => Reject();
        footer.AddWidget(_cancel);
        _signIn = new QPushButton();
        I18n.Bind(_signIn.SetText, "auth.signin");
        _signIn.SetObjectName("primary");
        _signIn.SetDefault(true);
        _signIn.Clicked += _ => Submit();
        footer.AddWidget(_signIn);
        root.AddLayout(footer);

        // The check is one network round trip; it runs on the pool and the dialog keeps its event
        // loop, so a server that takes its time does not freeze the window.
        _timer.SetInterval(200);
        _timer.Timeout += Poll;
        Destroyed += _ => _timer.Dispose();
    }

    private void Submit()
    {
        if (_pending != null) return;
        string user = _user.Text.Trim();
        if (user.Length == 0)
        {
            Explain(T("auth.need.user"));
            _user.SetFocus();
            return;
        }
        _status.SetVisible(false);
        _signIn.SetEnabled(false);
        _cancel.SetEnabled(false);
        string password = _password.Text;
        _pending = Task.Run(() => SupervisorClient.Default.Authenticate(_instance, user, password));
        _timer.Start();
    }

    private void Poll()
    {
        if (_pending is not { IsCompleted: true } task) return;
        _timer.Stop();
        _pending = null;

        ControlResponse? response = null;
        string? failure = null;
        try { response = task.GetAwaiter().GetResult(); }
        catch (Exception ex) { failure = ex.GetBaseException().Message; }

        if (response is { Ok: true })
        {
            Accept();
            return;
        }
        Explain(failure ?? response?.Error ?? T("auth.failed"));
        _signIn.SetEnabled(true);
        _cancel.SetEnabled(true);
    }

    private void Explain(string text)
    {
        _status.SetText(text);
        _status.SetVisible(true);
    }
}
