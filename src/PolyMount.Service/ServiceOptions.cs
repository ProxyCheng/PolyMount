namespace PolyMount.Service;

/// <summary>What the command line asked for beyond a configuration file.
///
/// <c>--dev</c> turns this process into a development host: it loads plugin assemblies from
/// wherever they were built, which the ordinary rule refuses, and it insists on a control pipe
/// name that no installed service uses. Those two go together and must not be separable — an
/// installation that accepted a plugin from outside its own directory would be running code the
/// administrator never approved, and a development host that attached to the installed pipe would
/// be standing in for the service that *is* approved.
///
/// <c>--plugin &lt;path&gt;</c> names an assembly to load without it being in the configuration. It is
/// how a plugin under development is tried without editing the configuration the installed service
/// reads; the same spelling exists in the MCP server.
/// </summary>
public sealed record ServiceOptions(bool Dev, IReadOnlyList<string> PluginPaths)
{
    public static readonly ServiceOptions None = new(false, Array.Empty<string>());

    public static ServiceOptions Parse(string[] args)
    {
        bool dev = args.Any(argument => string.Equals(argument, "--dev", StringComparison.OrdinalIgnoreCase));
        var plugins = new List<string>();
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (!string.Equals(args[i], "--plugin", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(args[i + 1])) continue;
            try { plugins.Add(Path.GetFullPath(args[i + 1])); }
            catch (Exception) { /* an unusable path is refused when it is looked for, with its name */ }
        }
        return new ServiceOptions(dev, plugins);
    }

    /// <summary>The arguments a worker process is started with, so it loads the same plugins the
    /// supervisor did. A worker does not read the configuration's plugin list on its own — it is
    /// handed the same <c>--config</c> — but the extra paths and the development allowance are
    /// command-line facts, not configuration, and have to be repeated.</summary>
    public IReadOnlyList<string> WorkerArguments()
    {
        var arguments = new List<string>();
        if (Dev) arguments.Add("--dev");
        foreach (string path in PluginPaths) { arguments.Add("--plugin"); arguments.Add(path); }
        return arguments;
    }
}
