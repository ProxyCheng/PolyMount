namespace PolyMount.Plugins.Svn;

/// <summary>
/// A credential that carries no secret: "use the account the Subversion client on this machine
/// already saved".
///
/// It exists so that choice is visible and nameable. The SVN source has always fallen back to the
/// client's cache when no credential is chosen, which made it a default nobody could see; a record
/// of this kind puts it in the layer's picker, in the credential manager, and in the explanation
/// when it cannot be used — a service that runs as another account sees nothing in that cache, and
/// saying so is worth more than a 401.
///
/// <see cref="Account"/> is whose cache: the client protects the password it saves with DPAPI under
/// the Windows account that typed it, so "the cache" is always somebody's. Left empty it means the
/// account this process runs as, which is what the fallback has always meant; naming somebody else
/// is what makes the kind useful to a service — see <see cref="WindowsUser"/>.
///
/// The kind is a reference, not a value. Which repository credential comes out of the cache still
/// depends on the repository: the client keys its cache by authentication realm, so the lookup
/// happens when a layer is built.
/// </summary>
internal sealed class SubversionCacheCredential : Credential
{
    public const string KindName = "svn-cached";

    /// <summary>The key this kind's one field is stored under, and the field the manager shows
    /// the record's account from.</summary>
    public const string AccountField = "account";

    /// <summary>The Windows account whose client cache to read; empty for this process's own.</summary>
    public string Account { get; init; } = "";

    public override string Kind => KindName;
}
