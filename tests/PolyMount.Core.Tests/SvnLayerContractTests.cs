using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Plugins.Svn;

namespace PolyMount.Core.Tests;

[TestClass]
public class SvnLayerContractTests
{
    // Regression guard for BUG-B: SvnLayer must implement IRevisionSource, otherwise
    // SourceRegistry.Create never pins a revision and svn lists revision 0 (empty repo).
    [TestMethod]
    public void SvnLayer_implements_IRevisionSource()
    {
        Assert.IsTrue(typeof(IRevisionSource).IsAssignableFrom(typeof(SvnLayer)),
            "SvnLayer must implement IRevisionSource so SourceRegistry pins HEAD on creation");
    }
}
