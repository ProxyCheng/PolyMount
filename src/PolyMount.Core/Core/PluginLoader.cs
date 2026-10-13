using System.Runtime.Loader;

namespace PolyMount.Core;

public interface ILayerPlugin { void Register(); }

/// <summary>Only assemblies explicitly enabled in configuration are loaded.</summary>
public static class PluginLoader
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> PluginTypes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PluginPresets = new(StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyList<string> LoadConfigured(IEnumerable<PluginConfig> plugins, string baseDirectory)
    {
        lock (Gate)
        {
        foreach (var type in PluginTypes) { SourceRegistry.Unregister(type); LayerRegistry.Unregister(type); }
        PluginTypes.Clear();
        foreach (var id in PluginPresets) LayerPresetRegistry.Unregister(id);
        PluginPresets.Clear();
        var presetBaseline = LayerPresetRegistry.All.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseline = LayerRegistry.Types.ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                foreach (var type in types) ((ILayerPlugin)Activator.CreateInstance(type)!).Register();
            }
            catch (Exception ex) { errors.Add(plugin.AssemblyPath + ": " + ex.Message); }
        }
        foreach (var type in LayerRegistry.Types.Where(t => !baseline.Contains(t))) PluginTypes.Add(type);
        foreach (var preset in LayerPresetRegistry.All.Where(p => !presetBaseline.Contains(p.Id))) PluginPresets.Add(preset.Id);
        return errors;
        }
    }
}
