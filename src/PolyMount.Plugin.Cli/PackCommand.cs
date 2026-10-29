using System.IO.Compression;

namespace PolyMount.Plugins;

/// <summary>A plugin as one file: the assembly, whatever sits beside it, and its manifest.
///
/// The shape is the folder the host reads. A package is for handing a plugin to someone who is not
/// going to build it — the directory layout inside is what an administrator unpacks into the
/// installation's plugins folder, and nothing in the host knows the archive exists.
/// </summary>
internal static class PackCommand
{
    public static int Run(IReadOnlyList<string> arguments)
    {
        string? source = null, output = null;
        for (int i = 0; i < arguments.Count; i++)
        {
            switch (arguments[i])
            {
                case "-o" when i + 1 < arguments.Count: output = arguments[++i]; break;
                case "--output" when i + 1 < arguments.Count: output = arguments[++i]; break;
                default: source ??= arguments[i]; break;
            }
        }
        if (source == null)
        {
            Console.Error.WriteLine("poly pack <plugin.dll | directory> [-o <package.pmpkg>]");
            return 2;
        }

        string directory = Directory.Exists(source) ? Path.GetFullPath(source) : Path.GetDirectoryName(Path.GetFullPath(source))!;
        string? assembly = Directory.Exists(source)
            ? Directory.EnumerateFiles(directory, "*.dll").FirstOrDefault(file => PluginInspector.Inspect(file).Plugins.Count > 0)
            : Path.GetFullPath(source);
        if (assembly == null)
        {
            Console.Error.WriteLine($"{directory} contains no plugin assembly");
            return 1;
        }

        var inspection = PluginInspector.Inspect(assembly);
        foreach (string problem in inspection.Problems) Console.Error.WriteLine("refusing to pack: " + problem);
        if (inspection.Problems.Count > 0) return 1;

        string name = inspection.Manifest?.Id is { Length: > 0 } id ? id : Path.GetFileNameWithoutExtension(assembly);
        output ??= Path.Combine(Environment.CurrentDirectory, name + ".pmpkg");
        string packageDirectory = Path.GetDirectoryName(Path.GetFullPath(output))!;
        Directory.CreateDirectory(packageDirectory);
        if (File.Exists(output)) File.Delete(output);

        // The contract travels with the host, never with the plugin: a second copy beside a plugin
        // would be a second contract in the same process.
        string contract = Path.GetFileNameWithoutExtension(typeof(IPolyMountPlugin).Assembly.Location);
        using (var archive = ZipFile.Open(output, ZipArchiveMode.Create))
        {
            foreach (string file in Directory.EnumerateFiles(directory).Order(StringComparer.OrdinalIgnoreCase))
            {
                string fileName = Path.GetFileName(file);
                if (Path.GetFileNameWithoutExtension(fileName).Equals(contract, StringComparison.OrdinalIgnoreCase)) continue;
                if (fileName.EndsWith(".Tests.dll", StringComparison.OrdinalIgnoreCase)) continue;
                // The manifest of *this* plugin only: a folder that builds several carries several,
                // and each package takes its own.
                if (fileName.EndsWith(".plugin.json", StringComparison.OrdinalIgnoreCase) &&
                    !fileName.Equals(Path.GetFileName(PluginManifest.PathFor(assembly)), StringComparison.OrdinalIgnoreCase))
                    continue;
                archive.CreateEntryFromFile(file, fileName);
            }
        }

        long bytes = new FileInfo(output).Length;
        Console.WriteLine($"{Path.GetFileName(output)}  {bytes / 1024.0:0.#} KiB  ({name})");
        Console.WriteLine("unpack it into the installation's plugins folder and name the assembly in the configuration.");
        return 0;
    }
}
