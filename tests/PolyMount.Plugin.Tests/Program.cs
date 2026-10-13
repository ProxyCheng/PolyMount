using PolyMount.Plugin.Testing;

if (args.Length == 0) { Console.Error.WriteLine("Usage: PolyMount.Plugin.Tests <plugin-directory> [key=value ...]"); return 2; }
string scratch = Path.Combine(Path.GetTempPath(), "PolyMount-plugin-tests-" + Guid.NewGuid().ToString("N"));
var parameters = args.Skip(1).Select(a => a.Split('=', 2)).ToDictionary(a => a[0], a => a.Length == 2 ? a[1] : "");
try
{
    var results = await PluginTestRunner.RunDirectoryAsync(args[0], new(scratch, parameters, CancellationToken.None));
    foreach (var result in results) Console.WriteLine($"{result.Outcome}: {result.Plugin}/{result.Name} {result.Message}");
    return results.Count == 0 || results.Any(r => r.Outcome == PluginTestOutcome.Failed) ? 1 : 0;
}
finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
