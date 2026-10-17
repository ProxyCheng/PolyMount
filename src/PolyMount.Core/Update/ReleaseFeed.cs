using System.Text.Json;

namespace PolyMount.Core.Update;

/// <summary>One published version, as a release feed describes it.</summary>
public sealed record ReleaseInfo(string Version, string Url);

/// <summary>Reads a release feed and decides whether it is newer than what is running.
///
/// Kept apart from the thing that fetches and the thing that shows it: what a feed says and how a
/// version is spelled are questions with answers that can be checked without a network or a
/// window, and the tray is the worst possible place to have to check them.
///
/// The shape accepted is GitHub's — <c>tag_name</c> and <c>html_url</c> — with <c>version</c> and
/// <c>url</c> accepted in their place, so a feed served as a plain file works without pointing the
/// product at GitHub.</summary>
public static class ReleaseFeed
{
    public static ReleaseInfo? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        var version = Text(root, "tag_name") ?? Text(root, "version");
        if (string.IsNullOrWhiteSpace(version)) return null;
        return new ReleaseInfo(version, Text(root, "html_url") ?? Text(root, "url") ?? "");
    }

    /// <summary>Accepts "1.2.3", "v1.2.3" and "1.2.3+abc" — what a tag or a build stamp looks
    /// like — and reports nothing for anything else, so an unparseable feed degrades to "could not
    /// tell" rather than to a comparison against zero.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text[0] is 'v' or 'V') text = text[1..];
        int end = 0;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == '.')) end++;
        if (end == 0) return null;
        return Version.TryParse(text[..end].TrimEnd('.'), out var version) ? version : null;
    }

    /// <summary>Whether the feed's version is ahead of <paramref name="current"/>. A version that
    /// cannot be read is never "newer": announcing an update needs a reason.</summary>
    public static bool IsNewer(string? tag, Version current) =>
        ParseVersion(tag) is { } version && version > current;

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
