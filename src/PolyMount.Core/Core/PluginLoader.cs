using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using PolyMount.Core.Ipc;

namespace PolyMount.Core;

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
    string Icon = "",
    string ApiVersion = "",
    PluginManifest? Manifest = null,
    string? ManifestError = null,
    // The credential kinds this plugin contributed. Last and nullable so the rows built before
    // kinds existed keep their meaning; a reader treats null as none.
    IReadOnlyList<string>? CredentialKinds = null,
    // The management pages this plugin offers, by id. Null for a plugin that offers none, and for
    // every row built before panels existed.
    IReadOnlyList<string>? Panels = null,
    // Where the plugin says its code belongs, and whether this process needed it. Skipped is not
    // a failure: a worker has no use for a plugin that only offers tools, and a front-end has none
    // for a source. The supervisor asks for both, so the list it reports is complete.
    PluginRoles Roles = PluginRoles.Both,
    bool Skipped = false);

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
    private static readonly HashSet<string> PluginCredentialKinds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PluginCredentialConsumers = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>Assemblies named on the command line rather than in the configuration, as the
    /// MCP server's <c>--plugin</c> does. They are loaded after the configured set and every rule
    /// about them is the configured set's rule, except that only a development host may name one
    /// outside its own directory.</summary>
    public static IReadOnlyList<string> ExtraPaths { get; set; } = Array.Empty<string>();

    /// <summary>Whether an assembly from outside the host's directory may be loaded at all. Only a
    /// development host sets it: the containment rule is what stops a file some lower-privileged
    /// account can write from becoming code in the service.</summary>
    public static bool AllowExternalPlugins { get; set; }

    /// <summary>What the last <see cref="LoadConfigured"/> saw. The supervisor answers the plugin
    /// manager from this; a process that has not loaded configuration yet reports empty.</summary>
    public static PluginLoadReport LastReport { get; private set; } = PluginLoadReport.Empty;

    /// <summary>The full paths of plugin assemblies loaded in this process, whether they are
    /// enabled now or not. A file in this set cannot be replaced until the process exits.</summary>
    public static IReadOnlyCollection<string> LoadedPaths { get { lock (Gate) return LoadedAssemblies.ToArray(); } }

    public static IReadOnlyList<string> LoadConfigured(IEnumerable<PluginConfig> plugins, string baseDirectory,
        PluginRoles wanted = PluginRoles.Both) =>
        LoadConfiguredDetailed(plugins, baseDirectory, wanted).Errors;

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
        if (!AllowExternalPlugins && !IsInsideHost(full, baseDirectory))
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
    /// <param name="wanted">What this process needs. A plugin whose manifest declares none of it
    /// is reported and not loaded — see <see cref="PluginRoles"/>.</param>
    public static PluginLoadReport LoadConfiguredDetailed(IEnumerable<PluginConfig> plugins, string baseDirectory,
        PluginRoles wanted = PluginRoles.Both)
    {
        var configured = plugins.ToList();
        // Command-line assemblies join the configured ones, last and enabled. A path that is also in
        // the configuration is not added twice: the configuration already says what to do with it.
        if (ExtraPaths.Count > 0)
        {
            foreach (string path in ExtraPaths)
                if (!configured.Any(entry => SamePath(entry.AssemblyPath, path, baseDirectory)))
                    configured.Add(new PluginConfig { AssemblyPath = path, Enabled = true });
        }
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
            foreach (var kind in PluginCredentialKinds) CredentialKinds.Unregister(kind);
            PluginCredentialKinds.Clear();
            // A layer type that is gone must not keep saying which credentials it would present:
            // the pickers would offer accounts for a layer that no longer exists.
            foreach (var type in PluginCredentialConsumers) CredentialConsumers.Unregister(type);
            PluginCredentialConsumers.Clear();
            foreach (var operation in PluginOperationNames) ControlOperations.Unregister(operation);
            PluginOperationNames.Clear();
            PluginNotices.Clear();
            PluginPanels.Clear();

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
                // Read before anything is decided: a disabled plugin is not loaded, and its row
                // still has to say what it is. A manifest that cannot be used is a refusal — an
                // enabled plugin whose manifest is broken is not half-loaded.
                string manifestError = "";
                PluginManifest? manifest = present ? PluginManifest.Read(fullPath, out manifestError) : null;
                if (manifestError.Length > 0) manifestError = configuredPath + ": " + manifestError;
                LocalizedText? title = manifest?.Title ?? cachedIdentity?.Title;
                LocalizedText? detail = manifest?.Detail ?? cachedIdentity?.Description;
                string icon = manifest?.Icon ?? cachedIdentity?.Icon ?? "";
                // Read from the file, before any branch decides whether to load it: a row has to
                // answer "which contract was this built against" even for a plugin this process
                // will not load — disabled, or built against another major.
                string apiVersion = present ? ReadApiVersion(fullPath) : "";
                if (!plugin.Enabled)
                {
                    results.Add(new PluginLoadResult(configuredPath, fullPath, false, present, false, version,
                        empty, empty, empty, empty, empty, empty, null,
                        title, detail, icon, apiVersion, manifest, EmptyOrNull(manifestError)));
                    continue;
                }
                if (pathError.Length > 0)
                {
                    results.Add(new PluginLoadResult(configuredPath, fullPath, true, present, false, version,
                        empty, empty, empty, empty, empty, empty, pathError,
                        title, detail, icon, apiVersion, manifest, EmptyOrNull(manifestError)));
                    errors.Add(pathError);
                    continue;
                }
                if (!present)
                {
                    string error = configuredPath + ": the file does not exist";
                    results.Add(new PluginLoadResult(configuredPath, fullPath, true, false, false, version,
                        empty, empty, empty, empty, empty, empty, error,
                        title, detail, icon, apiVersion, manifest, EmptyOrNull(manifestError)));
                    errors.Add(error);
                    continue;
                }
                if (manifest != null && (manifest.RoleSet & wanted) == 0)
                {
                    // Declared for another process. A row anyway: the report is what a front end
                    // reads, and "this plugin exists and is fine, this process just does not run
                    // it" is different from "this plugin is broken".
                    results.Add(new PluginLoadResult(configuredPath, fullPath, true, true, false, version,
                        empty, empty, empty, empty, empty, empty, null,
                        title, detail, icon, apiVersion, manifest, null,
                        Roles: manifest.RoleSet, Skipped: true));
                    continue;
                }
                if (manifestError.Length > 0)
                {
                    // A manifest that names no id, or cannot be parsed, is not a detail to put in a
                    // tooltip: the plugin page reads this row to describe the plugin, and a row it
                    // cannot describe is a plugin nobody can decide about. Refused before the
                    // assembly is touched.
                    results.Add(new PluginLoadResult(configuredPath, fullPath, true, true, false, version,
                        empty, empty, empty, empty, empty, empty, manifestError,
                        title, detail, icon, apiVersion, manifest, manifestError));
                    errors.Add(manifestError);
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
                    // What the plugin was compiled against, before any of its types are touched:
                    // an assembly built against a newer contract fails inside GetTypes, and the
                    // message a type loader gives then names a member rather than the mismatch.
                    apiVersion = ReadApiVersion(assembly);
                    if (ApiMismatch(apiVersion) is { } mismatch) throw new InvalidOperationException(mismatch);
                    var types = assembly.GetTypes().Where(t => !t.IsAbstract && typeof(IPolyMountPlugin).IsAssignableFrom(t)).ToArray();
                    if (types.Length == 0) throw new InvalidOperationException("Assembly exports no IPolyMountPlugin");
                    foreach (var type in types)
                    {
                        object instance = Activator.CreateInstance(type)!;
                        if (identity == null && instance is IPluginMetadata info &&
                            info.Title is { Values.Count: > 0 } declared)
                            identity = new Identity(declared, info.Description ?? declared,
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
                        identity?.Title ?? title, identity?.Description ?? detail, identity?.Icon ?? icon,
                        apiVersion, manifest, EmptyOrNull(manifestError),
                        Difference(after.CredentialKinds, before.CredentialKinds),
                        Difference(after.Panels, before.Panels),
                        manifest?.RoleSet ?? PluginRoles.Both));
                }
                catch (Exception ex)
                {
                    string error = configuredPath + ": " + Explain(ex);
                    errors.Add(error);
                    results.Add(new PluginLoadResult(configuredPath, fullPath, true, true, false, version,
                        empty, empty, empty, empty, empty, empty, error,
                        identity?.Title ?? title, identity?.Description ?? detail, identity?.Icon ?? icon,
                        apiVersion, manifest, EmptyOrNull(manifestError)));
                }
                if (identity != null) Identities[fullPath] = identity;
            }

            foreach (var type in LayerRegistry.Types.Where(t => !layerBaseline.Contains(t))) PluginTypes.Add(type);
            foreach (var preset in LayerPresetRegistry.All.Where(p => !presetBaseline.Contains(p.Id))) PluginPresets.Add(preset.Id);
            foreach (var name in PredictionStrategyRegistry.Names.Where(n => !strategyBaseline.Contains(n))) PluginStrategies.Add(name);
            foreach (var command in PluginCommands.Names) PluginCommandNames.Add(command);
            foreach (var operation in ControlOperations.Added) PluginOperationNames.Add(operation);
            foreach (var descriptor in CredentialKinds.All) PluginCredentialKinds.Add(descriptor.Kind);
            foreach (var type in CredentialConsumers.All) PluginCredentialConsumers.Add(type);

            LastReport = new PluginLoadReport(results, errors);
            return LastReport;
        }
    }

    private static string? EmptyOrNull(string text) => text.Length == 0 ? null : text;

    private static bool SamePath(string configured, string full, string baseDirectory)
    {
        try
        {
            string resolved = Path.GetFullPath(configured ?? "", baseDirectory);
            return string.Equals(resolved, Path.GetFullPath(full), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    /// <summary>Why a plugin built against <paramref name="apiVersion"/> cannot run here, or null
    /// when it can. Empty means the plugin records no version — it was compiled against the host
    /// before the contract was versioned, and the type-load message is still what explains that
    /// failure.</summary>
    internal static string? ApiMismatch(string apiVersion) =>
        apiVersion.Length == 0 || PluginApi.IsCompatible(apiVersion) ? null
        : "built against plugin contract " + apiVersion + ", this host provides " + PluginApi.Version
          + "; rebuild it against PolyMount.Plugin.Abstractions " + PluginApi.Major + ".x";

    /// <summary>The contract version a plugin <em>file</em> records, read out of its metadata.
    ///
    /// The overload below reads the attribute off a loaded assembly, which is fine for a plugin
    /// that is about to run and useless for the two rows that matter here: a plugin built against
    /// another major is exactly the assembly a host must not load, and a disabled one is never
    /// loaded at all — yet both need a row that says so. Nothing is resolved and no code runs; the
    /// attribute is one string on the assembly, walked to directly.</summary>
    public static string ReadApiVersion(string path)
    {
        try
        {
            // Shared for writing: a plugin being replaced while the page is open is a file the
            // reader should see the last version of, not one it cannot open at all.
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return "";
            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(handle);
                if (!IsPluginApiAttribute(reader, attribute)) continue;
                var blob = reader.GetBlobReader(attribute.Value);
                if (blob.ReadUInt16() != 1) return "";   // custom-attribute prolog
                return blob.ReadSerializedString() ?? "";
            }
        }
        catch (Exception) { /* not an assembly, or one this account cannot read */ }
        return "";
    }

    private static bool IsPluginApiAttribute(MetadataReader reader, CustomAttribute attribute)
    {
        EntityHandle parent;
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                parent = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                break;
            case HandleKind.MethodDefinition:
                parent = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType();
                break;
            default:
                return false;
        }
        return parent.Kind switch
        {
            HandleKind.TypeReference => IsContractAttribute(reader,
                reader.GetTypeReference((TypeReferenceHandle)parent).Namespace,
                reader.GetTypeReference((TypeReferenceHandle)parent).Name),
            HandleKind.TypeDefinition => IsContractAttribute(reader,
                reader.GetTypeDefinition((TypeDefinitionHandle)parent).Namespace,
                reader.GetTypeDefinition((TypeDefinitionHandle)parent).Name),
            _ => false
        };

        static bool IsContractAttribute(MetadataReader reader, StringHandle space, StringHandle name) =>
            reader.StringComparer.Equals(space, "PolyMount.Plugins") &&
            reader.StringComparer.Equals(name, "PluginApiAttribute");
    }

    /// <summary>The contract version an assembly records. Reading custom attributes does not run
    /// any of the plugin's code and does not resolve its types: the attribute's own type lives in
    /// the contract assembly this process already has. Empty for a plugin built before the
    /// attribute existed — those were compiled against Core itself, and the type-load message is
    /// still what explains them.
    /// </summary>
    private static string ReadApiVersion(Assembly assembly)
    {
        try
        {
            foreach (var data in assembly.GetCustomAttributesData())
                if (data.AttributeType == typeof(PluginApiAttribute) && data.ConstructorArguments.Count == 1)
                    return data.ConstructorArguments[0].Value as string ?? "";
        }
        catch (Exception) { /* an unreadable attribute set says nothing; the load itself will */ }
        return "";
    }

    /// <summary>A load failure in words a reader can act on. A plugin is compiled against a
    /// particular PolyMount.Core, and the host loads its own copy: when the two are different
    /// builds, the failure is a type that "does not exist", which reads like a broken plugin rather
    /// than what it is — a host that was not redeployed with its plugins. The MCP server is the
    /// usual one, because a running session holds it open and the deployment leaves it alone.</summary>
    internal static string Explain(Exception ex)
    {
        if (ex is not (TypeLoadException or FileLoadException or FileNotFoundException or MissingMethodException))
            return ex.Message;
        string core;
        try { core = typeof(PluginLoader).Assembly.Location; }
        catch (Exception) { core = ""; }
        return ex.Message
            + " — this plugin was built against a different PolyMount.Core than the one this process loaded"
            + (core.Length > 0 ? " (" + core + ")" : "")
            + "; publish and restart the host and its plugins from one build";
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
    /// <summary>The version a plugin displays: the release number its build stamped into the
    /// file, falling back to the assembly version.
    ///
    /// It has to be the file version, because every assembly in this product shares one assembly
    /// identity on purpose (see Directory.Build.props): the host and its plugins are built
    /// separately, and a version in the identity would stop them loading together. The release
    /// number lives in FileVersion, which is what a file's properties page shows and what a
    /// plugin list should show.</summary>
    public static string ReadVersion(string path)
    {
        try
        {
            string? released = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion;
            if (!string.IsNullOrWhiteSpace(released)) return released.Trim();
        }
        catch (Exception) { /* fall through to the identity */ }
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
        HashSet<string> Strategies, HashSet<string> Commands, HashSet<string> Operations,
        HashSet<string> CredentialKinds, HashSet<string> Panels);

    private static RegistrySnapshot Snapshot() => new(
        SourceRegistry.Types.ToHashSet(StringComparer.OrdinalIgnoreCase),
        LayerTypes().ToHashSet(StringComparer.OrdinalIgnoreCase),
        LayerPresetRegistry.All.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase),
        PredictionStrategyRegistry.Names.ToHashSet(StringComparer.OrdinalIgnoreCase),
        PluginCommands.Names.ToHashSet(StringComparer.OrdinalIgnoreCase),
        ControlOperations.Added.ToHashSet(StringComparer.Ordinal),
        CredentialKinds.All.Select(k => k.Kind).ToHashSet(StringComparer.OrdinalIgnoreCase),
        PluginPanels.Ids.ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static IEnumerable<string> LayerTypes() =>
        LayerRegistry.Types.Where(type => !SourceRegistry.Contains(type));

    private static IReadOnlyList<string> Difference(HashSet<string> after, HashSet<string> before) =>
        after.Where(item => !before.Contains(item)).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
}
