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
            await WaitForLog(path, "PENDING id=1 parent=0");
            string pending = ReadLog(path);
            StringAssert.Contains(pending, "PENDING id=1 parent=0");
            StringAssert.Contains(pending, "stage='waiting VFS lock'");
            StringAssert.Contains(pending, "BEGIN id=2 parent=1");
            StringAssert.Contains(pending, "END id=2 parent=1");
            Assert.IsFalse(pending.Contains("END id=1 parent=0"));
        }
        diagnostics.Flush();
        await WaitForLog(path, "END id=1 parent=0");
        StringAssert.Contains(ReadLog(path), "END id=1 parent=0");
        Assert.IsNull(RequestDiagnostics.Begin("untraced", "folder"));
    }
    private static async Task WaitForLog(string path, string text)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) && ReadLog(path).Contains(text)) return;
            await Task.Delay(20);
        }
        Assert.Fail("Asynchronous log was not written: " + text);
    }

    /// <summary>An export bundles every segment of an instance's diagnostics. Serilog names the
    /// rolled ones by sequence, so the family is matched rather than one expected name.</summary>
    [TestMethod]
    public void Diagnostics_log_family_includes_rolled_segments_and_nothing_else()
    {
        using var temp = new TempDir();
        var profile = new MountProfile { Name = "test", StoreRoot = temp.Sub("store"), Layers = new() { new() { Id = "local", Type = "overlay" } } };
        using var mount = new MountInstance(profile);
        string state = Path.Combine(temp.Sub("store"), "state");
        File.WriteAllText(Path.Combine(state, "requests_183.log"), "rolled");
        File.WriteAllText(Path.Combine(state, "pin.txt"), "795700");
        File.WriteAllText(Path.Combine(state, "listing-cache.log"), "not diagnostics");

        var names = mount.GetLogFiles().Select(Path.GetFileName).ToList();
        CollectionAssert.Contains(names, "requests_183.log");
        Assert.IsFalse(names.Contains("pin.txt"));
        Assert.IsFalse(names.Contains("listing-cache.log"));
        Assert.IsTrue(names.All(name => name!.StartsWith("requests", StringComparison.Ordinal)), string.Join(", ", names));
    }
    private static string ReadLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
