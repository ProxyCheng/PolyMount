using PolyMount.Plugins;

namespace PolyMount.Plugin.Testing;

/// <summary>
/// What can be established about a plugin's words without a person reading them.
///
/// A missing translation is invisible in the language it was written in and obvious in the other
/// one — which is exactly the case nobody runs by hand. The checks here are the mechanical half of
/// the rule that a plugin keeps its text in one embedded table: every key names every language the
/// table names, every one of them has something in it, and the manifest describes the plugin in
/// each language the product speaks. They return sentences rather than throwing, because the test
/// frameworks differ and a report that names every problem at once is worth more than the first
/// one.
/// </summary>
public static class PluginTextChecks
{
    /// <summary>The languages the product itself speaks. A plugin may carry more; missing one of
    /// these is a gap a reader will see.</summary>
    public static readonly IReadOnlyList<string> ProductLanguages = new[] { "en", "zh-CN" };

    /// <summary>A plugin's embedded table and its manifest, checked the same way wherever a test
    /// lives: <paramref name="pluginType"/> is any type of the plugin's assembly,
    /// <paramref name="textResourceName"/> the LogicalName its project gave the text file.</summary>
    public static IReadOnlyList<string> Verify(Type pluginType, string textResourceName)
    {
        var problems = new List<string>(Verify(PluginTexts.Load(pluginType, textResourceName)));
        string path = pluginType.Assembly.Location;
        var manifest = PluginManifest.Read(path, out string error);
        if (manifest == null) problems.Add("the manifest cannot be read (" + Path.GetFileName(path) + "): " + error);
        else problems.AddRange(VerifyManifest(manifest));
        return problems;
    }

    /// <summary>Every key has text in every language the table declares, and the declared set is
    /// the one the product speaks unless the caller says otherwise.</summary>
    public static IReadOnlyList<string> Verify(PluginTexts texts, IEnumerable<string>? languages = null)
    {
        var problems = new List<string>();
        string[] declared = (languages ?? texts.Languages).ToArray();
        if (texts.Keys.Count == 0) problems.Add("the text table has no keys");
        if (declared.Length == 0) problems.Add("the text table names no languages");
        foreach (string language in declared)
        {
            if (string.IsNullOrWhiteSpace(language)) problems.Add("the text table names an empty language");
        }
        foreach (string key in texts.Keys)
        {
            texts.TryGet(key, out var values);
            foreach (string language in declared)
            {
                if (!values.TryGetValue(language, out string? text) || string.IsNullOrWhiteSpace(text))
                    problems.Add("'" + key + "' has no " + language + " text");
            }
        }
        return problems;
    }

    /// <summary>The manifest describes the plugin in each language the product speaks. Readable
    /// without loading the plugin, which is the point of it being in the manifest at all.</summary>
    public static IReadOnlyList<string> VerifyManifest(PluginManifest? manifest)
    {
        var problems = new List<string>();
        if (manifest == null) { problems.Add("the plugin carries no manifest"); return problems; }
        if (string.IsNullOrWhiteSpace(manifest.Id)) problems.Add("the manifest has no id");
        foreach (string language in ProductLanguages)
        {
            if (string.IsNullOrWhiteSpace(manifest.Name.GetValueOrDefault(language)))
                problems.Add("the manifest has no name for " + language);
            if (string.IsNullOrWhiteSpace(manifest.Description.GetValueOrDefault(language)))
                problems.Add("the manifest has no description for " + language);
        }
        return problems;
    }

    /// <summary>The table in a file — the source-tree text file itself, for a check that should
    /// fail when the file is wrong rather than when the build embedded it.</summary>
    public static IReadOnlyList<string> VerifyFile(string path, IEnumerable<string>? languages = null)
    {
        using var stream = File.OpenRead(path);
        return Verify(PluginTexts.Parse(stream, path), languages);
    }

    /// <summary>A one-line-per-problem report, for a test message.</summary>
    public static string Describe(IReadOnlyList<string> problems) =>
        problems.Count == 0 ? "" : string.Join(Environment.NewLine, problems.Select(problem => "  " + problem));
}
