using System.Text.Json;

namespace PolyMount.Core;

/// <summary>Applies preset ordering without resetting existing provider identities or settings.</summary>
public static class LayerPresetMerger
{
    public static List<LayerConfig> Merge(IEnumerable<LayerConfig> existing, LayerPreset preset)
    {
        var remaining = existing.ToList();
        var matches = new LayerConfig?[preset.Layers.Count];
        var ids = remaining.Select(l => l.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Reserve exact matches first so an earlier same-type slot cannot steal one.
        for (int i = 0; i < preset.Layers.Count; i++)
        {
            var slot = preset.Layers[i];
            var match = remaining.FirstOrDefault(l => Same(l.Type, slot.Type) && Same(l.Id, slot.Id));
            if (match == null) continue;
            matches[i] = match;
            remaining.Remove(match);
        }
        var result = new List<LayerConfig>();
        for (int i = 0; i < preset.Layers.Count; i++)
        {
            var slot = preset.Layers[i];
            var match = matches[i] ?? remaining.FirstOrDefault(l => Same(l.Type, slot.Type));
            if (match != null)
            {
                remaining.Remove(match);
                result.Add(match);
                continue;
            }
            var added = JsonSerializer.Deserialize<LayerConfig>(JsonSerializer.Serialize(slot))!;
            string preferred = string.IsNullOrWhiteSpace(added.Id) ? added.Type : added.Id;
            string id = preferred;
            for (int suffix = 2; !ids.Add(id); suffix++) id = preferred + "-" + suffix;
            added.Id = id;
            result.Add(added);
        }
        result.AddRange(remaining);
        return result;
    }

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
