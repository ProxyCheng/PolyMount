namespace PolyMount.Plugins;

/// <summary>The contract version a plugin was built against.
///
/// Two numbers describe a plugin, and they move on different schedules: the plugin's own version,
/// which its author raises whenever they like, and the contract's, which changes only when the
/// surface a plugin compiles against does. This constant is the second one. A plugin's assembly
/// carries it as <see cref="PluginApiAttribute"/>, written there by the build rather than by the
/// author, and the host compares it with its own before it calls into the plugin.
///
/// Only the major moves incompatibly. A minor is additive — new members, new optional parameters
/// — and a plugin built against an older minor keeps working. A plugin built against a newer one
/// does not, and that is refused rather than left to fail somewhere inside a type load.
/// </summary>
public static class PluginApi
{
    /// <summary>The version of the contract in this package, as <c>major.minor</c>.
    ///
    /// 1.1 added <see cref="Ipc.BuildInfo"/> and the "build" command behind it: additive, so a
    /// plugin built against 1.0 still loads, and one built against this asks for a host that can
    /// answer the question rather than one that cannot.
    ///
    /// 2.0 took <c>Password</c> off <see cref="Credential"/>: how a credential proves itself is
    /// now a capability it implements (<see cref="IPasswordAuthentication"/>,
    /// <see cref="ICertificateAuthentication"/>, <see cref="IBearerAuthentication"/>) rather than
    /// two fields every kind had to carry. Breaking — a plugin that read the base member stops
    /// compiling — so the major moved, and a 1.x plugin is refused with that reason instead of
    /// failing somewhere deep.
    ///
    /// 2.1 added <see cref="PluginManifest.EmbeddedResourceName"/>: the manifest lives inside the
    /// assembly now, so a plugin is one file. Additive — a plugin that still ships its manifest
    /// beside it is read that way.
    ///
    /// 2.2 added <see cref="PluginNotice.Automatic"/> and <see cref="BrowserLogin"/>: a plugin can
    /// ask a front end to show a sign-in page and hand back what it leaves behind, because a
    /// service process has no window to show one in. Additive.
    ///
    /// 3.0 took <see cref="LayerFieldDescriptor.CredentialKinds"/> away. A layer no longer lists
    /// the credential kinds it accepts; it says what it can present
    /// (<see cref="IPluginContext.RegisterCredentialConsumer"/>), and the store asks it for every
    /// record (<see cref="CredentialRecord.UseFor"/>). Breaking — a plugin that filled that list
    /// stops compiling, which is the point: the list was the thing that had to be edited whenever
    /// a kind appeared.</summary>
    ///
    /// 3.2 added <see cref="CredentialKindDescriptor.Icon"/>: a kind names the mark its records
    /// wear, which is how a credential that reads another program's own cache comes to wear that
    /// program's glyph. Additive — a kind that names none draws the generic pass.
    public const string Version = "3.2";

    /// <summary>The major part of <see cref="Version"/>: the number that has to match.</summary>
    public static int Major => Parse(Version).Major;

    /// <summary>The minor part of <see cref="Version"/>.</summary>
    public static int Minor => Parse(Version).Minor;

    /// <summary>Splits a version into its two numbers, or answers (0, 0) for anything that is not
    /// one — a malformed declaration is refused by the loader rather than throwing on the way in.
    /// </summary>
    public static (int Major, int Minor) Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return (0, 0);
        string text = version.Trim();
        int dot = text.IndexOf('.');
        string major = dot < 0 ? text : text[..dot];
        string minor = dot < 0 ? "0" : text[(dot + 1)..];
        // Only the leading digits count: "1.0" parses, "1.0-beta" parses as 1.0, "1.x" does not.
        return (LeadingDigits(major), LeadingDigits(minor));
    }

    /// <summary>Whether a plugin built against <paramref name="plugin"/> can run under this
    /// contract: the same major, and a minor this one has reached.</summary>
    public static bool IsCompatible(string? plugin)
    {
        var (major, minor) = Parse(plugin);
        return major == Major && minor <= Minor && major > 0;
    }

    private static int LeadingDigits(string text)
    {
        int value = 0, count = 0;
        foreach (char c in text)
        {
            if (c is < '0' or > '9') break;
            if (value > (int.MaxValue - 9) / 10) break;
            value = value * 10 + (c - '0');
            count++;
        }
        return count == 0 ? 0 : value;
    }
}

/// <summary>What a plugin assembly says it was built against. The build writes it; nothing in a
/// plugin's source does.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class PluginApiAttribute(string version) : Attribute
{
    /// <summary>The contract version, <c>major.minor</c>.</summary>
    public string Version { get; } = version;
}
