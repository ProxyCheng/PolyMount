using System.Text.Json;

namespace PolyMount.Plugins.Svn;

/// <summary>What the manifest-family strategy treats as a package manifest, and how far one
/// lookup may go. The defaults match a content repository whose packages are directories named
/// <c>&lt;family&gt;_&lt;member&gt;.local</c> holding a <c>resource.repository</c> index; every
/// part of that shape is a parameter, so another engine's layout is a configuration change
/// rather than a second plugin.</summary>
public sealed class ManifestFamilyOptions
{
    /// <summary>The file a package keeps its resource index in. One manifest per package, at
    /// the package root; engines open them in families during start-up, which is the pattern
    /// this strategy gets ahead of.</summary>
    public string ManifestName { get; set; } = "resource.repository";
    /// <summary>What package directories are called, on top of the package name. Empty means
    /// the directory name is the package name. It is removed before a family is derived.</summary>
    public string PackageSuffix { get; set; } = ".local";
    /// <summary>The character that separates a family from its members in the package name
    /// (<c>char3_10071</c> belongs to <c>char3_</c>). A name without it is its own family.</summary>
    public string FamilySeparator { get; set; } = "_";
    /// <summary>Follow-up manifests one lookup may prefetch. A family can hold hundreds of
    /// packages, and the engine does not read all of them in one go.</summary>
    public int MaxManifests { get; set; } = 16;
    /// <summary>Prefetch the packages that follow the trigger in repository-listing order,
    /// regardless of family. Start-ups that walk the whole repository are sequential in that
    /// order; the family rule guesses by name instead and misses when the order disagrees.</summary>
    public bool Stride { get; set; }
    /// <summary>How many packages a stride lookup stays ahead of the reader.</summary>
    public int StrideLookahead { get; set; } = 4;
    /// <summary>How many lookups may be in flight at once. The source serves the reader and
    /// read-ahead from separate budgets; this bound keeps this strategy a small part of the
    /// latter.</summary>
    public int MaxInFlight { get; set; } = 2;
    public int TickIntervalMs { get; set; } = 50;
    /// <summary>How long the repository listing a lookup is computed from stays valid. The
    /// listing is one request; repeating it for every manifest the engine opens would cost
    /// more than the prefetch saves.</summary>
    public int ListingTtlMs { get; set; } = 120000;
    /// <summary>Quiet time for one family before another lookup of it is run: the engine opens
    /// a family's manifests in bursts, and one burst is one lookup.</summary>
    public int FamilyCooldownMs { get; set; } = 1000;
    /// <summary>Prefetches in a row that nobody read before the family they belong to is
    /// closed. One trigger that keeps guessing wrong must not spend the budget of the ones
    /// that guess right.</summary>
    public int GroupWasteStreak { get; set; } = 16;
    /// <summary>How long a family's counters are remembered across restarts.</summary>
    public int RetentionMs { get; set; } = 7 * 24 * 3600000;

    public static ManifestFamilyOptions From(IReadOnlyDictionary<string, JsonElement> parameters)
    {
        var options = new ManifestFamilyOptions();
        if (parameters.TryGetValue("Manifest.Name", out var name) &&
            name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()))
            options.ManifestName = name.GetString()!;
        if (parameters.TryGetValue("Manifest.PackageSuffix", out var suffix) && suffix.ValueKind == JsonValueKind.String)
            options.PackageSuffix = suffix.GetString()!;
        if (parameters.TryGetValue("Manifest.FamilySeparator", out var separator) &&
            separator.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(separator.GetString()))
            options.FamilySeparator = separator.GetString()!;
        options.MaxManifests = Number(parameters, "Manifest.MaxManifests", options.MaxManifests);
        if (parameters.TryGetValue("Manifest.Stride", out var stride) &&
            (stride.ValueKind == JsonValueKind.True || stride.ValueKind == JsonValueKind.False))
            options.Stride = stride.GetBoolean();
        options.StrideLookahead = Number(parameters, "Manifest.StrideLookahead", options.StrideLookahead);
        options.MaxInFlight = Number(parameters, "Manifest.MaxInFlight", options.MaxInFlight);
        options.TickIntervalMs = Number(parameters, "Manifest.TickIntervalMs", options.TickIntervalMs);
        options.ListingTtlMs = Number(parameters, "Manifest.ListingTtlMs", options.ListingTtlMs);
        options.FamilyCooldownMs = Number(parameters, "Manifest.FamilyCooldownMs", options.FamilyCooldownMs);
        options.GroupWasteStreak = Number(parameters, "Manifest.GroupWasteStreak", options.GroupWasteStreak);
        options.RetentionMs = Number(parameters, "Manifest.RetentionHours", options.RetentionMs / 3600000) * 3600000;
        if (options.TickIntervalMs is < 10 or > 10000 || options.MaxManifests is < 1 or > 4096 ||
            options.StrideLookahead is < 1 or > 256 ||
            options.MaxInFlight is < 1 or > 64 || options.ListingTtlMs is < 0 or > 86400000 ||
            options.FamilyCooldownMs is < 0 or > 600000 || options.GroupWasteStreak is < 1 or > 1000000 ||
            options.RetentionMs is < 0 or > 604800000)
            throw new ArgumentException("Invalid manifest prediction budgets");
        return options;
    }

    private static int Number(IReadOnlyDictionary<string, JsonElement> parameters, string key, int fallback) =>
        parameters.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32() : fallback;
}
