namespace PolyMount.Plugins;

/// <summary>Checks a plugin the way the host will: the manifest it declares, the contract it was
/// built against, and what its own <c>Register</c> puts on the table.
///
/// It is deliberately not the host. Nothing here mounts, opens a source or runs a command; the
/// plugin's registrations are recorded and printed. What that catches is the whole class of
/// mistakes that otherwise only show up as an installation that will not start: a missing or
/// malformed manifest, a build against another contract version, a type that no longer loads, a
/// plugin that registers nothing at all.
/// </summary>
internal static class VerifyCommand
{
    public static int Run(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            Console.Error.WriteLine("poly verify <plugin.dll | directory>");
            return 2;
        }

        int failures = 0;
        foreach (string target in Expand(arguments[0]))
            if (!Verify(target)) failures++;
        return failures == 0 ? 0 : 1;
    }

    private static IEnumerable<string> Expand(string target)
    {
        if (!Directory.Exists(target)) { yield return target; yield break; }
        foreach (string file in Directory
                     .EnumerateFiles(target, "*.dll", SearchOption.TopDirectoryOnly)
                     .Where(file => !file.EndsWith(".Tests.dll", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.OrdinalIgnoreCase))
            yield return file;
    }

    private static bool Verify(string path)
    {
        Console.WriteLine(path);
        if (!File.Exists(path))
        {
            Console.WriteLine("  FAIL  the file does not exist");
            return false;
        }

        var inspection = PluginInspector.Inspect(path);
        if (inspection.Manifest != null)
            Console.WriteLine($"  manifest  id={inspection.Manifest.Id}  roles=" +
                              string.Join("+", PluginRoleNames.Names(inspection.Manifest.RoleSet)) +
                              "  permissions=" +
                              (inspection.Manifest.Permissions.Count == 0 ? "none" : string.Join(", ", inspection.Manifest.Permissions)));
        else if (inspection.ManifestError.Length == 0)
            Console.WriteLine("  warn  no manifest: the host will load it as a legacy plugin (no id, no permissions, no name before it is enabled)");

        string apiVersion = inspection.ApiVersion;
        Console.WriteLine(apiVersion.Length == 0
            ? "  warn  no contract version recorded: built without PolyMount.Plugin.Sdk"
            : $"  contract  {apiVersion}");

        foreach (var plugin in inspection.Plugins)
        {
            Console.WriteLine($"  plugin    {plugin.Type.FullName}" +
                              (plugin.Title.Length > 0 ? $"  \"{plugin.Title}\"" : ""));
            Report("source", plugin.Registrations.Sources);
            Report("layer", plugin.Registrations.Layers);
            Report("preset", plugin.Registrations.Presets);
            Report("strategy", plugin.Registrations.Strategies);
            Report("command", plugin.Registrations.Commands);
            Report("operation", plugin.Registrations.Operations);
            Report("credential", plugin.Registrations.CredentialKinds);
            Report("credential consumer", plugin.Registrations.CredentialConsumers);
            Report("panel", plugin.Registrations.Panels);
            if (plugin.Registrations.Notices) Console.WriteLine("  adds      notices");
        }

        // The declaration decides where the code is loaded, so a declaration that does not match
        // what Register does is a plugin that loads where nothing can reach it — or misses the one
        // process that would have used it. Both are silent at run time, which is why they are
        // checked here.
        var declared = inspection.Manifest?.RoleSet ?? PluginRoles.Both;
        var problems = new List<string>(inspection.Problems);
        foreach (var plugin in inspection.Plugins)
        {
            var r = plugin.Registrations;
            bool backend = r.Layers.Count + r.Sources.Count + r.Presets.Count + r.Strategies.Count
                           + r.Commands.Count + r.CredentialKinds.Count + r.CredentialConsumers.Count
                           + r.Panels.Count > 0
                           || r.Notices;
            bool frontend = r.Operations.Count > 0;
            if (backend && (declared & PluginRoles.Backend) == 0)
                problems.Add($"declares '{PluginRoleNames.Frontend}' but registers server-side contributions; no worker would load it, so they would never exist");
            if (frontend && (declared & PluginRoles.Frontend) == 0)
                problems.Add($"declares '{PluginRoleNames.Backend}' but registers {r.Operations.Count} control operation(s); no front-end would load it, so its tools would never be offered");
            if (!frontend && (declared & PluginRoles.Frontend) != 0)
                Console.WriteLine("  warn  declares 'frontend' but registers no control operation — front-ends will load it for nothing");
        }

        foreach (string problem in problems) Console.WriteLine("  FAIL  " + problem);
        if (problems.Count == 0) Console.WriteLine("  ok");
        return problems.Count == 0;
    }

    private static void Report(string label, List<string> values)
    {
        if (values.Count > 0) Console.WriteLine($"  adds      {label}: {string.Join(", ", values)}");
    }
}
