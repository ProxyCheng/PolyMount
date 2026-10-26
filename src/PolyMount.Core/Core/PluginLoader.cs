using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using PolyMount.Core.Ipc;

namespace PolyMount.Core;

/// <summary>The entry point of a plugin assembly. Everything a plugin contributes is added
/// through the context it is given — layers, sources, presets, prediction strategies and
/// commands — so a plugin is not constrained to being a data source or a layer.</summary>
public interface IPolyMountPlugin { void Register(IPluginContext context); }

/// <summary>The old name of <see cref="IPolyMountPlugin"/>. A plugin no longer has to provide a
/// layer, so the name no longer describes the contract. This alias is kept only so an assembly
/// built against an earlier release still loads; new plugins implement the new interface.</summary>
[Obsolete("Implement IPolyMountPlugin; this alias exists only so older plugin assemblies still load.")]
public interface ILayerPlugin : IPolyMountPlugin { }

/// <summary>What a plugin calls itself in a window. Optional: a plugin that does not implement
/// this is shown by its file name, which is what every plugin looked like before there was
/// anything else to show. The title is the short human name ("SVN repository"); the description
/// is the sentence a reader gets on hover; the icon is one of the tray's line-icon names, so a
/// plugin chooses a mark without shipping a picture — and the scheme still tints it.</summary>
public interface IPluginMetadata
{
    LocalizedText Title { get; }
    LocalizedText Description { get; }
    string Icon { get; }
}

/// <summary>What one configured plugin was, and what it turned out to be: the file the
/// configuration names, whether it is there, whether it loaded, and the extension points it
/// registered. The plugin manager shows the last two; a front-end that only needs the errors
/// reads <see cref="PluginLoadReport.Errors"/>.</summary>
public sealed record PluginLoadResult(
    string AssemblyPath,
    string FullPath,
    bool Enabled,
    bool Present,
    bool Loaded,
    string Version,
    IReadOnlyList<string> SourceTypes,
    IReadOnlyList<string> LayerTypes,
    IReadOnlyList<string> Presets,
    IReadOnlyList<string> Strategies,
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> Operations,
    string? Error,
    LocalizedText? Title = null,
    LocalizedText? Description = null,
    string Icon = "");

/// <summary>The result of the last configuration load: one row per configured plugin, in the
/// order the configuration names them, plus the errors that made the host refuse the set.</summary>
public sealed class PluginLoadReport
{
    public static readonly PluginLoadReport Empty = new(Array.Empty<PluginLoadResult>(), Array.Empty<string>());

    public PluginLoadReport(IReadOnlyList<PluginLoadResult> plugins, IReadOnlyList<string> errors) =>
        (Plugins, Errors) = (plugins, errors);

    public IReadOnlyList<PluginLoadResult> Plugins { get; }
    public IReadOnlyList<string> Errors { get; }
}

/// <summary>Only assemblies explicitly enabled in configuration are loaded.
///
/// Loading is also the moment the loader learns what each assembly contributes: the registries
/// are global, so a plugin's effects are read as the difference its own <c>Register</c> made. The
/// report lives here because this is the only place that sees a plugin arrive and leave.</summary>
public static class PluginLoader
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> PluginTypes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PluginPresets = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PluginStrategies = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PluginCommandNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PluginOperationNames = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Every assembly this process has ever loaded from a plugin path. An assembly that
    /// was loaded once stays loaded for the life of the process — there is no unloading it — and
    /// its file stays locked, which is what makes "replace a plugin while the service runs"
    /// impossible rather than merely unwise.</summary>
    private static readonly HashSet<string> LoadedAssemblies = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>What a plugin called itself the last time it was loaded. A disabled plugin is not
    /// loaded — that is the point of disabling it — so its row would fall back to the file name
    /// unless the name it gave once is remembered here. This is per process: after a restart a
    /// plugin that is still disabled is shown by its file name until it is enabled once.</summary>
    private sealed record Identity(LocalizedText Title, LocalizedText Description, string Icon);
    private static readonly Dictionary<string, Identity> Identities = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Directories a loaded plugin may keep its dependencies in. One resolver serves all
    /// of them, installed once: a handler added per load would accumulate for the life of a
    /// process that reloads plugins on every enable and disable.</summary>
    private static readonly HashSet<string> DependencyDirectories = new(StringComparer.OrdinalIgnoreCase);
    private static bool _resolverInstalled;

    /// <summary>What the last <see cref="LoadConfigured"/> saw. The supervisor answers the plugin
    /// manager from this; a process that has not loaded configuration yet reports empty.</summary>
    public static PluginLoadReport LastReport { get; private set; } = PluginLoadReport.Empty;

    /// <summary>The full paths of plugin assemblies loaded in this process, whether they are
    /// enabled now or not. A file in this set cannot be replaced until the process exits.</summary>
    public static IReadOnlyCollection<string> LoadedPaths { get { lock (Gate) return LoadedAssemblies.ToArray(); } }

    public static IReadOnlyList<string> LoadConfigured(IEnumerable<PluginConfig> plugins, string baseDirectory) =>
        LoadConfiguredDetailed(plugins, baseDirectory).Errors;

    /// <summary>Where a plugin assembly may live: inside the host's own directory or the
    /// <c>plugins</c> folder under it.
    ///
    /// A plugin is code that runs as the service account, with everything that account can reach.
    /// The configuration used to accept any path — an absolute one, a UNC one — and load it, so a
    /// file placed where a lower-privileged account can write became code execution in the
    /// service the moment the configuration named it. The rule is therefore about where the file
    /// is, not who wrote the entry: the installation directory is writable only by an
    /// administrator, and that is the same account that could replace the service itself.</summary>
    public static bool IsInsideHost(string fullPath, string baseDirectory)
    {
        string root;
        try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory)); }
        catch (Exception) { return false; }
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The path a configuration entry resolves to, resolved against the host's directory,
    /// or the reason it may not be used. Nothing is loaded: this is what a configuration is checked
    /// with before any of its code runs.</summary>
    public static string? ResolvePath(string configuredPath, string baseDirectory, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            error = "the plugin entry has no assembly path";
            return null;
        }
        string full;
        try { full = Path.GetFullPath(configuredPath, baseDirectory); }
        catch (Exception ex)
        {
            error = configuredPath + ": the path cannot be resolved: " + ex.Message;
            return null;
        }
        if (!IsInsideHost(full, baseDirectory))
        {
            error = configuredPath + ": a plugin has to be inside the installation directory (" + baseDirectory
                + "); a path outside it would be code from somewhere this installation does not control";
            return null;
        }
        return full;
    }

    /// <summary>Checks every enabled entry's path without loading anything, and returns the errors.
    /// The host asks this before it loads a configuration it was handed, so a document that names
    /// code outside the installation is refused before any of it runs.</summary>
    public static IReadOnlyList<string> CheckPaths(IEnumerable<PluginConfig> plugins, string baseDirectory)
    {
        var errors = new List<string>();
        foreach (var plugin in plugins)
        {
            if (!plugin.Enabled) continue;
            ResolvePath(plugin.AssemblyPath, baseDirectory, out string error);
            if (error.Length > 0) errors.Add(error);
        }
        return errors;
    }

    /// <summary>Loads the enabled plugins and reports on every configured one. A failed plugin is
    /// not fatal here — the caller decides: the host treats the error list as fatal, while the
    /// plugin manager has to be able to show a broken plugin and change it.</summary>
    public static PluginLoadReport LoadConfiguredDetailed(IEnumerable<PluginConfig> plugins, string baseDirectory)
    {
        var configured = plugins.ToList();
        lock (Gate)
        {
            // Everything the previous set registered is taken back before the next set is read:
            // the configuration is the statement about what this process offers, and a plugin that
            // was switched off must not leave its layer type behind.
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

            var pluginContext = new PluginContext();
            var results = new List<PluginLoadResult>(configured.Count);
            var errors = new List<string>();
            var presetBaseline = LayerPresetRegistry.All.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var strategyBaseline = PredictionStrategyRegistry.Names.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var layerBaseline = LayerRegistry.Types.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var plugin in configured)
            {
                string configuredPath = plugin.AssemblyPath ?? "";
                string fullPath;
                // Resolved through the one containment rule, so the loader cannot be the way
                // around what CheckPaths already refused.
                fullPath = ResolvePath(configuredPath, baseDirectory, out string pathError) ?? "";
                bool present = fullPath.Length > 0 && File.Exists(fullPath);
                string version = present ? ReadVersion(fullPath) : "";
                var empty = new List<string>();
                Identities.TryGetValue(fullPath, out var cachedIdentity);
                if (!plugin.Enabled)
                {
                    results.Add(new PluginLoadResult(configuredPath, fullPath, false, present, false, version,
                        empty, empty, empty, empty, empty, empty, null,
                        cachedIdentity?.Title, cachedIdentity?.Description, cachedIdentity?.Icon ?? ""));
                    continue;
                }
                if (pathError.Length > 0)
                {
                    results.Add(new PluginLoadResult(configuredPath, fullPath, true, present, false, version,
                        empty, empty, empty, empty, empty, empty, pathError,
                        cachedIdentity?.Title, cachedIdentity?.Description, cachedIdentity?.Icon ?? ""));
                    errors.Add(pathError);
                    continue;
                }
                if (!present)
                {
                    string error = configuredPath + ": the file does not exist";
                    results.Add(new PluginLoadResult(configuredPath, fullPath, true, false, false, version,
                        empty, empty, empty, empty, empty, empty, error,
                        cachedIdentity?.Title, cachedIdentity?.Description, cachedIdentity?.Icon ?? ""));
                    errors.Add(error);
                    continue;
                }

                var before = Snapshot();
                Identity? identity = null;
                try
                {
                    AddDependencyDirectory(Path.GetDirectoryName(fullPath)!);
                    var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => !a.IsDynamic && a.Location == fullPath)
                        ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
                    LoadedAssemblies.Add(fullPath);
                    var types = assembly.GetTypes().Where(t => !t.IsAbstract && typeof(IPolyMountPlugin).IsAssignableFrom(t)).ToArray();
                    if (types.Length == 0) throw new InvalidOperationException("Assembly exports no IPolyMountPlugin");
                    foreach (var type in types)
                    {
                        object instance = Activator.CreateInstance(type)!;
                        if (identity == null && instance is IPluginMetadata info &&
                            info.Title is { Values.Count: > 0 } title)
                            identity = new Identity(title, info.Description ?? title,
                                string.IsNullOrWhiteSpace(info.Icon) ? "plugin" : info.Icon);
                        ((IPolyMountPlugin)instance).Register(pluginContext);
                    }
                    var after = Snapshot();
                    results.Add(new PluginLoadResult(configuredPath, fullPath, true, true, true,
                        version.Length > 0 ? version : (assembly.GetName().Version?.ToString() ?? ""),
                        Difference(after.Sources, before.Sources),
                        Difference(after.Layers, before.Layers),
                        Difference(after.Presets, before.Presets),
                        Difference(after.Strategies, before.Strategies),
                        Difference(after.Commands, before.Commands),
                        Difference(after.Operations, before.Operations),
                        null,
                        identity?.Title, identity?.Description, identity?.Icon ?? ""));
                }
                catch (Exception ex)
                {
                    string error = configuredPath + ": " + ex.Message;
                    errors.Add(error);
                    results.Add(new PluginLoadResult(configuredPath, fullPath, true, true, false, version,
                        empty, empty, empty, empty, empty, empty, error,
                        identity?.Title, identity?.Description, identity?.Icon ?? ""));
                }
                if (identity != null) Identities[fullPath] = identity;
            }

            foreach (var type in LayerRegistry.Types.Where(t => !layerBaseline.Contains(t))) PluginTypes.Add(type);
            foreach (var preset in LayerPresetRegistry.All.Where(p => !presetBaseline.Contains(p.Id))) PluginPresets.Add(preset.Id);
            foreach (var name in PredictionStrategyRegistry.Names.Where(n => !strategyBaseline.Contains(n))) PluginStrategies.Add(name);
            foreach (var command in PluginCommands.Names) PluginCommandNames.Add(command);
            foreach (var operation in ControlOperations.Added) PluginOperationNames.Add(operation);

            LastReport = new PluginLoadReport(results, errors);
            return LastReport;
        }
    }

    /// <summary>The DLLs in the host's plugins folder that the configuration does not name. They
    /// are what the plugin manager offers to add: a file someone copied there by hand, or a
    /// bundled plugin this installation does not enable.</summary>
    public static IReadOnlyList<string> DiscoverFiles(IEnumerable<PluginConfig> configured, string baseDirectory)
    {
        string directory = Path.Combine(baseDirectory, "plugins");
        if (!Directory.Exists(directory)) return Array.Empty<string>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in configured)
        {
            if (string.IsNullOrWhiteSpace(plugin.AssemblyPath)) continue;
            try { known.Add(Path.GetFullPath(plugin.AssemblyPath, baseDirectory)); } catch (Exception) { }
        }
        return Directory.EnumerateFiles(directory, "*.dll")
            .Where(path => !Path.GetFileName(path).EndsWith(".Tests.dll", StringComparison.OrdinalIgnoreCase))
            .Where(path => !known.Contains(Path.GetFullPath(path)))
            // The folder also holds the dependencies a plugin brought with it (SQLite, Serilog,
            // the WinFsp binding). They are not things to add, and a list that offers them is a
            // list that invites an error. Reading the metadata is enough; nothing is loaded.
            .Where(IsPluginAssembly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Whether an assembly declares a type implementing the plugin contract, read from
    /// its metadata without loading it. Used only to decide what the plugin folder offers: the
    /// loader itself still takes exactly the assemblies the configuration names.</summary>
    public static bool IsPluginAssembly(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            if (!reader.HasMetadata) return false;
            var metadata = reader.GetMetadataReader();
            foreach (var handle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(handle);
                // The contract itself extends nothing, but the obsolete alias is an interface
                // extending it, and an interface is not something the loader can instantiate.
                if ((type.Attributes & (System.Reflection.TypeAttributes.Interface | System.Reflection.TypeAttributes.Abstract)) != 0)
                    continue;
                foreach (var implementation in type.GetInterfaceImplementations())
                {
                    var target = metadata.GetInterfaceImplementation(implementation).Interface;
                    string? name = target.Kind switch
                    {
                        HandleKind.TypeReference =>
                            metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)target).Name),
                        HandleKind.TypeDefinition =>
                            metadata.GetString(metadata.GetTypeDefinition((TypeDefinitionHandle)target).Name),
                        _ => null
                    };
                    if (name is "IPolyMountPlugin" or "ILayerPlugin") return true;
                }
            }
            return false;
        }
        catch (Exception) { return false; }
    }

    /// <summary>The assembly version a plugin file carries, or empty when it cannot be read (a
    /// running process may hold it, and a malformed file has none).</summary>
    public static string ReadVersion(string path)
    {
        try { return AssemblyName.GetAssemblyName(path).Version?.ToString() ?? ""; }
        catch (Exception) { return ""; }
    }

    /// <summary>Lets a plugin load a dependency that sits beside it. One handler for every
    /// directory any plugin has ever come from: the directories are small in number and the
    /// handler is called only when the default context cannot resolve a name.</summary>
    private static void AddDependencyDirectory(string directory)
    {
        lock (Gate)
        {
            if (!DependencyDirectories.Add(directory) || _resolverInstalled) return;
            _resolverInstalled = true;
            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                string[] directories;
                lock (Gate) directories = DependencyDirectories.ToArray();
                foreach (string candidate in directories)
                {
                    string dependency = Path.Combine(candidate, name.Name + ".dll");
                    if (File.Exists(dependency)) return context.LoadFromAssemblyPath(dependency);
                }
                return null;
            };
        }
    }

    // ---- registry snapshots ------------------------------------------------------------

    private sealed record RegistrySnapshot(
        HashSet<string> Sources, HashSet<string> Layers, HashSet<string> Presets,
        HashSet<string> Strategies, HashSet<string> Commands, HashSet<string> Operations);

    private static RegistrySnapshot Snapshot() => new(
        SourceRegistry.Types.ToHashSet(StringComparer.OrdinalIgnoreCase),
        LayerTypes().ToHashSet(StringComparer.OrdinalIgnoreCase),
        LayerPresetRegistry.All.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase),
        PredictionStrategyRegistry.Names.ToHashSet(StringComparer.OrdinalIgnoreCase),
        PluginCommands.Names.ToHashSet(StringComparer.OrdinalIgnoreCase),
        ControlOperations.Added.ToHashSet(StringComparer.Ordinal));

    private static IEnumerable<string> LayerTypes() =>
        LayerRegistry.Types.Where(type => !SourceRegistry.Contains(type));

    private static IReadOnlyList<string> Difference(HashSet<string> after, HashSet<string> before) =>
        after.Where(item => !before.Contains(item)).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
}
