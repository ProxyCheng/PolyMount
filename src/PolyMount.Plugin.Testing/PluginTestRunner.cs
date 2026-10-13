using System.Reflection;
using System.Runtime.Loader;

namespace PolyMount.Plugin.Testing;

public sealed record PluginTestContext(string WorkingDirectory, IReadOnlyDictionary<string, string> Parameters, CancellationToken CancellationToken);
public enum PluginTestOutcome { Passed, Failed, Skipped }
public sealed record PluginTestResult(string Plugin, string Name, PluginTestOutcome Outcome, string? Message = null);

/// <summary>Plugins expose their own tests without referencing any host-specific unit test framework.</summary>
public interface IPluginTestProvider
{
    string PluginName { get; }
    Task<IReadOnlyList<PluginTestResult>> RunAsync(PluginTestContext context);
}

public static class PluginTestRunner
{
    public static async Task<IReadOnlyList<PluginTestResult>> RunDirectoryAsync(string directory, PluginTestContext context)
    {
        // The test host resolves plugin dependencies beside the test assemblies, not
        // from the production host or its plugin directory.
        AssemblyLoadContext.Default.Resolving += ResolveDependency;
        Assembly? ResolveDependency(AssemblyLoadContext loadContext, AssemblyName name)
        {
            string file = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(file) ? loadContext.LoadFromAssemblyPath(Path.GetFullPath(file)) : null;
        }
        try
        {
        Directory.CreateDirectory(context.WorkingDirectory);
        var results = new List<PluginTestResult>();
        foreach (var file in Directory.EnumerateFiles(directory, "PolyMount.Plugin.*.Tests.dll"))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            try
            {
                string path = Path.GetFullPath(file);
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => !a.IsDynamic && a.Location == path)
                    ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                foreach (var type in assembly.GetTypes().Where(t => !t.IsAbstract && typeof(IPluginTestProvider).IsAssignableFrom(t)))
                {
                    var provider = (IPluginTestProvider)Activator.CreateInstance(type)!;
                    try { results.AddRange(await provider.RunAsync(context)); }
                    catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) { results.Add(new(provider.PluginName, "test-provider", PluginTestOutcome.Failed, ex.ToString())); }
                }
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { results.Add(new(Path.GetFileName(file), "discovery", PluginTestOutcome.Failed, ex.ToString())); }
        }
        return results;
        }
        finally { AssemblyLoadContext.Default.Resolving -= ResolveDependency; }
    }
}
