using System.Text.Json;
using System.Text.Json.Serialization;
using PolyMount.Core;
using PolyMount.Core.Ipc;

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
    private readonly Func<string, ShimTarget?> _inspect;
    private readonly string _payloadSource;
    private readonly object _gate = new();

    internal TxFShimService(string directory, IImageFileExecutionOptions registry,
        Func<string, TargetDiscovery.Result>? scan = null, string? payloadSource = null,
        Func<string, ShimTarget?>? inspect = null)
    {
        Directory = Path.GetFullPath(directory);
        _registry = registry;
        _scan = scan ?? TargetDiscovery.Scan;
        _inspect = inspect ?? TargetDiscovery.Inspect;
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

    /// <summary>
    /// What this plugin wants a person told about <paramref name="instance"/>: the programs that
    /// are using the mount and would fail to save on it, and — for one already instrumented but
    /// started before that happened — that it needs a restart.
    ///
    /// This is the only form the failure takes on this side of the boundary. A transactional open
    /// is refused by the kernel before the file system is asked (the volume's device object does
    /// not claim transaction support, and WinFsp gives nobody a way to change that), so no request
    /// log, recording or metric ever carries it. What is observable is who is using the mount and
    /// what their images can do — the same rule an install uses — and that is enough to say
    /// something before the save fails rather than after.
    ///
    /// The scan reads whole files, so answers are remembered per image and keyed by size and
    /// modification time: this runs whenever a front end asks, and a directory of engine binaries
    /// is a few hundred megabytes.
    /// </summary>
    internal IReadOnlyList<PluginNotice> Notices(MountStatus status)
    {
        if (!status.Mounted) return Array.Empty<PluginNotice>();

        var notices = new List<PluginNotice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ShimState state;
        lock (_gate) state = Load();

        foreach (var caller in status.Callers)
        {
            if (caller.ImagePath.Length == 0) continue;
            // This product's own processes use the mount to keep its stores; they are nobody's
            // program to advise about.
            if (caller.Name.StartsWith("PolyMount", StringComparison.OrdinalIgnoreCase)) continue;
            string image = caller.ImagePath;
            if (!seen.Add(image)) continue;
            string name = Path.GetFileName(image);
            if (name.Length == 0) continue;
            if (Evidence(image) is not { } evidence) continue;

            string? existing = _registry.Launcher(name);
            if (existing == null)
            {
                notices.Add(new PluginNotice(
                    $"txf-shim:unshimmed:{image.ToLowerInvariant()}",
                    WindowTitle,
                    Text(
                        $"{name} is using the mount {status.MountPoint} and can open a file inside a transaction.\n\n" +
                        $"This volume cannot accept a transactional open, so saving through it fails with error 6832 " +
                        $"(the image {evidence}). Start it through the compatibility layer?\n\n" +
                        "A program that is already running keeps the behaviour it started with until it is restarted.",
                        $"{name} 正在使用挂载 {status.MountPoint}，并且能够在事务中打开文件。\n\n" +
                        $"这个卷无法接受事务性打开，经由它的保存会失败（错误 6832，依据：{evidence}）。" +
                        "要让它经由兼容层启动吗？\n\n" +
                        "已经在运行的程序要保持到重启后才生效。"),
                    Text("Enable compatibility", "启用兼容层"),
                    new ControlRequest { Cmd = TxFShimPlugin.Command, Action = "install", Path = image }));
                continue;
            }

            // Instrumented — but the shim is injected when a process starts, so one that was
            // already running when the value was written never saw it.
            if (!SamePath(existing, PayloadLauncher)) continue;
            var entry = state.Entries.FirstOrDefault(e =>
                string.Equals(e.ImageName, name, StringComparison.OrdinalIgnoreCase));
            if (entry == null) continue;
            if (!DateTime.TryParse(caller.StartedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var started)) continue;
            if (!DateTime.TryParse(entry.InstalledUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var installed)) continue;
            if (started >= installed) continue;

            notices.Add(new PluginNotice(
                $"txf-shim:restart:{image.ToLowerInvariant()}",
                WindowTitle,
                Text(
                    $"{name} was started before the compatibility layer was enabled, so it is still running without it.\n\n" +
                    "Restart it for the change to take effect.",
                    $"{name} 在启用兼容层之前就已经启动，因此现在仍在没有兼容层的情况下运行。\n\n" +
                    "请重启它，改动才会生效。")));
        }
        return notices;
    }

    private static readonly LocalizedText WindowTitle = new("Transactional opens", "事务性打开");

    private static LocalizedText Text(string en, string zh) => new(en, zh);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Size, DateTime Modified, string? Evidence)> _scans =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What an image can do, from a remembered answer when the file has not changed since.
    /// Null means it reaches no transactional API, or it could not be read.</summary>
    private string? Evidence(string image)
    {
        long size;
        DateTime modified;
        try
        {
            var info = new FileInfo(image);
            if (!info.Exists) return null;
            size = info.Length;
            modified = info.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        if (_scans.TryGetValue(image, out var known) && known.Size == size && known.Modified == modified)
            return known.Evidence;
        string? evidence;
        try { evidence = _inspect(image)?.Evidence; }
        catch (Exception) { evidence = null; }
        _scans[image] = (size, modified, evidence);
        return evidence;
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
    /// Instruments every image under <paramref name="root"/> that reaches a transactional API, or
    /// the one image when <paramref name="root"/> names an executable.
    ///
    /// The single-image form is what a reminder about a program that is already running uses: the
    /// tray knows the path of the image that is using the mount, not the directory somebody would
    /// have chosen, and instrumenting its siblings because they share a folder would be an answer
    /// to a question nobody asked. The registry is keyed by image <em>name</em>, so the effect is
    /// machine-wide either way; the path only decides what was looked at.
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
            bool oneImage = root.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            var targets = oneImage
                ? new[] { _inspect(root) ?? throw new InvalidOperationException(
                    $"'{Path.GetFullPath(root)}' does not reach a transactional file API; there is nothing to instrument") }
                : (IReadOnlyList<ShimTarget>)_scan(root).Targets;
            if (targets.Count == 0)
                throw new InvalidOperationException(
                    $"no image under '{Path.GetFullPath(root)}' imports a transactional file API; there is nothing to instrument");
            string installedFrom = oneImage ? (Path.GetDirectoryName(Path.GetFullPath(root)) ?? Path.GetFullPath(root)) : Path.GetFullPath(root);

            foreach (var target in targets)
            {
                string? existing = _registry.Launcher(target.ImageName);
                if (existing != null && !SamePath(existing, PayloadLauncher))
                    throw new InvalidOperationException(
                        $"'{target.ImageName}' already has an Image File Execution Options Debugger value ('{existing}'); " +
                        "this plugin will not replace another tool's entry");
            }

            var state = Load();
            foreach (var target in targets)
            {
                _registry.SetLauncher(target.ImageName, PayloadLauncher);
                state.Entries.RemoveAll(e => string.Equals(e.ImageName, target.ImageName, StringComparison.OrdinalIgnoreCase));
                state.Entries.Add(new ShimEntry
                {
                    ImageName = target.ImageName,
                    ImagePath = target.ImagePath,
                    Root = installedFrom,
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
    /// source, and a copy that differs from what is installed replaces it — this is how a fixed
    /// launcher reaches a machine that already has one: the payload is what instrumented programs
    /// actually run, and a deploy that only filled in a missing file would leave the old launcher
    /// in place for the life of the installation.</summary>
    private void DeployPayload()
    {
        string source = _payloadSource;
        string sourceDll = Path.Combine(source, "PolyMount.TxFShim.dll");
        string sourceLauncher = Path.Combine(source, "PolyMount.TxFShim.exe");
        if (!File.Exists(sourceDll) || !File.Exists(sourceLauncher))
        {
            // What is installed is all there is: a build without the C++ toolchain publishes the
            // plugin without its native half, and an installation that already has the payload
            // keeps working with it. Only having nothing at all is refused — an Image File
            // Execution Options value that points at a missing launcher keeps the instrumented
            // program from starting at all.
            if (PayloadPresent) return;
            throw new InvalidOperationException(
                $"the native shim is not deployed: expected '{sourceDll}' and '{sourceLauncher}'. " +
                "Nothing is written until those exist — an Image File Execution Options value that points at a missing " +
                "launcher keeps the instrumented program from starting at all");
        }
        System.IO.Directory.CreateDirectory(Directory);
        CopyPayload(sourceDll, PayloadDll);
        CopyPayload(sourceLauncher, PayloadLauncher);
    }

    /// <summary>Replaces an installed file when the plugin ships a different one. A copy that
    /// cannot be made because the old launcher is running is reported rather than skipped: an
    /// install that says it succeeded and left the old launcher in place is the state that is
    /// hardest to notice.</summary>
    private static void CopyPayload(string source, string destination)
    {
        try
        {
            if (File.Exists(destination) && SameContent(source, destination)) return;
            File.Copy(source, destination, overwrite: true);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(destination)}' is running or in use, so the copy this plugin ships could not replace it. " +
                "Close the programs started through the compatibility layer and install again.", ex);
        }
    }

    private static bool SameContent(string left, string right)
    {
        var one = new FileInfo(left);
        var other = new FileInfo(right);
        if (one.Length != other.Length) return false;
        using var a = File.OpenRead(left);
        using var b = File.OpenRead(right);
        var leftBytes = new byte[64 * 1024];
        var rightBytes = new byte[64 * 1024];
        while (true)
        {
            int read = a.Read(leftBytes, 0, leftBytes.Length);
            if (read != b.Read(rightBytes, 0, rightBytes.Length)) return false;
            if (read == 0) return true;
            if (!leftBytes.AsSpan(0, read).SequenceEqual(rightBytes.AsSpan(0, read))) return false;
        }
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
