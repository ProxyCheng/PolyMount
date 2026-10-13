using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class RequestDiagnosticsTests
{
    [TestMethod]
    public async Task Unfinished_request_reports_wait_stage_and_nested_operations()
    {
        using var temp = new TempDir();
        string path = Path.Combine(temp.Path, "requests.log");
        using var diagnostics = new RequestDiagnostics(path);
        using (var outer = RequestDiagnostics.Begin("VFS.List", "folder", diagnostics))
        {
            outer!.Stage("waiting VFS lock");
            using (RequestDiagnostics.Begin("Layer.ReadObject:svn", "folder")) { }
            await Task.Delay(1100);
            diagnostics.Flush();
            string pending = File.ReadAllText(path);
            StringAssert.Contains(pending, "PENDING id=1 parent=0");
            StringAssert.Contains(pending, "stage='waiting VFS lock'");
            StringAssert.Contains(pending, "BEGIN id=2 parent=1");
            StringAssert.Contains(pending, "END id=2 parent=1");
            Assert.IsFalse(pending.Contains("END id=1 parent=0"));
        }
        diagnostics.Flush();
        StringAssert.Contains(File.ReadAllText(path), "END id=1 parent=0");
        Assert.IsNull(RequestDiagnostics.Begin("untraced", "folder"));
    }
}
