using PolyMount.Core.Ipc;

namespace PolyMount.Tray;

/// <summary>
/// Which layers name each credential, as "instance / layer". A delete names these before it
/// happens, so an account in use is not removed silently. A service that cannot be asked yields
/// nothing rather than failing the dialog.
///
/// One entry per instance, taken from the layer that actually talks to the source — the deepest one
/// that names a credential. Counting every layer whose <c>Source</c> happens to carry an id read as
/// "6 layers" for five instances: two of them still had the credential written on their <c>warmup</c>
/// layer as well, left there by whatever made that layer. A storage layer never presents a
/// credential, so a leftover id on one is not a second user of it — and a count that says otherwise
/// sends the reader looking for an instance that does not exist.
/// </summary>
internal static class CredentialUsage
{
    public static Dictionary<string, List<string>> Of()
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var response = SupervisorClient.Default.GetConfig();
            if (response.Config == null) return result;
            return Of(AppConfig.FromJson(response.Config));
        }
        catch (Exception) { }
        return result;
    }

    /// <summary>The same answer from a configuration already in hand, so the rule can be checked
    /// without a service — see <see cref="CredentialManager.Smoke"/>.</summary>
    internal static Dictionary<string, List<string>> Of(AppConfig config)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mount in config.Mounts)
            {
                var layers = mount.GetLayers();
                var naming = layers.LastOrDefault(layer => !string.IsNullOrWhiteSpace(layer.Source.CredentialId));
                string id = naming?.Source.CredentialId ?? (layers.Count == 0 ? mount.Source.CredentialId : "");
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!result.TryGetValue(id, out var names))
                    result[id] = names = new List<string>();
                if (naming == null) { names.Add(mount.Name); continue; }
                string layerName = string.IsNullOrWhiteSpace(naming.Name) ? naming.Type : naming.Name;
                names.Add(mount.Name + " / " + layerName);
            }
        return result;
    }
}
