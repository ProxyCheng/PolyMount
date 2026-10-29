namespace PolyMount.Plugins;

/// <summary>
/// The credential kinds plugins have contributed, beside the two the product ships.
///
/// Registration is a plugin's, and a plugin that is switched off takes its kinds with it — the
/// records of those kinds stay in the store (the file belongs to the person, not to the plugin),
/// they simply cannot be opened until something describes them again. That is why the manager
/// shows them as unusable rather than losing them.
/// </summary>
public static class CredentialKinds
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, CredentialKindDescriptor> Registered =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every kind a loaded plugin describes, in the order the plugins registered them.</summary>
    public static IReadOnlyList<CredentialKindDescriptor> All
    {
        get { lock (Gate) return Registered.Values.ToArray(); }
    }

    public static CredentialKindDescriptor? Find(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return null;
        lock (Gate) return Registered.GetValueOrDefault(kind);
    }

    /// <summary>Whether the product itself knows this kind, without asking a plugin.</summary>
    public static bool IsBuiltIn(string kind) =>
        string.Equals(kind, CredentialTypes.Password, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, CredentialTypes.Certificate, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a stored record of this kind can be understood at all: built in, or
    /// described by a plugin loaded now.</summary>
    public static bool IsKnown(string kind) => IsBuiltIn(kind) || Find(kind) != null;

    /// <summary>Adds a kind. A second plugin claiming a name is refused rather than replacing the
    /// first: which code builds a credential is not something a load order should decide.</summary>
    internal static void Register(CredentialKindDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        string kind = descriptor.Kind ?? "";
        if (kind.Length is 0 or > 64 || !kind.All(IsKindChar))
            throw new ArgumentException("a credential kind is 1-64 letters, digits, '-' or '_': '" + kind + "'");
        if (IsBuiltIn(kind))
            throw new ArgumentException("'" + kind + "' is a credential kind the product ships; a plugin cannot replace it");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in descriptor.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Path))
                throw new ArgumentException("credential kind '" + kind + "' has a field with no path");
            if (!paths.Add(field.Path))
                throw new ArgumentException("credential kind '" + kind + "' names the field '" + field.Path + "' twice");
        }
        if (descriptor.AccountField.Length > 0 && !paths.Contains(descriptor.AccountField))
            throw new ArgumentException("credential kind '" + kind + "' names an account field it does not have");
        lock (Gate)
        {
            if (Registered.ContainsKey(kind))
                throw new ArgumentException("the credential kind '" + kind + "' is already registered");
            Registered[kind] = descriptor;
        }
    }

    internal static void Unregister(string kind)
    {
        lock (Gate) Registered.Remove(kind);
    }

    /// <summary>Drops every plugin kind. They all belong to plugins, so unloading is all or
    /// nothing, the same rule as the commands.</summary>
    internal static void Clear()
    {
        lock (Gate) Registered.Clear();
    }

    private static bool IsKindChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';
}
