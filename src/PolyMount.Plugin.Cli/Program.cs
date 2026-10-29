using PolyMount.Plugins;

// poly — the tool beside the plugin contract.
//
// Four commands, in the order a plugin meets them: new (a project that builds), verify (what the
// host will make of it), dev (a host to run it under), pack (something to hand to someone else).

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine($"""
        poly — PolyMount plugin tool

          poly new <id> [--dir <directory>] [--kind layer|source] [--name <title>]
                                 scaffold a plugin project
          poly verify <plugin.dll | directory>
                                 the manifest, the contract version and what Register adds
          poly dev <plugin.dll> [--mount <path>] [--config <file>] [--service <exe>]
                                 run it under a development host on its own pipe
          poly pack <plugin.dll | directory> [-o <package.pmpkg>]
                                 one file for someone who will not build it

        contract version: {PluginApi.Version}
        """);
    return 0;
}

var rest = args.Skip(1).ToArray();
return args[0].ToLowerInvariant() switch
{
    "new" => NewCommand.Run(rest),
    "verify" => VerifyCommand.Run(rest),
    "dev" => DevCommand.Run(rest),
    "pack" => PackCommand.Run(rest),
    _ => Unknown(args[0])
};

static int Unknown(string command)
{
    Console.Error.WriteLine($"poly: unknown command '{command}' — try poly --help");
    return 2;
}
