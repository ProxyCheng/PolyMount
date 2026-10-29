namespace PolyMount.Plugins;

/// <summary>A plugin project that builds.
///
/// What it generates is the smallest thing that is a plugin: a project that references the
/// contract and imports the SDK, a manifest, and one class that registers something. The
/// scaffolding is deliberately thin — a plugin's shape is the contract's shape, and anything more
/// here would be a second opinion about it.
/// </summary>
internal static class NewCommand
{
    public static int Run(IReadOnlyList<string> arguments)
    {
        string? id = null, directory = null, name = null, kind = "layer";
        for (int i = 0; i < arguments.Count; i++)
        {
            switch (arguments[i])
            {
                case "--dir" when i + 1 < arguments.Count: directory = arguments[++i]; break;
                case "--name" when i + 1 < arguments.Count: name = arguments[++i]; break;
                case "--kind" when i + 1 < arguments.Count: kind = arguments[++i]; break;
                default: id ??= arguments[i]; break;
            }
        }
        if (id == null)
        {
            Console.Error.WriteLine("poly new <id> [--dir <directory>] [--kind layer|source] [--name <title>]");
            return 2;
        }
        if (kind is not ("layer" or "source"))
        {
            Console.Error.WriteLine($"--kind {kind}: expected layer or source");
            return 2;
        }

        string className = string.Concat(id.Split('.', '-', '_').Select(part =>
            part.Length == 0 ? "" : char.ToUpperInvariant(part[0]) + part[1..])) + "Plugin";
        string title = name ?? className;
        directory = Path.GetFullPath(directory ?? id);
        if (Directory.Exists(directory) && Directory.EnumerateFiles(directory).Any())
        {
            Console.Error.WriteLine($"{directory} is not empty");
            return 1;
        }
        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, className + ".csproj"), Project(id));
        File.WriteAllText(Path.Combine(directory, "plugin.json"),
            Manifest(id, title, kind == "source" ? "network" : "filesystem"));
        File.WriteAllText(Path.Combine(directory, className + ".cs"),
            kind == "source" ? SourcePlugin(className, title) : LayerPlugin(className, title));
        File.WriteAllText(Path.Combine(directory, "README.md"), Readme(id, className));

        Console.WriteLine($"created {directory}");
        Console.WriteLine("  next: dotnet build, then poly verify <bin/Debug/net10.0-windows/" + id + ".dll>");
        return 0;
    }

    private static string Project(string id) => $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0-windows</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <PlatformTarget>x64</PlatformTarget>
            <AssemblyName>{{id}}</AssemblyName>
            <RootNamespace>Plugin.{{id.Replace('.', '_')}}</RootNamespace>
          </PropertyGroup>

          <ItemGroup>
            <!-- The plugin contract. Nothing else of the product is referenced: what a plugin may
                 use is what this package exports. -->
            <PackageReference Include="PolyMount.Plugin.Abstractions" Version="1.0.0" PrivateAssets="all" />
            <PackageReference Include="PolyMount.Plugin.Sdk" Version="1.0.0" PrivateAssets="all" />
          </ItemGroup>

          <ItemGroup>
            <Using Include="PolyMount.Plugins" />
          </ItemGroup>
        </Project>
        """;

    private static string Manifest(string id, string title, string permission) => $$"""
        {
          "schemaVersion": 1,
          "id": "{{id}}",
          "name": { "en": "{{title}}", "zh-CN": "{{title}}" },
          "description": { "en": "TODO: one sentence about what this plugin adds.", "zh-CN": "TODO：一句话说明这个插件增加什么。" },
          "icon": "plugin",
          "publisher": "",
          "license": "MIT",
          "permissions": ["{{permission}}"],
          "host": ">=0.9.5"
        }
        """;

    private static string LayerPlugin(string className, string title) => $$"""
        using PolyMount.Plugins;

        namespace Plugin;

        /// <summary>{{title}}: a layer type. A layer answers for a path, or says it does not have
        /// it; the chain above decides what that means. This one holds nothing and serves nothing —
        /// replace `TryGetEntry` with whatever the layer is for.</summary>
        public sealed class {{className}} : IPolyMountPlugin
        {
            public const string Type = "{{TypeName(className)}}";

            public void Register(IPluginContext context) => context.RegisterLayer(Type, Create, null, new LayerDescriptor
            {
                Type = Type,
                Title = new LocalizedText("{{title}}", "{{title}}"),
                Description = new LocalizedText("TODO: what this layer does.", "TODO：这一层做什么。")
            });

            private static ILayer Create(LayerConfig config, LayerBuildContext build) => new Layer(config, build);

            private sealed class Layer(LayerConfig config, LayerBuildContext build) : ILayer
            {
                // The generation the layer's answers belong to. A layer whose content follows the
                // source reports the source's; one with no revision of its own reports a constant.
                public string Generation => build.LowerGeneration();
                public string Name => config.Id;
                public bool Writable => false;

                public bool TryGetEntry(string path, out VfsEntry entry)
                {
                    entry = null!;
                    return false;
                }

                public IReadOnlyList<VfsEntry> ListOwn(string path) => Array.Empty<VfsEntry>();

                public bool TryOpenRead(string path, out VfsEntry entry, out Stream stream)
                {
                    entry = null!;
                    stream = null!;
                    return false;
                }
            }
        }
        """;

    private static string SourcePlugin(string className, string title) => $$"""
        using PolyMount.Plugins;

        namespace Plugin;

        /// <summary>{{title}}: a data source. This is the bottom of a mount's chain — the thing that
        /// actually has the bytes. `Register` gives it a type name, a factory and the descriptor the
        /// configuration editor draws; everything else is the protocol's own business.</summary>
        public sealed class {{className}} : IPolyMountPlugin
        {
            public const string Type = "{{TypeName(className)}}";

            public void Register(IPluginContext context) => context.RegisterSource(Type,
                (config, stateDirectory) => new Source(config, stateDirectory),
                config =>
                {
                    if (string.IsNullOrWhiteSpace(config.Url))
                        throw new ArgumentException("an address is required");
                },
                new LayerDescriptor
                {
                    Type = Type,
                    Title = new LocalizedText("{{title}}", "{{title}}"),
                    Description = new LocalizedText("TODO: what this source reads.", "TODO：这个来源读取什么。"),
                    Fields = new()
                    {
                        new LayerFieldDescriptor
                        {
                            Path = "Source.Url",
                            Title = new LocalizedText("Address", "地址"),
                            Kind = LayerFieldKind.Text,
                            Required = true
                        }
                    }
                });

            private sealed class Source(SourceConfig config, string stateDirectory) : ILayer
            {
                // Where this layer may keep state that describes the current mount. The host
                // creates it; a source that remembers nothing can ignore it.
                public string StateDirectory => stateDirectory;

                // The source's own idea of which version its content is at. Caches key on it, so a
                // source that cannot name one reports a constant and is cached by nothing but its
                // own answers.
                public string Generation => "1";
                public string Name => config.Url;
                public bool Writable => false;

                public bool TryGetEntry(string path, out VfsEntry entry)
                {
                    entry = null!;
                    return false;
                }

                public IReadOnlyList<VfsEntry> ListOwn(string path) => Array.Empty<VfsEntry>();

                public bool TryOpenRead(string path, out VfsEntry entry, out Stream stream)
                {
                    entry = null!;
                    stream = null!;
                    return false;
                }
            }
        }
        """;

    private static string Readme(string id, string className) => $$"""
        # {{id}}

        A PolyMount plugin. It is an assembly that implements `IPolyMountPlugin`; everything it adds
        is registered through the `IPluginContext` it is handed while `Register` runs.

        ```
        dotnet build
        poly verify bin/Debug/net10.0-windows/{{id}}.dll   # manifest, contract version, what it adds
        poly dev    bin/Debug/net10.0-windows/{{id}}.dll   # a host of its own, on its own pipe
        poly pack   bin/Debug/net10.0-windows              # one file to hand over
        ```

        `plugin.json` is what the host reads before it loads anything: the identifier, the name in
        each language, and what the plugin reaches for. The contract version is stamped into the
        assembly by the build, from `<PluginApiVersion>` — raise it only when the plugin is rebuilt
        against a newer `PolyMount.Plugin.Abstractions`.

        Installing it: unpack the package into the installation's `plugins` folder and name the
        assembly in `config.json` (the tray's plugin page writes that entry for you).
        """;

    private static string TypeName(string className) =>
        className.EndsWith("Plugin", StringComparison.Ordinal) ? className[..^"Plugin".Length].ToLowerInvariant() : className.ToLowerInvariant();
}
