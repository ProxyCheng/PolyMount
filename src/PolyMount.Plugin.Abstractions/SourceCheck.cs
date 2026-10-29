using PolyMount.Plugins.Ipc;

namespace PolyMount.Plugins;

/// <summary>What a source answered when it was asked, in the words the answer arrived in.
///
/// <see cref="Kind"/> is the part a window acts on — which remedy to offer — and
/// <see cref="Message"/> is what whoever refused said, shown as it is. A check has no
/// configuration behind it to fall back on, so the message is the whole of the explanation.</summary>
/// <param name="Ok">The source answered with these settings.</param>
/// <param name="Kind">One of <see cref="SourceCheckKinds"/>.</param>
/// <param name="Message">What the source said, empty when it answered.</param>
/// <param name="Entries">How many entries the root listing held: the proof that something was
/// read rather than merely constructed. Zero is a source that answered with an empty folder,
/// or a check that never got that far.</param>
/// <param name="Milliseconds">How long the whole exchange took, so a reader can tell a source that
/// answered from one that was quick because nothing was reachable.</param>
public sealed record SourceCheckResult(bool Ok, string Kind, string Message, int Entries, double Milliseconds);

/// <summary>The outcomes a <see cref="SourceCheck"/> reports, chosen so that a window can pick its
/// remedy from the name rather than from the sentence (the sentences come from the layers).</summary>
public static class SourceCheckKinds
{
    /// <summary>The source answered.</summary>
    public const string Ok = "";

    /// <summary>The account was missing or refused. The remedy is another credential.</summary>
    public const string Auth = FailureKinds.Auth;

    /// <summary>The credential may not be presented here: it is bound to other origins and this
    /// address has to be added to it in the credential manager.</summary>
    public const string Origin = FailureKinds.Origin;

    /// <summary>The settings do not describe a source: an address of the wrong shape, a required
    /// value missing, a type no plugin provides.</summary>
    public const string Invalid = "invalid";

    /// <summary>Everything else the source said: unreachable, untrusted certificate, missing
    /// folder. The message names which.</summary>
    public const string Unreachable = "unreachable";
}
