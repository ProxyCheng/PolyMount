namespace PolyMount.Plugins;

/// <summary>
/// Which layer types would present a given credential, asked of the layers themselves.
///
/// A layer used to write down the credential *kinds* it accepted, which made every new kind a
/// change to every layer that could use it — and a kind a plugin added after that list was written
/// invisible to it, however well the layer could have presented the credential. What a layer
/// actually knows is what it can present: a name and a password, a client certificate, a token.
/// So the filter is a predicate the layer registers here, asked with the credential itself, and the
/// answer for a stored record is computed where the plugins live and travels with the record
/// (<see cref="CredentialRecord.UseFor"/>).
///
/// This is also why the answer is per record rather than per kind: two credentials of one kind can
/// differ in what they can do (a kind may build an account one time and something else the next),
/// and the question "would this layer take *this* credential" is the one a picker asks.
/// </summary>
public static class CredentialConsumers
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Func<Credential, bool>> Registered =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every layer type that says what it presents, in the order the plugins registered
    /// them.</summary>
    public static IReadOnlyList<string> All
    {
        get { lock (Gate) return Registered.Keys.ToArray(); }
    }

    /// <summary>The layer types that would present <paramref name="credential"/>. Empty means no
    /// loaded layer can, which is what hides the record from every picker — the record itself is
    /// still there, and the manager still shows it.</summary>
    public static IReadOnlyList<string> Of(Credential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        List<string>? accepting = null;
        lock (Gate)
        {
            foreach (var (type, accepts) in Registered)
            {
                bool answer;
                // A filter that throws is a plugin's bug. It must not make the whole credential
                // list unreadable, and "this layer cannot say" is not "this layer takes it".
                try { answer = accepts(credential); }
                catch (Exception) { continue; }
                if (answer) (accepting ??= new List<string>()).Add(type);
            }
        }
        return accepting ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    /// <summary>Adds a filter. A second plugin claiming one layer type is refused rather than
    /// replacing the first: which code decides what a layer presents is not something a load order
    /// should settle.</summary>
    internal static void Register(string type, Func<Credential, bool> accepts)
    {
        ArgumentNullException.ThrowIfNull(accepts);
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("a credential consumer needs the layer type it speaks for");
        lock (Gate)
        {
            if (Registered.ContainsKey(type))
                throw new ArgumentException("the layer type '" + type + "' already says which credentials it presents");
            Registered[type] = accepts;
        }
    }

    internal static void Unregister(string type)
    {
        lock (Gate) Registered.Remove(type);
    }

    /// <summary>Drops every filter. They all belong to plugins, so unloading is all or nothing, the
    /// same rule as the credential kinds.</summary>
    internal static void Clear()
    {
        lock (Gate) Registered.Clear();
    }
}
