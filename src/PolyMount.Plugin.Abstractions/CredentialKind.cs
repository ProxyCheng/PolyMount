using System.Text.Json.Serialization;

namespace PolyMount.Plugins;

/// <summary>What one field of a plugin's credential looks like in the form that fills it.
///
/// Deliberately small: a credential is a thing a source presents, not a configuration document,
/// so the fields are the values a kind needs to build one — an endpoint, an account, a token —
/// and nothing that would turn the credential manager into a second layer editor.</summary>
public enum CredentialFieldKind
{
    /// <summary>An ordinary value the record shows back: an endpoint, an account, a tenant.</summary>
    Text,

    /// <summary>A value that is stored and never shown again, like a password: a token, a key.</summary>
    Secret,

    /// <summary>A switch the kind reads as "true" or "false".</summary>
    Boolean
}

/// <summary>One field a credential of a plugin's kind holds.</summary>
public sealed class CredentialFieldDescriptor
{
    /// <summary>The key this field's value is stored under, and the one <see
    /// cref="CredentialKindDescriptor.Create"/> reads it back by. Unique within the kind.</summary>
    public string Path { get; set; } = "";
    public CredentialFieldKind Kind { get; set; }
    public LocalizedText Title { get; set; } = new();
    public LocalizedText Description { get; set; } = new();
    public LocalizedText Placeholder { get; set; } = new();
    /// <summary>A field the record cannot be saved without. A secret filled in when the record
    /// already holds one counts as filled: the form never shows a stored secret back.</summary>
    public bool Required { get; set; }
    /// <summary>What a new record starts with, if anything.
    ///
    /// Environment names in it are expanded by whoever fills the form — so <c>%USERNAME%</c> is
    /// the person filling it, which is how a kind asks "whose?" without knowing the answer.</summary>
    public string? DefaultValue { get; set; }
}

/// <summary>
/// A kind of credential a plugin contributes.
///
/// The product ships two — an account with a password, and a PKCS#12 certificate — and a layer
/// says what it can present, not which kinds exist (<see cref="CredentialConsumers"/>). Before
/// this existed the list was closed: the store, the payload, the form and the picker each had the
/// two names written into them, so a protocol that authenticates some other way (an internal SSO
/// token, a cloud identity, a hardware key) could not be added without changing all of them
/// together. A plugin now describes its kind once and everything downstream reads the description.
///
/// What a kind does not do is decide how the secret is protected or where the record lives: the
/// values travel through the same store, are protected with the same DPAPI blob, are bound to the
/// same origins and are shown by the same manager. A kind is a shape, not a second store.
/// </summary>
public sealed class CredentialKindDescriptor
{
    /// <summary>The stable discriminator written beside the secret. Letters, digits, '-' and
    /// '_'.</summary>
    public string Kind { get; set; } = "";
    public LocalizedText Title { get; set; } = new();
    public LocalizedText Description { get; set; } = new();

    /// <summary>One of the tray's line-icon names — the same vocabulary a layer descriptor names
    /// its own mark from; empty draws the generic pass.
    ///
    /// A list of accounts is scanned rather than read, and a row names the kind in words only when
    /// the kind is not an account: what a reader recognises a record by is the program whose secret
    /// it holds. A kind that reads another program's own cache therefore wears that program's mark
    /// — a tortoise for TortoiseSVN — drawn in this set's stroke rather than copied, so it belongs
    /// to the same sheet as everything beside it. A name the front end does not draw is not an
    /// error: the badge is a summary, and "some account" is still true.</summary>
    public string Icon { get; set; } = "";

    /// <summary>The field whose value is the account a reader recognises the record by, shown in
    /// the credential list and searched by. Empty for a kind that names no account.</summary>
    public string AccountField { get; set; } = "";

    public List<CredentialFieldDescriptor> Fields { get; set; } = new();

    /// <summary>
    /// What the stored values become when a source presents the credential.
    ///
    /// It runs in the process that loaded the plugin, on every use — which is also how a kind
    /// says a record is no longer good: throwing here is what makes the manager mark the row
    /// unusable and a mount report the message rather than "the credential does not exist".
    ///
    /// Not serialized: a front end receives the descriptor to draw the form, never the code that
    /// builds the credential, and the delegate it sees is the one below.
    /// </summary>
    [JsonIgnore]
    public Func<IReadOnlyDictionary<string, string>, Credential> Create { get; init; } = _ =>
        throw new InvalidOperationException("this credential kind does not build a credential");
}
