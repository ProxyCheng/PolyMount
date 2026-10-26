using System.Globalization;
using System.Text.Json;
using QtSharp.QtCore;
using QtSharp.QtGui;
using QtSharp.QtWidgets;

namespace PolyMount.Tray;

/// <summary>Qt translation catalogs; settings only select the active QTranslator.</summary>
internal static class I18n
{
    private static QTranslator? _translator;
    private static TranslationEvents? _events;
    public static string Language { get; private set; } = LoadLanguage();
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language == "en" ? "en-US" : "zh-CN");
    public static string T(string key) => QCoreApplication.Translate("PolyMount", key);
    public static string F(string key, params object[] args) => string.Format(Culture, T(key), args);
    public static void Initialize()
    {
        QtResources.Register();
        _events = new TranslationEvents();
        QCoreApplication.Instance().InstallEventFilter(_events);
        Install(Language);
    }
    private static void Install(string language)
    {
        if (_events!.Catalogs.TryGetValue(language, out var existing))
        {
            if (_translator != null) QCoreApplication.RemoveTranslator(_translator);
            _translator = existing;
            QCoreApplication.InstallTranslator(existing);
            return;
        }
        var translator = new QTranslator();
        if (!translator.Load(":/polymount/translations/polymount_" + language + ".qm"))
        { translator.Dispose(); throw new InvalidOperationException("Invalid Qt translation catalog: " + language); }
        _events.Catalogs.Add(language, translator);
        if (_translator != null) QCoreApplication.RemoveTranslator(_translator);
        _translator = translator;
        QCoreApplication.InstallTranslator(translator);
    }
    public static void Bind(Action<string> setter, string key)
    {
        setter(T(key));
        var target = setter.Target as QObject;
        if (target == null) throw new ArgumentException("Translation target must be a Qt object");
        var filter = new ObjectTranslationFilter(target, () => setter(T(key)));
        target.InstallEventFilter(filter);
        _events!.Bindings.Add(filter);
        target.Destroyed += _ => { _events.Bindings.Remove(filter); filter.DeleteLater(); };
    }
    // For non-widget objects (tray actions), Qt's application LanguageChange drives updates.
    public static event Action? Changed;

    /// <summary>Subscribes a handler for the life of <paramref name="owner"/>: a language change
    /// reaches every window the reader has open, and a window that has been closed in the meantime is
    /// a deleted object for a handler that outlived it. A page is destroyed when its tab is closed,
    /// so anything on a page subscribes here rather than to the raw event.</summary>
    public static void OnChanged(Action handler, QObject owner)
    {
        Changed += handler;
        owner.Destroyed += _ => Changed -= handler;
    }
    public static void SetLanguage(string language)
    {
        language = language == "en" ? "en" : "zh-CN";
        if (Language == language) return;
        Language = language;
        Install(language);
        UiSettings.Set("Language", Language);
    }
    private static string LoadLanguage() =>
        UiSettings.Get("Language") ?? (CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en");
    private sealed class ObjectTranslationFilter(QObject parent, Action translate) : QObject(parent)
    {
        public void Retranslate() => translate();
        public override bool EventFilter(QObject watched, QEvent e)
        {
            if (e.type() == QEvent.Type.LanguageChange) translate();
            return false;
        }
    }
    private sealed class TranslationEvents : QObject
    {
        public List<ObjectTranslationFilter> Bindings { get; } = new();
        public Dictionary<string, QTranslator> Catalogs { get; } = new();
        public override bool EventFilter(QObject watched, QEvent e)
        {
            if (e.type() == QEvent.Type.LanguageChange && watched.Handle == QCoreApplication.Instance().Handle)
            {
                foreach (var binding in Bindings.ToArray()) binding.Retranslate();
                Changed?.Invoke();
            }
            return false;
        }
    }
}
