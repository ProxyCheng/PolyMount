namespace PolyMount.Plugins;

/// <summary>Which of the product's processes a plugin's code is needed in.
///
/// A plugin is one assembly, but the product is several processes: the supervisor that owns the
/// configuration, one worker process per mount, and the MCP server an agent starts. A layer or a
/// source only ever runs in a worker; a control operation only ever runs in a front-end. Loading
/// both everywhere works — that is what every plugin did until this existed — but it means the
/// MCP server loads a Subversion source it can never use, and a future plugin that only offers
/// tools would be loaded into every worker.
///
/// So a plugin declares where its code belongs, and a process loads only what it declared. What
/// a process loads is not what a reader is shown: the supervisor loads everything, because the
/// plugin page is answered from its report, and it is the one process that must be able to
/// describe a plugin it does not run.
/// </summary>
[Flags]
public enum PluginRoles
{
    /// <summary>Loaded by nothing. Not a useful declaration; the host refuses it.</summary>
    None = 0,

    /// <summary>Servers: layers, sources, prediction strategies, commands, notices, credential
    /// kinds — anything whose code is reached through a mount.</summary>
    Backend = 1,

    /// <summary>Front-ends: control operations, the tools a client gets from the catalog.</summary>
    Frontend = 2,

    /// <summary>Both halves — the common case, and what a manifest that says nothing means.</summary>
    Both = Backend | Frontend
}

/// <summary>The words a manifest uses for <see cref="PluginRoles"/>.</summary>
public static class PluginRoleNames
{
    public const string Backend = "backend";
    public const string Frontend = "frontend";

    /// <summary>Reads a manifest's role list. Absent (or empty) means both, which is what every
    /// plugin written before roles existed did. A word the product does not know is refused
    /// rather than ignored: a plugin that meant one thing and got another would be loaded where
    /// its code is never reached, and nothing would say so.</summary>
    public static bool TryParse(IEnumerable<string>? roles, out PluginRoles parsed, out string error)
    {
        parsed = PluginRoles.None;
        error = "";
        if (roles == null) { parsed = PluginRoles.Both; return true; }
        foreach (string role in roles)
        {
            switch (role?.Trim().ToLowerInvariant())
            {
                case "": continue;
                case Backend: parsed |= PluginRoles.Backend; continue;
                case Frontend: parsed |= PluginRoles.Frontend; continue;
                default:
                    error = $"unknown role '{role}' (the roles are {Backend} and {Frontend})";
                    return false;
            }
        }
        if (parsed == PluginRoles.None) parsed = PluginRoles.Both;
        return true;
    }

    /// <summary>The roles a reader can see, in the product's words.</summary>
    public static IReadOnlyList<string> Names(PluginRoles roles)
    {
        var names = new List<string>(2);
        if ((roles & PluginRoles.Backend) != 0) names.Add(Backend);
        if ((roles & PluginRoles.Frontend) != 0) names.Add(Frontend);
        return names;
    }
}
