using PolyMount.Core.Ipc;

namespace PolyMount.Tray;

/// <summary>
/// Which layers name each credential, as "instance / layer". A delete names these before it
/// happens, so an account in use is not removed silently. A service that cannot be asked yields
/// nothing rather than failing the dialog.
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
            foreach (var mount in AppConfig.FromJson(response.Config).Mounts)
                foreach (var layer in mount.GetLayers())
                {
                    string id = layer.Source.CredentialId;
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    if (!result.TryGetValue(id, out var names))
                        result[id] = names = new List<string>();
                    string layerName = string.IsNullOrWhiteSpace(layer.Name) ? layer.Type : layer.Name;
                    names.Add(mount.Name + " / " + layerName);
                }
        }
        catch (Exception) { }
        return result;
    }
}
