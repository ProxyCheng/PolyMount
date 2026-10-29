namespace PolyMount.Plugins.Smb;

/// <summary>
/// Localized text for this plugin's descriptors, from <c>Resources/plugin-texts.json</c> — embedded
/// in the assembly, so the plugin stays one file. No user-visible text is written in code; the keys
/// are resolved by the contract's loader, the same way Core's own layer texts are.
/// </summary>
internal static class SmbTexts
{
    public const string ResourceName = "PolyMount.Plugin.Smb.Texts";

    private static readonly PluginTexts Texts = PluginTexts.Load(typeof(SmbTexts), ResourceName);

    public static LocalizedText Get(string key) => Texts.Get(key);

    /// <summary>Every language a key was written in, for a preset's title.</summary>
    public static Dictionary<string, string> All(string key) => Texts.All(key);

    /// <summary>The table itself, so a test can check that every key is written in the languages
    /// the product speaks.</summary>
    public static PluginTexts Table => Texts;
}
