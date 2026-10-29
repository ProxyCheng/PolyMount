namespace PolyMount.Plugins.TxFShim;

/// <summary>
/// Localized text for this plugin's notices and its management page, from
/// <c>Resources/plugin-texts.json</c> — embedded in the assembly, so the plugin stays one file. No
/// user-visible text is written in code; the keys are resolved by the contract's loader, the same
/// way the product's own layer texts are.
/// </summary>
internal static class TxFShimTexts
{
    public const string ResourceName = "PolyMount.Plugin.TxFShim.Texts";

    private static readonly PluginTexts Texts = PluginTexts.Load(typeof(TxFShimTexts), ResourceName);

    public static LocalizedText Get(string key) => Texts.Get(key);

    /// <summary>A sentence with values in it, filled in every language at once.</summary>
    public static LocalizedText Format(string key, params object[] args) => Texts.Format(key, args);

    /// <summary>The same text with a detail appended — a path or a size after the sentence that
    /// names it. Both languages get the suffix, so nothing about the result depends on the
    /// reader's language.</summary>
    public static LocalizedText With(LocalizedText text, string suffix) => new()
    {
        Values = text.Values.ToDictionary(pair => pair.Key, pair => pair.Value + suffix, StringComparer.Ordinal)
    };

    /// <summary>The table itself, so a test can check that every key is written in both
    /// languages.</summary>
    public static PluginTexts Table => Texts;
}
