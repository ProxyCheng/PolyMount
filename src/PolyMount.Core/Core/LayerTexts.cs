namespace PolyMount.Core;

/// <summary>
/// The product's own layer text — the built-in descriptors' titles, descriptions and field labels.
///
/// It is read exactly the way a plugin's text is, through the contract's <see cref="PluginTexts"/>:
/// one table, one loader, so "where does this sentence come from" has the same answer inside the
/// product and outside it. The file is <c>Resources/layer-texts.json</c>, embedded as
/// <c>PolyMount.LayerTexts</c>.
/// </summary>
internal static class LayerTexts
{
    public const string ResourceName = "PolyMount.LayerTexts";

    private static readonly PluginTexts Texts = PluginTexts.Load(typeof(LayerTexts), ResourceName);

    public static LocalizedText Get(string key) => Texts.Get(key);

    /// <summary>The table itself, so a test can check that every key is written in the languages
    /// the product speaks.</summary>
    public static PluginTexts Table => Texts;
}
