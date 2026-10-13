using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using QtSharp.Api;

namespace PolyMount.Tray;

/// <summary>Qt translation catalogs; settings only select the active QTranslator.</summary>
internal static class I18n
{
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PolyMount", "ui.json");
    private static QTranslator? _translator;
    private static TranslationEvents? _events;
    public static string Language { get; private set; } = LoadLanguage();
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language == "en" ? "en-US" : "zh-CN");
    public static string T(string key) => QCoreApplication.Translate("PolyMount", key);
    public static string F(string key, params object[] args) => string.Format(Culture, T(key), args);
    public static void Initialize()
    {
        _events = new TranslationEvents();
        QCoreApplication.Instance().InstallEventFilter(_events);
        Install(Language);
    }
    private static void Install(string language)
    {
        if (_events!.Catalogs.FirstOrDefault(c => c.Translator.Language() == language).Translator is { } existing)
        {
            if (_translator != null) QCoreApplication.RemoveTranslator(_translator);
            _translator = existing;
            QCoreApplication.InstallTranslator(existing);
            return;
        }
        var translator = new QTranslator();
        using var stream = typeof(I18n).Assembly.GetManifestResourceStream("PolyMount.translations.polymount_" + language + ".qm")
            ?? throw new InvalidOperationException("Qt translation resource is missing: " + language);
        using var memory = new MemoryStream(); stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            // QTranslator's memory overload requires data to remain valid for its lifetime.
            if (!translator.Load(pointer, bytes.Length)) throw new InvalidOperationException("Invalid Qt translation catalog");
            _events!.Catalogs.Add((translator, pointer));
        }
        catch { Marshal.FreeHGlobal(pointer); translator.Dispose(); throw; }
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
    public static void SetLanguage(string language)
    {
        language = language == "en" ? "en" : "zh-CN";
        if (Language == language) return;
        Language = language;
        Install(language);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { Language }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static string LoadLanguage()
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return json.RootElement.GetProperty("Language").GetString() == "en" ? "en" : "zh-CN";
        }
        catch { return CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en"; }
    }
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
        public List<(QTranslator Translator, IntPtr Data)> Catalogs { get; } = new();
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
