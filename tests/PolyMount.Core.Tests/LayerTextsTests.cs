using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Plugin.Testing;

namespace PolyMount.Core.Tests;

/// <summary>
/// The product's own layer text, held to the same standard a plugin's is: one embedded table, every
/// key in every language, and a missing key that reads rather than blanks a form.
/// </summary>
[TestClass]
public class LayerTextsTests
{
    [TestMethod]
    public void Every_built_in_layer_string_is_written_in_both_languages()
    {
        var problems = PluginTextChecks.Verify(LayerTexts.Table);
        Assert.AreEqual(0, problems.Count, PluginTextChecks.Describe(problems));
    }

    [TestMethod]
    public void A_key_the_table_does_not_have_reads_as_the_key()
    {
        // A typo in a key is a missing sentence, not a plugin that fails to load and not an empty
        // label: showing the key is what makes it findable and what a translation tool sees.
        var text = LayerTexts.Get("no.such.key");
        Assert.AreEqual("no.such.key", text.Resolve("en"));
        Assert.AreEqual("no.such.key", text.Resolve("zh-CN"));
        Assert.AreEqual("no.such.key", text.Resolve("de"));
    }
}
