using System.Runtime.Loader;
using PolyMount.Core.Ipc;

namespace PolyMount.Core;

/// <summary>The entry point of a plugin assembly. Everything a plugin contributes is added
/// through the context it is given — layers, sources, presets, prediction strategies and
/// commands — so a plugin is not constrained to being a data source.</summary>
public interface ILayerPlugin { void Register(IPluginContext context); }

/// <summary>Only assemblies explicitly enabled in configuration are loaded.</summary>
public static class PluginLoader
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> PluginTypes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PluginPresets = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PluginStrategies = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PluginCommandNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PluginOperationNames = new(StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyList<string> LoadConfigured(IEnumerable<PluginConfig> plugins, string baseDirectory)
    {
        lock (Gate)
        {
        foreach (var type in PluginTypes) { SourceRegistry.Unregister(type); LayerRegistry.Unregister(type); }
        PluginTypes.Clear();
        foreach (var id in PluginPresets) LayerPresetRegistry.Unregister(id);
        PluginPresets.Clear();
        foreach (var name in PluginStrategies) PredictionStrategyRegistry.Unregister(name);
        PluginStrategies.Clear();
        foreach (var command in PluginCommandNames) PluginCommands.Unregister(command);
        PluginCommandNames.Clear();
        foreach (var operation in PluginOperationNames) ControlOperations.Unregister(operation);
        PluginOperationNames.Clear();
        var presetBaseline = LayerPresetRegistry.All.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var strategyBaseline = PredictionStrategyRegistry.Names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseline = LayerRegistry.Types.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pluginContext = new PluginContext();
        var errors = new List<string>();
        foreach (var plugin in plugins.Where(p => p.Enabled))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(plugin.AssemblyPath)) throw new ArgumentException("Plugin assembly path is empty");
                string path = Path.GetFullPath(plugin.AssemblyPath, baseDirectory);
                AssemblyLoadContext.Default.Resolving += (context, name) =>
                {
                    string dependency = Path.Combine(Path.GetDirectoryName(path)!, name.Name + ".dll");
                    return File.Exists(dependency) ? context.LoadFromAssemblyPath(dependency) : null;
                };
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => !a.IsDynamic && a.Location == path)
                    ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                var types = assembly.GetTypes().Where(t => !t.IsAbstract && typeof(ILayerPlugin).IsAssignableFrom(t)).ToArray();
                if (types.Length == 0) throw new InvalidOperationException("Assembly exports no ILayerPlugin");
                foreach (var type in types) ((ILayerPlugin)Activator.CreateInstance(type)!).Register(pluginContext);
            }
            catch (Exception ex) { errors.Add(plugin.AssemblyPath + ": " + ex.Message); }
        }
        foreach (var type in LayerRegistry.Types.Where(t => !baseline.Contains(t))) PluginTypes.Add(type);
        foreach (var preset in LayerPresetRegistry.All.Where(p => !presetBaseline.Contains(p.Id))) PluginPresets.Add(preset.Id);
        foreach (var name in PredictionStrategyRegistry.Names.Where(n => !strategyBaseline.Contains(n))) PluginStrategies.Add(name);
        // Every command in the registry came from a plugin: Core registers none of its own.
        foreach (var command in PluginCommands.Names) PluginCommandNames.Add(command);
        foreach (var operation in ControlOperations.Added) PluginOperationNames.Add(operation);
        return errors;
        }
    }
}
