using PolyMount.Core;
using PolyMount.Plugin.Testing;

namespace PolyMount.Plugins.Svn;

/// <summary>Tests exported by a separate test DLL; never shipped with the production plugin.</summary>
public sealed class SvnPluginTests : IPluginTestProvider
{
    public string PluginName => "svn";
    public Task<IReadOnlyList<PluginTestResult>> RunAsync(PluginTestContext context)
    {
        var results = new List<PluginTestResult>();
        void Check(string name, Action test)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            try { test(); results.Add(new(PluginName, name, PluginTestOutcome.Passed)); }
            catch (Exception ex) { results.Add(new(PluginName, name, PluginTestOutcome.Failed, ex.Message)); }
        }
        Check("configured-loading-and-preset", () =>
        {
            var path = typeof(SvnLayerPlugin).Assembly.Location;
            var errors = PluginLoader.LoadConfigured(new[] { new PluginConfig { AssemblyPath = path } }, AppContext.BaseDirectory);
            if (errors.Count > 0) throw new Exception(string.Join("; ", errors));
            if (!LayerPresetRegistry.All.Any(p => p.Id == "svn-workspace")) throw new Exception("Missing plugin preset");
            if (typeof(SvnLayer).Assembly == typeof(ILayer).Assembly) throw new Exception("Plugin is not independent");
            PluginLoader.LoadConfigured(Array.Empty<PluginConfig>(), AppContext.BaseDirectory);
            if (SourceRegistry.Contains("svn") || LayerPresetRegistry.All.Any(p => p.Id == "svn-workspace")) throw new Exception("Disabled plugin registrations remain active");
            PluginLoader.LoadConfigured(new[] { new PluginConfig { AssemblyPath = path } }, AppContext.BaseDirectory);
        });
        Check("registration", () => { if (!SourceRegistry.Contains("svn")) throw new Exception("SVN was not registered"); });
        Check("readonly-contract", () =>
        {
            var layer = SvnLayer.Create("https://example.invalid/repo", "", Path.Combine(context.WorkingDirectory, "pin.txt"));
            if (layer.Writable || ((IObjectLayer)layer).TryApply(Array.Empty<ObjectChange>())) throw new Exception("Read-only SVN accepted a write");
            if (layer is not IRevisionSource) throw new Exception("Missing revision capability");
        });
        Check("invalid-parameters", () =>
        {
            try { SourceRegistry.Validate("svn", new SourceConfig { Url = "not-a-url" }); }
            catch (ArgumentException) { return; }
            throw new Exception("Invalid URL was accepted");
        });
        if (context.Parameters.TryGetValue("svn.repository", out var repository))
            Check("repository-integration", () =>
            {
                var layer = SourceRegistry.Create(new SourceConfig { Type = "svn", Url = repository,
                    Username = context.Parameters.GetValueOrDefault("svn.username"), Password = context.Parameters.GetValueOrDefault("svn.password") }, context.WorkingDirectory);
                if (!((IObjectLayer)layer).TryReadObject("", out var root) || !root.Header.IsDir || root.Children == null)
                    throw new Exception("Repository root did not produce a directory object");
                foreach (var child in root.Children)
                    if (!layer.TryGetEntry(child.Name.ToUpperInvariant(), out var header) || header.Name != child.Name)
                        throw new Exception("Case-insensitive child header lookup failed");
                var directory = root.Children.FirstOrDefault(c => c.IsDir);
                if (directory != null)
                {
                    var canonical = layer.ListOwn(directory.Name);
                    var alternate = layer.ListOwn(directory.Name.ToUpperInvariant());
                    if (!canonical.Select(c => c.Name).SequenceEqual(alternate.Select(c => c.Name)))
                        throw new Exception("Case-insensitive directory listing failed");
                }
            });
        else results.Add(new(PluginName, "repository-integration", PluginTestOutcome.Skipped, "Set svn.repository to enable network testing"));
        return Task.FromResult<IReadOnlyList<PluginTestResult>>(results);
    }
}
