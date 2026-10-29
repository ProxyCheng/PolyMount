using System.Reflection;

namespace PolyMount.Plugins;

/// <summary>What a plugin turns out to be, without a host: the manifest beside it, the contract it
/// was built against, and the registrations its own <c>Register</c> makes.
///
/// Shared by the tool's two commands that need it — checking a plugin, and starting a host for
/// one — so that what a person is shown and what is run come from the same reading.
/// </summary>
internal sealed record PluginInspection(
    string Path,
    PluginManifest? Manifest,
    string ManifestError,
    string ApiVersion,
    IReadOnlyList<string> Problems,
    IReadOnlyList<RegisteredPlugin> Plugins);

internal sealed record RegisteredPlugin(Type Type, string Title, RecordingContext Registrations);

internal static class PluginInspector
{
    public static PluginInspection Inspect(string path)
    {
        var problems = new List<string>();
        var plugins = new List<RegisteredPlugin>();
        string full = System.IO.Path.GetFullPath(path);

        PluginManifest? manifest = PluginManifest.Read(full, out string manifestError);
        if (manifestError.Length > 0) problems.Add(manifestError);

        string apiVersion = "";
        var directory = System.IO.Path.GetDirectoryName(full)!;
        var context = new PluginLoadContext(directory);
        Assembly assembly;
        try
        {
            assembly = context.LoadFromAssemblyPath(full);
        }
        catch (Exception ex)
        {
            problems.Add("the assembly does not load: " + ex.Message);
            return new PluginInspection(full, manifest, manifestError, apiVersion, problems, plugins);
        }

        apiVersion = ReadApiVersion(assembly);
        if (apiVersion.Length > 0 && !PluginApi.IsCompatible(apiVersion))
            problems.Add($"built against plugin contract {apiVersion}; this tool provides {PluginApi.Version} — rebuild it against PolyMount.Plugin.Abstractions {PluginApi.Major}.x");

        Type[] types;
        try
        {
            types = assembly.GetTypes().Where(t => !t.IsAbstract && typeof(IPolyMountPlugin).IsAssignableFrom(t)).ToArray();
        }
        catch (ReflectionTypeLoadException ex)
        {
            problems.Add("types do not load: " + string.Join("; ",
                ex.LoaderExceptions.Where(e => e != null).Select(e => e!.Message).Distinct()));
            types = Array.Empty<Type>();
        }
        if (types.Length == 0 && problems.Count == 0) problems.Add("the assembly exports no IPolyMountPlugin");

        foreach (var type in types)
        {
            var recorded = new RecordingContext();
            try
            {
                var instance = (IPolyMountPlugin)Activator.CreateInstance(type)!;
                string title = (instance as IPluginMetadata)?.Title?.Resolve("en") ?? "";
                instance.Register(recorded);
                plugins.Add(new RegisteredPlugin(type, title, recorded));
                if (recorded.IsEmpty) problems.Add(type.Name + " registers nothing");
            }
            catch (Exception ex)
            {
                problems.Add("Register threw: " + (ex.InnerException ?? ex).Message);
            }
        }

        return new PluginInspection(full, manifest, manifestError, apiVersion, problems, plugins);
    }

    public static string ReadApiVersion(Assembly assembly)
    {
        try
        {
            foreach (var data in assembly.GetCustomAttributesData())
                if (data.AttributeType == typeof(PluginApiAttribute) && data.ConstructorArguments.Count == 1)
                    return data.ConstructorArguments[0].Value as string ?? "";
        }
        catch (Exception) { }
        return "";
    }
}
