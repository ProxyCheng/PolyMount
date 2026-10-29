using System.Diagnostics;

namespace PolyMount.Plugins;

/// <summary>Runs a plugin under a host of its own.
///
/// The installed service is not this: it cannot be stopped by whoever is writing the plugin, it
/// takes every mount down with it, and it locks the assembly it loaded, so the next build cannot
/// replace the file. A development host is a second supervisor on its own pipe, with its own store
/// and its own logs, started with <c>--dev</c> so it may load the assembly where it was built.
///
/// What a person watches is the tray pointed at that pipe (POLYMOUNT_PIPE) or the MCP server
/// started with --pipe; both discover the workers through the supervisor, so a mount in the
/// development host behaves like an installed one.
/// </summary>
internal static class DevCommand
{
    public static int Run(IReadOnlyList<string> arguments)
    {
        string? plugin = null, service = null, mount = null, config = null;
        for (int i = 0; i < arguments.Count; i++)
        {
            switch (arguments[i])
            {
                case "--service" when i + 1 < arguments.Count: service = arguments[++i]; break;
                case "--mount" when i + 1 < arguments.Count: mount = arguments[++i]; break;
                case "--config" when i + 1 < arguments.Count: config = arguments[++i]; break;
                default: plugin ??= arguments[i]; break;
            }
        }
        if (plugin == null)
        {
            Console.Error.WriteLine("poly dev <plugin.dll> [--mount <path>] [--config <file>] [--service <PolyMount.Service.exe>]");
            return 2;
        }

        plugin = Path.GetFullPath(plugin);
        var inspection = PluginInspector.Inspect(plugin);
        foreach (string problem in inspection.Problems)
        {
            Console.Error.WriteLine("refusing to run: " + problem);
            return 1;
        }

        service ??= DefaultServicePath();
        if (!File.Exists(service))
        {
            Console.Error.WriteLine($"PolyMount.Service.exe not found at {service}. Pass --service <path>.");
            return 2;
        }

        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PolyMount", "dev", Path.GetFileNameWithoutExtension(plugin));
        Directory.CreateDirectory(root);
        string pipe = "PolyMount.Dev." + new string(Path.GetFileNameWithoutExtension(plugin).Where(char.IsLetterOrDigit).ToArray());
        string configPath = config == null ? Path.Combine(root, "config.json") : Path.GetFullPath(config);
        string mountPoint = mount ?? Path.Combine(root, "mount");
        if (config == null) File.WriteAllText(configPath, Config(pipe, Path.Combine(root, "store"), mountPoint, inspection));

        Console.WriteLine($"development host on {pipe}");
        Console.WriteLine($"  config  {configPath}");
        Console.WriteLine($"  mount   {mountPoint}");
        Console.WriteLine($"  plugin  {plugin}");
        if (config == null)
            Console.WriteLine("  the instance is a starting point: edit the config to give the source its parameters.");
        Console.WriteLine($"  watch:  set POLYMOUNT_PIPE={pipe} && PolyMount.Tray.exe");
        Console.WriteLine();

        var start = new ProcessStartInfo(service)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(service)!,
            ArgumentList = { "--config", configPath, "--dev", "--plugin", plugin }
        };
        using var process = Process.Start(start)!;
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>Where the service usually is: beside this tool in a source tree, or where the
    /// setup puts it.</summary>
    private static string DefaultServicePath()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "PolyMount.Service.exe");
        if (File.Exists(beside)) return beside;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PolyMount", "Service", "PolyMount.Service.exe");
    }

    /// <summary>A configuration with one instance. When the plugin registers a source, the chain
    /// ends with it — the layer type and the source type are the same name, which is how every
    /// source in this product is configured — so the mount at least has somewhere to read from.
    /// Its parameters are left at their defaults: a source that needs an address says so, in the
    /// instance's own status, which is more useful than a guess that could be wrong.</summary>
    private static string Config(string pipe, string storeRoot, string mountPoint, PluginInspection inspection)
    {
        string? source = inspection.Plugins.SelectMany(plugin => plugin.Registrations.Sources).FirstOrDefault();
        string tail = source == null
            ? ""
            : ",\n        { \"Id\": \"" + source + "\", \"Type\": \"" + source + "\", \"Source\": { \"Type\": \"" + source + "\" } }\n  ";
        return $$"""
            {
              "ControlPipe": {{Quote(pipe)}},
              "Plugins": [],
              "Mounts": [
                {
                  "Name": "dev",
                  "Enabled": true,
                  "MountPoint": {{Quote(mountPoint)}},
                  "StoreRoot": {{Quote(storeRoot)}},
                  "Layers": [
                    { "Id": "memory-cache", "Type": "memory-cache" },
                    { "Id": "overlay", "Type": "overlay" }{{tail}}
                  ]
                }
              ]
            }
            """;
    }

    private static string Quote(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}
