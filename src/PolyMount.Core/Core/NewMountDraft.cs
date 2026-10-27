using System.Globalization;
using System.IO;

namespace PolyMount.Core;

/// <summary>What a first configuration is made of: a preset, the few values a preset cannot know
/// (what the instance is called, where it mounts, and the source's own address and account), and
/// the preset's defaults for everything else.
///
/// It lives in Core rather than in the window because none of it is about widgets. Turning an
/// address into a name, keeping that name unique, proposing a mount path, and merging a preset
/// into a new profile are answers a service, a command line or a test would give the same way —
/// and the rules they follow (a name is compared case-insensitively, a mount point is compared
/// after normalisation) are the configuration's, not a dialog's.</summary>
public static class NewMountDraft
{
    /// <summary>The directory a first instance lands in when nothing on the machine suggests a
    /// better one. Under the reader's profile rather than beside the sources: it is theirs, it
    /// needs no elevation, and it cannot collide with a repository checkout that happens to share
    /// a drive.</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PolyMount");

    /// <summary>The layer a preset mounts through: the one whose type is a registered source.
    /// A preset with none (caches only) is legal; it simply asks for nothing but a name.</summary>
    public static LayerConfig? SourceLayer(IEnumerable<LayerConfig> layers, IEnumerable<string> sourceTypes)
    {
        var types = new HashSet<string>(sourceTypes, StringComparer.OrdinalIgnoreCase);
        return layers.FirstOrDefault(layer => types.Contains(layer.Type));
    }

    public static LayerDescriptor? Descriptor(string? type, IEnumerable<LayerDescriptor> descriptors) =>
        type == null ? null : descriptors.FirstOrDefault(descriptor =>
            string.Equals(descriptor.Type, type, StringComparison.OrdinalIgnoreCase));

    /// <summary>The source descriptor of the preset — what the second page asks about.</summary>
    public static LayerDescriptor? SourceDescriptor(LayerPreset preset, IEnumerable<LayerDescriptor> descriptors,
        IEnumerable<string> sourceTypes) =>
        Descriptor(SourceLayer(preset.Layers, sourceTypes)?.Type, descriptors);

    /// <summary>The fields a preset cannot answer for the reader: the source's required values
    /// (a repository's address, a share's path) and the account it presents. Everything else keeps
    /// the preset's default and is left to the editor, where the reader can see the whole chain.</summary>
    public static List<LayerFieldDescriptor> Essentials(LayerDescriptor? descriptor)
    {
        if (descriptor == null) return new();
        return descriptor.Fields
            .Where(field => field.Required || field.Kind == LayerFieldKind.Credential)
            .ToList();
    }

    /// <summary>A name for an instance, taken from what it mounts. The last path component is the
    /// part a reader recognises — the repository's own name, the share, the folder — and looking at
    /// the address is how every checkout dialog arrives at its default folder. Only the shape is
    /// borrowed from the address: it is still the reader's to change, which is why the window stops
    /// following the address as soon as they type over it.</summary>
    public static string NameFromSource(string? source)
    {
        string text = (source ?? "").Trim();
        if (text.Length == 0) return "Mount";
        // A URL keeps its path in AbsolutePath; a UNC path or a drive path parses as a file URI (or
        // not at all), and its separators are the ones on disk. Both end with the component that
        // names the thing being mounted.
        string? host = null;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri && !uri.IsFile)
        {
            host = uri.Host;
            text = uri.AbsolutePath;
            try { text = Uri.UnescapeDataString(text); } catch (FormatException) { }
        }
        string name = LastComponent(text);
        // A repository at the site root ("https://svn.example.com") has no path to take a name
        // from; the site's own name is the next best answer.
        if (name.Length == 0 && !string.IsNullOrEmpty(host)) name = host.Split('.')[0];
        // A name is written into a log folder and compared with every other instance, so what
        // cannot be a folder name cannot be a name either.
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '-');
        name = name.Trim().Trim('.');
        if (name.Length == 0 || name.All(c => c == '-')) name = "Mount";
        if (name.Length > 64) name = name[..64].TrimEnd('-', '.', ' ');
        return name.Length == 0 ? "Mount" : name;
    }

    private static string LastComponent(string path)
    {
        string text = path.TrimEnd('/', '\\');
        int cut = Math.Max(text.LastIndexOfAny(new[] { '/', '\\' }), text.LastIndexOf(':'));
        return cut >= 0 ? text[(cut + 1)..] : text;
    }

    /// <summary>The first free spelling of a name. Two instances may not share one — the service
    /// refuses the configuration — and "demo-2" is the familiar way out of the collision.</summary>
    public static string UniqueName(string name, IEnumerable<string> taken)
    {
        string wanted = string.IsNullOrWhiteSpace(name) ? "Mount" : name.Trim();
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(wanted)) return wanted;
        for (int suffix = 2; ; suffix++)
        {
            string candidate = wanted + "-" + suffix.ToString(CultureInfo.InvariantCulture);
            if (!used.Contains(candidate)) return candidate;
        }
    }

    /// <summary>A place to mount: the root the reader last used (or the one the machine's other
    /// instances live under), plus the instance's name. A name already in use as a mount point gets
    /// a suffix rather than an argument — the reader asked for a new instance, not for this path.</summary>
    public static string SuggestMountPoint(string root, string name, IEnumerable<string> taken)
    {
        string folder = string.IsNullOrWhiteSpace(name) ? "Mount" : name.Trim();
        var used = new HashSet<string>(taken.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeMountPoint), StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(root, folder);
        for (int suffix = 2; used.Contains(NormalizeMountPoint(path)); suffix++)
            path = Path.Combine(root, folder + "-" + suffix.ToString(CultureInfo.InvariantCulture));
        return path;
    }

    /// <summary>Two mount points are the same place when they name the same absolute path; the
    /// comparison is the one Windows itself makes, case-insensitively, with a trailing separator
    /// ignored.</summary>
    public static string NormalizeMountPoint(string path)
    {
        string trimmed = (path ?? "").Trim();
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed)); }
        catch (Exception) { return trimmed; }
    }

    /// <summary>The root the next instance should be proposed under: what the reader last chose,
    /// else the parent of an instance this machine already has (its convention is better than a
    /// guess), else <see cref="DefaultRoot"/>.</summary>
    public static string SuggestRoot(string? remembered, IEnumerable<string> existingMountPoints)
    {
        if (!string.IsNullOrWhiteSpace(remembered)) return remembered.Trim();
        foreach (string mountPoint in existingMountPoints)
        {
            string parent;
            try { parent = Path.GetDirectoryName(NormalizeMountPoint(mountPoint)) ?? ""; }
            catch (Exception) { continue; }
            if (parent.Length > 0 && Directory.Exists(parent)) return parent;
        }
        return DefaultRoot;
    }

    /// <summary>Composes the profile: the preset's chain, with the typed values written into the
    /// layer that mounts the source. The compatibility fields the editor keeps in step
    /// (<see cref="MountProfile.Source"/>, <see cref="MountProfile.CacheCapacityBytes"/>) are
    /// written here too, because a service reading this configuration must see the same instance
    /// the window just described.</summary>
    public static MountProfile Create(LayerPreset preset, string name, string mountPoint,
        IEnumerable<(LayerFieldDescriptor Field, string Value)> values,
        IEnumerable<LayerDescriptor> descriptors, IEnumerable<string> sourceTypes)
    {
        var layers = LayerPresetMerger.Merge(Array.Empty<LayerConfig>(), preset);
        var source = SourceLayer(layers, sourceTypes);
        if (source != null && Descriptor(source.Type, descriptors) is { } descriptor)
        {
            // Only the fields this source's descriptor declares: a preset may carry layers of
            // other types, and a value is written to the layer it was asked on.
            var declared = new HashSet<string>(descriptor.Fields.Select(field => field.Path), StringComparer.OrdinalIgnoreCase);
            LayerProperties.Apply(source, values
                .Where(value => declared.Contains(value.Field.Path))
                .Select(value => (value.Field, value.Value)));
        }
        var profile = new MountProfile
        {
            Name = (name ?? "").Trim(),
            MountPoint = (mountPoint ?? "").Trim(),
            Enabled = true,
            Layers = layers
        };
        // The identity is made now and never changes: it names the store the instance will keep
        // its cache, its overlay and its pin in.
        profile.EnsureId();
        if (source != null) profile.Source = source.Source;
        profile.CacheCapacityBytes = layers
            .Where(layer => string.Equals(layer.Type, "cache", StringComparison.OrdinalIgnoreCase))
            .Sum(layer => layer.CacheCapacityBytes);
        return profile;
    }
}
