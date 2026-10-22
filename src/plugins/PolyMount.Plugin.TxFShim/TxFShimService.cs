using System.Text.Json;
using System.Text.Json.Serialization;

namespace PolyMount.Plugins.TxFShim;

/// <summary>One image this plugin has instrumented.</summary>
internal sealed class ShimEntry
{
    public string ImageName { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public string Root { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string InstalledUtc { get; set; } = "";
    public string Launcher { get; set; } = "";
}

/// <summary>A disagreement between the record of what we installed and the registry itself.</summary>
internal sealed record ShimDrift(string Image, string Kind, string Detail);

internal sealed record ShimTargetView(string Image, string Path, string Evidence);

internal sealed record ShimScan(string Root, IReadOnlyList<ShimTargetView> Targets, IReadOnlyList<string> Notes);

internal sealed record ShimStatus(
    string Plugin,
    string Directory,
    bool PayloadPresent,
    string Dll,
    string Launcher,
    IReadOnlyList<ShimEntry> Installed,
    IReadOnlyList<ShimDrift> Drift,
    string Log,
    long LogBytes);

internal sealed record ShimLogTail(string Path, bool Exists, IReadOnlyList<string> Lines);

/// <summary>
/// What the plugin does, with the file system and the registry as inputs so a test can supply both.
///
/// The registry is the truth of what is instrumented; the state file is only the record of what
/// this plugin wrote. Keeping them apart is what makes drift reportable instead of silently
/// repaired: a key that disappeared under us and a key that points at something else are different
/// facts, and neither is a reason to overwrite someone else's configuration.
/// </summary>
internal sealed class TxFShimService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly IImageFileExecutionOptions _registry;
    private readonly Func<string, TargetDiscovery.Result> _scan;
    private readonly string _payloadSource;
    private readonly object _gate = new();

    internal TxFShimService(string directory, IImageFileExecutionOptions registry,
        Func<string, TargetDiscovery.Result>? scan = null, string? payloadSource = null)
    {
        Directory = Path.GetFullPath(directory);
        _registry = registry;
        _scan = scan ?? TargetDiscovery.Scan;
        // The plugin's own folder is where the build puts the native half. A test supplies its own
        // so that "no payload" can be the state it tests, rather than a property of the checkout.
        _payloadSource = payloadSource
            ?? Path.Combine(Path.GetDirectoryName(typeof(TxFShimPlugin).Assembly.Location) ?? AppContext.BaseDirectory, "native");
    }

    /// <summary>Where the stable copy of the native payload and the record live.</summary>
    internal string Directory { get; }
    internal string StatePath => Path.Combine(Directory, "state.json");
    internal string LogPath => Path.Combine(Directory, "hits.log");
    internal string PayloadDll => Path.Combine(Directory, "PolyMount.TxFShim.dll");
    internal string PayloadLauncher => Path.Combine(Directory, "PolyMount.TxFShim.exe");

    /// <summary>The stable copy both the registry and the injected image name point at. It has to
    /// be readable by whoever runs the engine, and writable by nobody — a user-writable path here
    /// would be a DLL hijack of every instrumented process.</summary>
    internal bool PayloadPresent => File.Exists(PayloadDll) && File.Exists(PayloadLauncher);

    internal static JsonSerializerOptions SerializerOptions => Json;

    internal ShimStatus Status()
    {
        lock (_gate)
        {
            var state = Load();
            var drift = new List<ShimDrift>();
            foreach (var entry in state.Entries)
            {
                string? current = _registry.Launcher(entry.ImageName);
                if (current == null)
                    drift.Add(new ShimDrift(entry.ImageName, "missing",
                        "the record says this image is instrumented, but its Debugger value is gone"));
                else if (!SamePath(current, entry.Launcher))
                    drift.Add(new ShimDrift(entry.ImageName, "changed",
                        $"the Debugger value points at '{current}', not at '{entry.Launcher}'"));
            }
            var recorded = state.Entries.Select(e => e.ImageName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string image in _registry.Instrumented())
            {
                if (recorded.Contains(image)) continue;
                if (SamePath(_registry.Launcher(image), PayloadLauncher))
                    drift.Add(new ShimDrift(image, "unrecorded",
                        "a Debugger value points at this plugin's launcher, but the record has no entry for it"));
            }

            return new ShimStatus(
                "txf-shim",
                Directory,
                PayloadPresent,
                PayloadDll,
                PayloadLauncher,
                state.Entries.ToArray(),
                drift,
                LogPath,
                File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0);
        }
    }

    /// <summary>What would be instrumented, without touching anything.</summary>
    internal ShimScan Scan(string root)
    {
        var result = _scan(root);
        return new ShimScan(
            Path.GetFullPath(root),
            result.Targets.Select(t => new ShimTargetView(t.ImageName, t.ImagePath, t.Evidence)).ToArray(),
            result.Notes);
    }

    /// <summary>
    /// Instruments every image under <paramref name="root"/> that reaches a transactional API.
    ///
    /// Refusals are deliberate and come in two kinds. Without the native payload there is nothing
    /// for the Debugger value to point at, and an engine that cannot start is worse than one that
    /// cannot save. A Debugger value that already points somewhere else belongs to something else —
    /// another compatibility shim, a vendor's tool — and taking it would break that instead.
    /// Either check runs over every target before the first write, so an install is all or nothing.
    /// </summary>
    internal ShimStatus Install(string root)
    {
        lock (_gate)
        {
            DeployPayload();
            EnsureLogDirectory();
            var scan = _scan(root);
            if (scan.Targets.Count == 0)
                throw new InvalidOperationException(
                    $"no image under '{Path.GetFullPath(root)}' imports a transactional file API; there is nothing to instrument");

            foreach (var target in scan.Targets)
            {
                string? existing = _registry.Launcher(target.ImageName);
                if (existing != null && !SamePath(existing, PayloadLauncher))
                    throw new InvalidOperationException(
                        $"'{target.ImageName}' already has an Image File Execution Options Debugger value ('{existing}'); " +
                        "this plugin will not replace another tool's entry");
            }

            var state = Load();
            foreach (var target in scan.Targets)
            {
                _registry.SetLauncher(target.ImageName, PayloadLauncher);
                state.Entries.RemoveAll(e => string.Equals(e.ImageName, target.ImageName, StringComparison.OrdinalIgnoreCase));
                state.Entries.Add(new ShimEntry
                {
                    ImageName = target.ImageName,
                    ImagePath = target.ImagePath,
                    Root = Path.GetFullPath(root),
                    Evidence = target.Evidence,
                    InstalledUtc = DateTime.UtcNow.ToString("O"),
                    Launcher = PayloadLauncher
                });
            }
            Save(state);
        }
        return Status();
    }

    /// <summary>
    /// Removes what this plugin installed: one named image, or every image under one root.
    /// A value that no longer points at this plugin's launcher is left alone and reported — the
    /// record is dropped either way, because from here on it describes nothing we own.
    /// </summary>
    internal ShimStatus Remove(string? root, string? image)
    {
        // The pipe's Name field carries the instance, so an image travels in Path: a path ending in
        // .exe names one image, anything else is the directory it was installed from.
        if (string.IsNullOrWhiteSpace(image) && root != null &&
            root.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            image = Path.GetFileName(root);
            root = null;
        }
        if (string.IsNullOrWhiteSpace(root) && string.IsNullOrWhiteSpace(image))
            throw new ArgumentException("remove_txf_shim needs a root or an image");

        lock (_gate)
        {
            var state = Load();
            string? fullRoot = string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root);
            var doomed = state.Entries.Where(entry =>
                    (!string.IsNullOrWhiteSpace(image) && string.Equals(entry.ImageName, image, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(fullRoot) && SamePath(entry.Root, fullRoot)))
                .ToArray();

            foreach (var entry in doomed)
            {
                if (SamePath(_registry.Launcher(entry.ImageName), entry.Launcher))
                    _registry.RemoveLauncher(entry.ImageName);
                state.Entries.Remove(entry);
            }
            Save(state);
        }
        return Status();
    }

    /// <summary>The last <paramref name="lines"/> hits the injected DLL recorded.</summary>
    internal ShimLogTail Log(int lines)
    {
        if (lines <= 0) lines = 200;
        if (!File.Exists(LogPath)) return new ShimLogTail(LogPath, false, Array.Empty<string>());
        lock (_gate)
        {
            var tail = new List<string>(lines);
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long start = Math.Max(0, stream.Length - 1024 * 1024);
            stream.Position = start;
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null) tail.Add(line);
            if (tail.Count > lines) tail.RemoveRange(0, tail.Count - lines);
            return new ShimLogTail(LogPath, true, tail);
        }
    }

    /// <summary>Puts the native half where the registry points. The plugin's own folder is the
    /// source, so upgrading the plugin replaces the copy on the next install.</summary>
    private void DeployPayload()
    {
        if (PayloadPresent) return;
        string source = _payloadSource;
        string sourceDll = Path.Combine(source, "PolyMount.TxFShim.dll");
        string sourceLauncher = Path.Combine(source, "PolyMount.TxFShim.exe");
        if (!File.Exists(sourceDll) || !File.Exists(sourceLauncher))
            throw new InvalidOperationException(
                $"the native shim is not deployed: expected '{sourceDll}' and '{sourceLauncher}'. " +
                "Nothing is written until those exist — an Image File Execution Options value that points at a missing " +
                "launcher keeps the instrumented program from starting at all");
        System.IO.Directory.CreateDirectory(Directory);
        File.Copy(sourceDll, PayloadDll, overwrite: true);
        File.Copy(sourceLauncher, PayloadLauncher, overwrite: true);
    }

    /// <summary>
    /// Makes the folder the injected shim writes its hits to, and makes it writable by the people
    /// who run the instrumented program.
    ///
    /// The service creates it as LocalSystem, and a folder created that way inherits nothing that
    /// lets a normal account write: the shim would open the log, fail, and skip the line without
    /// anyone noticing. Append is all that is granted — a log a reader can rewrite is not a record.
    /// </summary>
    private void EnsureLogDirectory()
    {
        System.IO.Directory.CreateDirectory(Directory);
        try
        {
            var security = new System.Security.AccessControl.DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
                System.Security.AccessControl.FileSystemRights.CreateFiles | System.Security.AccessControl.FileSystemRights.AppendData,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));
            System.IO.FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(Directory), security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException)
        {
            // The shim will still work; only its log will be missing. That is better than refusing
            // an install, and Status reports the log size so the difference is visible.
        }
    }

    private ShimState Load()
    {
        try
        {
            if (!File.Exists(StatePath)) return new ShimState();
            return JsonSerializer.Deserialize<ShimState>(File.ReadAllText(StatePath), Json) ?? new ShimState();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A record that cannot be read is not a reason to refuse to work: the registry still
            // holds the truth, and Status reports the difference.
            return new ShimState();
        }
    }

    private void Save(ShimState state)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, Json));
    }

    internal static bool SamePath(string? left, string? right)
    {
        if (left == null || right == null) return false;
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}

internal sealed class ShimState
{
    public List<ShimEntry> Entries { get; set; } = new();
}
