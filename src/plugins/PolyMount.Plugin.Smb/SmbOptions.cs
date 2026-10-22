using System.Text.Json;

namespace PolyMount.Plugins.Smb;

/// <summary>What a remote folder layer may be tuned with, read once when the layer is built.</summary>
public sealed record SmbOptions(long GenerationTtlSeconds)
{
    public const long DefaultGenerationTtlSeconds = 5;
    public const long MinimumTtlSeconds = 1;
    public const long MaximumTtlSeconds = 300;

    public static SmbOptions From(IReadOnlyDictionary<string, JsonElement>? parameters)
    {
        long ttl = DefaultGenerationTtlSeconds;
        if (parameters != null && parameters.TryGetValue("GenerationTtlSeconds", out var value))
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out ttl))
                throw new ArgumentException("GenerationTtlSeconds must be a whole number of seconds");
        }
        if (ttl < MinimumTtlSeconds || ttl > MaximumTtlSeconds)
            throw new ArgumentException("GenerationTtlSeconds must be between " + MinimumTtlSeconds + " and " + MaximumTtlSeconds);
        return new SmbOptions(ttl);
    }
}
