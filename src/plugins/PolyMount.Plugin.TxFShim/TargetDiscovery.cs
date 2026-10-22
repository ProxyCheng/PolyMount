namespace PolyMount.Plugins.TxFShim;

/// <summary>The transactional file APIs a user-mode process can reach.
///
/// This is the whole surface: a transaction cannot be attached to a handle any other way from user
/// mode (the create context that carries one is kernel-only), so an image that imports none of
/// these cannot open a file in a transaction at all.
/// </summary>
internal static class TxFImports
{
    internal static IReadOnlyList<string> Apis { get; } = new[]
    {
        "CreateFileTransactedA", "CreateFileTransactedW",
        "CreateDirectoryTransactedA", "CreateDirectoryTransactedW",
        "MoveFileTransactedA", "MoveFileTransactedW",
        "DeleteFileTransactedA", "DeleteFileTransactedW",
        "CopyFileTransactedA", "CopyFileTransactedW",
        "FindFirstFileTransactedA", "FindFirstFileTransactedW",
        "CreateHardLinkTransactedA", "CreateHardLinkTransactedW",
        "CreateSymbolicLinkTransactedA", "CreateSymbolicLinkTransactedW",
        "SetFileAttributesTransactedA", "SetFileAttributesTransactedW",
        "GetFileAttributesTransactedA", "GetFileAttributesTransactedW",
        "RemoveDirectoryTransactedA", "RemoveDirectoryTransactedW"
    };

    /// <summary>The first transactional API in <paramref name="names"/>, or null when there is
    /// none. Matching is ordinal: an import name is a byte string, not a culture-sensitive word.</summary>
    internal static string? First(IReadOnlyList<string> names)
    {
        foreach (string name in names)
            if (Apis.Contains(name, StringComparer.Ordinal)) return name;
        return null;
    }
}

/// <summary>One image that would be injected, and why.</summary>
internal sealed record ShimTarget(string ImagePath, string ImageName, string Evidence);

/// <summary>
/// Which images under a directory reach a transactional API.
///
/// The rule names no product. An image qualifies when it imports one of those APIs itself, when it
/// imports a DLL beside it that does, or when either of them merely quotes the name — because a
/// program that resolves its file APIs at run time imports nothing at all, and the engine this was
/// written for is one of those (it keeps "kernel32.dll" and a table of API names beside each other
/// and calls GetProcAddress).
///
/// That last part is also why the answer is a superset of "images that call it on a given run":
/// whether a process saves is not something its import table, or its string table, can say.
/// </summary>
internal static class TargetDiscovery
{
    internal sealed record Result(IReadOnlyList<ShimTarget> Targets, IReadOnlyList<string> Notes);

    internal static Result Scan(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("a directory is required");
        string full = Path.GetFullPath(root);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"no directory '{full}'");

        // A directory of engine binaries holds the same runtime DLL behind a dozen launchers, and
        // the byte scan reads the whole file — so each file is answered once per scan.
        var imports = new Dictionary<string, PortableExecutable.Imports>(StringComparer.OrdinalIgnoreCase);
        var mentions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        PortableExecutable.Imports Import(string path)
        {
            if (!imports.TryGetValue(path, out var read)) imports[path] = read = PortableExecutable.Read(path);
            return read;
        }
        string? Mention(string path)
        {
            if (!mentions.TryGetValue(path, out var found)) mentions[path] = found = ApiNameScanner.First(path);
            return found;
        }

        var targets = new List<ShimTarget>();
        foreach (string image in Directory.EnumerateFiles(full, "*.exe", SearchOption.TopDirectoryOnly))
        {
            var own = Import(image);
            if (TxFImports.First(own.Names) is { } direct)
            {
                targets.Add(new ShimTarget(image, Path.GetFileName(image), $"imports {direct}"));
                continue;
            }
            if (Mention(image) is { } quoted)
            {
                targets.Add(new ShimTarget(image, Path.GetFileName(image), $"resolves {quoted} at run time"));
                continue;
            }

            // Through a DLL beside the image: resolving next to it (and not through the search
            // path) is deliberate — the question is what this installation loads, not what some
            // other copy of the same name might.
            foreach (string module in own.Modules)
            {
                string beside = Path.Combine(full, module);
                if (!File.Exists(beside)) continue;
                if (TxFImports.First(Import(beside).Names) is { } indirect)
                {
                    targets.Add(new ShimTarget(image, Path.GetFileName(image), $"loads {module}, which imports {indirect}"));
                    break;
                }
                if (Mention(beside) is { } throughModule)
                {
                    targets.Add(new ShimTarget(image, Path.GetFileName(image), $"loads {module}, which resolves {throughModule} at run time"));
                    break;
                }
            }
        }

        targets.Sort((left, right) => string.Compare(left.ImageName, right.ImageName, StringComparison.OrdinalIgnoreCase));
        return new Result(targets, new List<string>());
    }
}
