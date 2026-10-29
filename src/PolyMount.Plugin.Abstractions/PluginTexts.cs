using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace PolyMount.Plugins;

/// <summary>
/// A plugin's user-visible text, read from a resource inside its own assembly.
///
/// Every string a person reads about a plugin — a layer's title, the sentence under a field, the
/// name of a preset, why a credential kind exists — belongs in one text table beside the code, not
/// in it: a line of C# can hold one language, and the product speaks two. The table is an embedded
/// resource, so a plugin is still one file to hand out; it has one entry per string:
/// <code>
/// { "svn.title": { "en": "SVN repository", "zh-CN": "SVN 仓库" } }
/// </code>
/// The format is the one <c>plugin.json</c> already uses for its own two strings, and the one
/// Core's built-in layer texts use, so a reader who has seen one has seen all of them.
///
/// A key that is not in the table is answered with the key itself. A missing translation is then
/// visible — it reads like <c>svn.field.url</c> in the window — instead of blanking a form or
/// failing to load the plugin, and it is the same failure whether the whole table is missing an
/// entry or the code asked for a name that was never written. Completeness is a test's job
/// (<c>PluginTextChecks</c>), not a load-time exception's.
/// </summary>
public sealed class PluginTexts
{
    private readonly Dictionary<string, Dictionary<string, string>> _texts;

    private PluginTexts(Dictionary<string, Dictionary<string, string>> texts)
    {
        _texts = texts;
        Keys = texts.Keys.ToArray();
        // The product's own two first, so a table written in another order still lists them that
        // way; anything else a plugin brings sorts after.
        Languages = texts.Values.SelectMany(values => values.Keys).Distinct(StringComparer.Ordinal)
            .OrderBy(language => language switch { "en" => 0, "zh-CN" => 1, _ => 2 })
            .ThenBy(language => language, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>A table with no entries: every key answers with itself.</summary>
    public static PluginTexts Empty { get; } = new(new(StringComparer.Ordinal));

    /// <summary>Every key the table holds, in the order the file lists them.</summary>
    public IReadOnlyList<string> Keys { get; }

    /// <summary>The languages any key in the table names — not every key names every one of them;
    /// <see cref="PluginTextChecks.Verify(PluginTexts, IEnumerable{string}?)"/> is what says so.</summary>
    public IReadOnlyList<string> Languages { get; }

    /// <summary>The text table a plugin embeds. <paramref name="anchor"/> is any type of the
    /// plugin's assembly; <paramref name="resourceName"/> is the <c>LogicalName</c> the project
    /// gave the resource.</summary>
    public static PluginTexts Load(Type anchor, string resourceName) => Load(anchor.Assembly, resourceName);

    public static PluginTexts Load(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                assembly.GetName().Name + " has no resource named '" + resourceName +
                "'. Mark the text file as an EmbeddedResource with that LogicalName in the project that builds the plugin.");
        return Parse(stream, resourceName);
    }

    /// <summary>Reads a table from a stream — a test reading the file from the source tree uses
    /// this, so what it checks is the file that gets embedded and not a copy of it.</summary>
    public static PluginTexts Parse(Stream stream, string source = "plugin texts")
    {
        Dictionary<string, Dictionary<string, string>>? texts;
        try { texts = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream); }
        catch (JsonException ex) { throw new InvalidDataException(source + " is not a text table: " + ex.Message, ex); }
        if (texts == null) throw new InvalidDataException(source + " is empty");
        return new PluginTexts(new(texts, StringComparer.Ordinal));
    }

    /// <summary>The text for a key, or the key itself when the table has none.</summary>
    public LocalizedText Get(string key) => _texts.TryGetValue(key, out var values)
        ? new LocalizedText { Values = new(values, StringComparer.Ordinal) }
        : new LocalizedText(key);

    /// <summary>A sentence with values in it — <c>"'{0}' has no {1} text"</c> — filled in every
    /// language at once, so the call site holds no words in either of them.
    ///
    /// The placeholders are .NET's own and numbers are formatted invariantly: a count reads the
    /// same in a log, a test and a window, and a value that needs the reader's culture is the
    /// caller's to format before it gets here. A format string with a stray brace is text, not a
    /// failure — it is shown as written rather than taking a page down.</summary>
    public LocalizedText Format(string key, params object[] args)
    {
        var values = All(key);
        if (args.Length == 0) return new LocalizedText { Values = values };
        var formatted = new Dictionary<string, string>(values.Count, StringComparer.Ordinal);
        foreach (var (language, text) in values)
        {
            try { formatted[language] = string.Format(CultureInfo.InvariantCulture, text, args); }
            catch (FormatException) { formatted[language] = text; }
        }
        return new LocalizedText { Values = formatted };
    }

    /// <summary>The languages a key was written in, for a caller that wants them all at once (a
    /// preset's title is a <see cref="Dictionary{TKey,TValue}"/>, not a
    /// <see cref="LocalizedText"/>).</summary>
    public Dictionary<string, string> All(string key) => _texts.TryGetValue(key, out var values)
        ? new(values, StringComparer.Ordinal)
        : new() { ["en"] = key, ["zh-CN"] = key };

    public bool Contains(string key) => _texts.ContainsKey(key);

    /// <summary>The languages a key was written in, and whether the table has it at all.</summary>
    public bool TryGet(string key, out Dictionary<string, string> values) => _texts.TryGetValue(key, out values!);
}
