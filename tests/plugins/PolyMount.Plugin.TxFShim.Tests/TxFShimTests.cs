using PolyMount.Core;
using PolyMount.Core.Ipc;
using PolyMount.Plugin.Testing;

namespace PolyMount.Plugins.TxFShim;

/// <summary>Tests exported by a separate test DLL; never shipped with the production plugin.</summary>
public sealed class TxFShimTests : IPluginTestProvider
{
    public string PluginName => "txf-shim";

    /// <summary>A check that cannot run here. Reported as skipped rather than passed: a test that
    /// quietly succeeded against nothing would be the one nobody notices.</summary>
    private sealed class Skip(string reason) : Exception(reason);

    private const string Launcher = @"C:\ProgramData\PolyMount\txf-shim\PolyMount.TxFShim.exe";

    public Task<IReadOnlyList<PluginTestResult>> RunAsync(PluginTestContext context)
    {
        var results = new List<PluginTestResult>();
        void Check(string name, Action test)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            try { test(); results.Add(new(PluginName, name, PluginTestOutcome.Passed)); }
            catch (Skip skip) { results.Add(new(PluginName, name, PluginTestOutcome.Skipped, skip.Message)); }
            catch (Exception ex) { results.Add(new(PluginName, name, PluginTestOutcome.Failed, ex.Message)); }
        }

        Check("registrations", () =>
        {
            string path = typeof(TxFShimPlugin).Assembly.Location;
            var errors = PluginLoader.LoadConfigured(new[] { new PluginConfig { AssemblyPath = path } }, Path.GetDirectoryName(path)!);
            if (errors.Count > 0) throw new Exception(string.Join("; ", errors));

            foreach (string operation in new[] { "get_txf_shim", "list_txf_shim_targets", "install_txf_shim", "remove_txf_shim", "read_txf_shim_log" })
                if (ControlOperations.Find(operation) == null) throw new Exception($"'{operation}' was not registered");
            // Installing is the operation that changes the machine; a catalog that marked it read
            // would let an agent call it without being asked.
            if (ControlOperations.Find("install_txf_shim")!.Effect != ControlEffect.Write)
                throw new Exception("install_txf_shim is not declared as a write");
            if (ControlOperations.Find("get_txf_shim")!.Effect != ControlEffect.Read)
                throw new Exception("get_txf_shim is not declared as a read");
            if (typeof(TxFShimPlugin).Assembly == typeof(ILayer).Assembly) throw new Exception("Plugin is not independent");

            // The management page is an extension point like the operations: a plugin that offers
            // one has to be listed, and a plugin that is switched off may not keep offering it.
            var page = PluginPanels.Descriptors().FirstOrDefault(panel => panel.Id == "txf-shim")
                ?? throw new Exception("the management page was not registered");
            // It belongs to the plugin's row in the plugin manager, not to a destination of its own
            // in the sidebar: this is what is managed here, and the plugin page is where a reader
            // goes looking for what a plugin adds.
            if (page.OwnPage) throw new Exception("the page asked for a sidebar destination of its own");

            PluginLoader.LoadConfigured(Array.Empty<PluginConfig>(), AppContext.BaseDirectory);
            if (ControlOperations.Find("get_txf_shim") != null)
                throw new Exception("A disabled plugin's operation remains registered");
            if (PluginPanels.Ids.Contains("txf-shim"))
                throw new Exception("A disabled plugin's management page remains registered");
            PluginLoader.LoadConfigured(new[] { new PluginConfig { AssemblyPath = path } }, Path.GetDirectoryName(path)!);
        });

        Check("import-names", () =>
        {
            if (TxFImports.First(new[] { "CreateFileW", "CreateFileTransactedW" }) != "CreateFileTransactedW")
                throw new Exception("A transactional import was not recognized");
            if (TxFImports.First(new[] { "CreateFileW", "CreateFileTransactedWEx" }) != null)
                throw new Exception("A name that merely starts like one was accepted");
            if (TxFImports.First(Array.Empty<string>()) != null) throw new Exception("An empty list had a match");
        });

        Check("pe-reader", () =>
        {
            string? kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
            if (!File.Exists(kernel32)) throw new Skip("no kernel32.dll to read");
            var imports = PortableExecutable.Read(kernel32);
            if (imports.IsEmpty) throw new Exception("a real system image reported no imports");
            if (imports.Modules.Any(module => module.Length == 0 || !module.Contains(".dll", StringComparison.OrdinalIgnoreCase)))
                throw new Exception("an import module name looks wrong: " + string.Join(", ", imports.Modules));

            // A file that is not an image, and a file that is not there, are both "no imports" —
            // an install scanning a directory must not fail over either.
            string temp = TempDirectory();
            try
            {
                string text = Path.Combine(temp, "readme.exe");
                File.WriteAllText(text, "not an image");
                if (!PortableExecutable.Read(text).IsEmpty) throw new Exception("a text file parsed as an image");
                if (!PortableExecutable.Read(Path.Combine(temp, "missing.exe")).IsEmpty)
                    throw new Exception("a missing file parsed as an image");
            }
            finally { Directory.Delete(temp, recursive: true); }
        });

        Check("discovery", () =>
        {
            string temp = TempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(temp, "plain.exe"), "not an image");
                var empty = TargetDiscovery.Scan(temp);
                if (empty.Targets.Count != 0) throw new Exception("an image with no imports became a target");

                bool threw = false;
                try { TargetDiscovery.Scan(Path.Combine(temp, "absent")); }
                catch (DirectoryNotFoundException) { threw = true; }
                if (!threw) throw new Exception("a missing directory was scanned instead of refused");
            }
            finally { Directory.Delete(temp, recursive: true); }

            // The two rules the engine taught, on fixtures rather than on the engine: an image that
            // does not import a transactional API but names one at run time is still a target, and
            // so is an image that loads a DLL beside it which names one. The engine used to be the
            // fixture for both; on 2026-10-28 it stopped naming the API altogether, because the
            // FileManager now downgrades a transactional open itself, and a test that fails when
            // the program it watches gets fixed is a test of the program, not of the scanner.
            string quotedDirectory = TempDirectory();
            try
            {
                // ASCII, because that is what the scanner looks for (a managed literal would be
                // UTF-16 and would never match).
                File.WriteAllText(Path.Combine(quotedDirectory, "quoted.exe"),
                    "no import table here; this image resolves CreateFileTransactedW at run time");
                var quoted = TargetDiscovery.Scan(quotedDirectory);
                if (!quoted.Targets.Any(t => t.ImageName == "quoted.exe" && t.Evidence.Contains("at run time", StringComparison.Ordinal)))
                    throw new Exception("an image that names a transactional API at run time was not recognized: "
                        + string.Join("; ", quoted.Targets.Select(t => t.ImageName + " = " + t.Evidence)));

                // Beside the image: a real loader with one of its imported modules next to it, and
                // that module naming the API. This is the engine's shape without the engine.
                string? cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                if (File.Exists(cmd))
                {
                    string besideDirectory = TempDirectory();
                    try
                    {
                        File.Copy(cmd, Path.Combine(besideDirectory, "loader.exe"));
                        string module = PortableExecutable.Read(cmd).Modules.First();
                        File.WriteAllText(Path.Combine(besideDirectory, module),
                            "no import table here; this module resolves CreateFileTransactedW at run time");
                        var beside = TargetDiscovery.Scan(besideDirectory);
                        if (!beside.Targets.Any(t =>
                                t.ImageName == "loader.exe" &&
                                t.Evidence.Contains("loads " + module, StringComparison.OrdinalIgnoreCase) &&
                                t.Evidence.Contains("at run time", StringComparison.Ordinal)))
                            throw new Exception("an image whose beside-DLL names a transactional API was not recognized: "
                                + string.Join("; ", beside.Targets.Select(t => t.ImageName + " = " + t.Evidence)));
                        if (beside.Targets.Any(t => !t.ImageName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                            throw new Exception("something that is not an executable became a target");
                    }
                    finally { Directory.Delete(besideDirectory, recursive: true); }
                }
            }
            finally { Directory.Delete(quotedDirectory, recursive: true); }
        });

        Check("install-one-image", SingleImageInstall);
        Check("notices", Notices);

        Check("install-refuses-without-payload", () =>
        {
            string temp = TempDirectory();
            try
            {
                var registry = new FakeImageFileExecutionOptions();
                // The checkout this runs from has a native\ folder; point the service somewhere
                // empty so "the payload is not deployed" is the state under test.
                var service = new TxFShimService(temp, registry, ScanOf(OneTarget()),
                    payloadSource: Path.Combine(temp, "no-native-here"));
                bool refused = false;
                try { service.Install(@"D:\engine"); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("native shim is not deployed", StringComparison.Ordinal))
                { refused = true; }
                if (!refused) throw new Exception("install proceeded with nothing for the Debugger value to point at");
                if (registry.Writes != 0) throw new Exception("a refused install still wrote to the registry");
            }
            finally { Directory.Delete(temp, recursive: true); }
        });

        Check("install-replaces-a-changed-payload", () =>
        {
            string temp = TempDirectory();
            string payload = TempDirectory();
            try
            {
                File.WriteAllBytes(Path.Combine(payload, "PolyMount.TxFShim.dll"), new byte[] { 1 });
                File.WriteAllBytes(Path.Combine(payload, "PolyMount.TxFShim.exe"), new byte[] { 1 });
                var registry = new FakeImageFileExecutionOptions();
                var service = new TxFShimService(temp, registry, ScanOf(OneTarget()), payloadSource: payload);
                service.Install(@"D:\engine");

                // The plugin ships a new launcher. Installing again is what carries it to a machine
                // that already has the old one, and every instrumented program runs this file: a
                // deploy that only filled in a missing copy would fix nothing that is already there.
                File.WriteAllBytes(Path.Combine(payload, "PolyMount.TxFShim.exe"), new byte[] { 2, 2 });
                service.Install(@"D:\engine");
                if (File.ReadAllBytes(service.PayloadLauncher).Length != 2)
                    throw new Exception("a changed launcher was left in place by the next install");

                var unchanged = File.GetLastWriteTimeUtc(service.PayloadDll);
                service.Install(@"D:\engine");
                if (File.GetLastWriteTimeUtc(service.PayloadDll) != unchanged)
                    throw new Exception("a payload that had not changed was rewritten");
            }
            finally
            {
                Directory.Delete(temp, recursive: true);
                Directory.Delete(payload, recursive: true);
            }
        });

        Check("install-keeps-an-installed-payload-when-the-plugin-ships-none", () =>
        {
            // A build without the C++ toolchain publishes the plugin without its native half, and
            // a clone of the repository is exactly that: no native\ folder. An installation that
            // already has the payload keeps working, which is the state the plugin tests run in.
            string temp = TempDirectory();
            try
            {
                DeployPayload(temp);
                var registry = new FakeImageFileExecutionOptions();
                var service = new TxFShimService(temp, registry, ScanOf(OneTarget()),
                    payloadSource: Path.Combine(temp, "no-native-here"));
                var status = service.Install(@"D:\engine");
                if (!status.PayloadPresent) throw new Exception("an installed payload was reported missing");
                if (status.Installed.Count != 1) throw new Exception("the install wrote nothing for the image");
            }
            finally { Directory.Delete(temp, recursive: true); }
        });

        Check("install-and-remove", () =>
        {
            string temp = TempDirectory();
            try
            {
                DeployPayload(temp);
                var registry = new FakeImageFileExecutionOptions();
                var service = new TxFShimService(temp, registry, ScanOf(OneTarget()));
                var status = service.Install(@"D:\engine");

                if (registry.Launcher("Editor.exe") is not { } written || !written.EndsWith("PolyMount.TxFShim.exe", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("the Debugger value was not written");
                if (status.Installed.Count != 1) throw new Exception("the record did not gain the image");
                if (status.Drift.Count != 0) throw new Exception("a fresh install reported drift: " + status.Drift[0].Detail);
                if (!File.Exists(service.StatePath)) throw new Exception("the record was not written");

                var removed = service.Remove(@"D:\engine", null);
                if (registry.Launcher("Editor.exe") != null) throw new Exception("the Debugger value survived removal");
                if (removed.Installed.Count != 0) throw new Exception("the record still lists the image");
            }
            finally { Directory.Delete(temp, recursive: true); }
        });

        Check("panel", () =>
        {
            string temp = TempDirectory();
            try
            {
                DeployPayload(temp);
                var registry = new FakeImageFileExecutionOptions();
                var service = new TxFShimService(temp, registry, ScanOf(OneTarget()));
                service.Install(@"D:\engine");

                var view = service.Panel();
                if (view.Rows.Count != 1) throw new Exception("the page did not list the instrumented image");
                var row = view.Rows[0];
                if (row.Cells.Count != 3) throw new Exception("a row does not fill the three columns it is given");
                if (row.Cells[0] != "Editor.exe") throw new Exception("the row does not name the program: " + row.Cells[0]);
                if (row.Tooltip == null) throw new Exception("the row does not say why that program is here");
                var remove = row.Actions.FirstOrDefault();
                if (remove == null) throw new Exception("an instrumented image cannot be taken out from the page");
                if (remove.Request.Cmd != TxFShimPlugin.Command || remove.Request.Action != "remove")
                    throw new Exception("the row's button does not ask the plugin to remove that image");
                if (remove.Request.Path != row.Cells[1])
                    throw new Exception("the button names a different image than the row shows");
                if (remove.Confirm == null)
                    throw new Exception("removing a shim a running program keeps until restart is asked without a word");

                var install = view.Actions.FirstOrDefault(action => action.Id == "install");
                if (install == null) throw new Exception("the page cannot put a folder's programs under the shim");
                if (install.Input != PluginPanelInput.Directory)
                    throw new Exception("adding a folder does not ask the window for one");
                if (install.Request.Action != "install") throw new Exception("the folder is asked for and not used");
                if (view.Facts.Count == 0) throw new Exception("the page says nothing about the payload it depends on");

                service.Remove(@"D:\engine", null);
                var empty = service.Panel();
                if (empty.Rows.Count != 0) throw new Exception("a removed image is still listed");
                if (empty.Empty == null) throw new Exception("an empty page does not say what to do");
                if (empty.Actions.All(action => action.Id != "install")) throw new Exception("an empty page cannot add anything");
            }
            finally { Directory.Delete(temp, recursive: true); }
        });

        Check("install-refuses-a-foreign-entry", () =>
        {
            string temp = TempDirectory();
            try
            {
                DeployPayload(temp);
                var registry = new FakeImageFileExecutionOptions();
                // Another tool already starts this image through its own launcher. Install must
                // refuse the whole set: half a table is harder to explain than a refusal.
                registry.Seed("Other.exe", @"C:\OtherTool\shim.exe");
                var service = new TxFShimService(temp, registry, ScanOf(TwoTargets()));
                bool refused = false;
                try { service.Install(@"D:\engine"); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("Other.exe", StringComparison.Ordinal))
                { refused = true; }
                if (!refused) throw new Exception("a foreign Debugger value was replaced");
                if (registry.Writes != 0) throw new Exception("the first image was written before the refusal");
                if (registry.Launcher("Other.exe") != @"C:\OtherTool\shim.exe") throw new Exception("the foreign value changed");
                if (File.Exists(service.StatePath)) throw new Exception("a refused install left a record behind");
            }
            finally { Directory.Delete(temp, recursive: true); }
        });

        Check("drift", () =>
        {
            string temp = TempDirectory();
            try
            {
                DeployPayload(temp);
                var registry = new FakeImageFileExecutionOptions();
                var service = new TxFShimService(temp, registry, ScanOf(OneTarget()));
                service.Install(@"D:\engine");

                registry.Forget("Editor.exe");
                var missing = service.Status().Drift.SingleOrDefault(d => d.Image == "Editor.exe");
                if (missing is not { Kind: "missing" }) throw new Exception("a key that disappeared was not reported");

                registry.Seed("Editor.exe", @"C:\Elsewhere\shim.exe");
                var changed = service.Status().Drift.SingleOrDefault(d => d.Image == "Editor.exe");
                if (changed is not { Kind: "changed" }) throw new Exception("a key that points elsewhere was not reported");

                registry.Seed("Stray.exe", service.PayloadLauncher);
                var unrecorded = service.Status().Drift.SingleOrDefault(d => d.Image == "Stray.exe");
                if (unrecorded is not { Kind: "unrecorded" }) throw new Exception("a key of ours with no record was not reported");
            }
            finally { Directory.Delete(temp, recursive: true); }
        });

        Check("remove-leaves-a-foreign-value", () =>
        {
            string temp = TempDirectory();
            try
            {
                DeployPayload(temp);
                var registry = new FakeImageFileExecutionOptions();
                var service = new TxFShimService(temp, registry, ScanOf(OneTarget()));
                service.Install(@"D:\engine");

                // Something else took the key over while it was installed. Removal must not delete
                // what is no longer ours, and must still drop the record.
                registry.Seed("Editor.exe", @"C:\Elsewhere\shim.exe");
                var status = service.Remove(null, "Editor.exe");
                if (registry.Launcher("Editor.exe") != @"C:\Elsewhere\shim.exe")
                    throw new Exception("removal deleted a value that no longer pointed at this plugin");
                if (status.Installed.Count != 0) throw new Exception("the record still lists the image");
            }
            finally { Directory.Delete(temp, recursive: true); }

            // The pipe's Name field is the instance, so an image arrives in Path. A path ending in
            // .exe has to mean the image, not a directory.
            string second = TempDirectory();
            try
            {
                DeployPayload(second);
                var registry = new FakeImageFileExecutionOptions();
                var service = new TxFShimService(second, registry, ScanOf(OneTarget()));
                service.Install(@"D:\engine");
                var byPath = service.Remove(@"D:\engine\Editor.exe", null);
                if (registry.Launcher("Editor.exe") != null) throw new Exception("a removal by image path left the key");
                if (byPath.Installed.Count != 0) throw new Exception("a removal by image path left the record behind");
            }
            finally { Directory.Delete(second, recursive: true); }
        });

        Check("log-tail", () =>
        {
            string temp = TempDirectory();
            try
            {
                var service = new TxFShimService(temp, new FakeImageFileExecutionOptions());
                if (service.Log(10) is { Exists: true }) throw new Exception("a missing log was reported as present");

                File.WriteAllLines(service.LogPath, Enumerable.Range(0, 300).Select(i => $"hit {i}"));
                var tail = service.Log(5);
                if (!tail.Exists) throw new Exception("the log was not found");
                if (tail.Lines.Count != 5) throw new Exception($"asked for 5 lines, got {tail.Lines.Count}");
                if (tail.Lines[^1] != "hit 299") throw new Exception("the tail did not end at the last line");
                if (tail.Lines[0] != "hit 295") throw new Exception("the tail started somewhere else");
            }
            finally { Directory.Delete(temp, recursive: true); }
        });

        return Task.FromResult<IReadOnlyList<PluginTestResult>>(results);
    }

    /// <summary>The one-image install is what a reminder about a running program uses: the path it
    /// knows is the running image's, and its folder's other executables were not asked about.</summary>
    private static void SingleImageInstall()
    {
        string temp = TempDirectory();
        try
        {
            var registry = new FakeImageFileExecutionOptions();
            var editor = new ShimTarget(@"D:\engine\Editor.exe", "Editor.exe", "loads Messiah_x64h.dll, which resolves CreateFileTransactedW at run time");
            var other = new ShimTarget(@"D:\engine\Other.exe", "Other.exe", "imports CreateFileTransactedW");
            var service = new TxFShimService(temp, registry, ScanOf(new[] { editor, other }),
                inspect: InspectOf(editor, other));
            DeployPayload(temp);

            var status = service.Install(editor.ImagePath);
            if (status.Installed.Count != 1)
                throw new Exception($"installing one image took {status.Installed.Count}: a program's siblings were not asked about");
            if (status.Installed[0].ImageName != "Editor.exe") throw new Exception("the wrong image was recorded: " + status.Installed[0].ImageName);
            if (status.Installed[0].Root != @"D:\engine") throw new Exception("the record does not name the folder it came from: " + status.Installed[0].Root);
            if (registry.Launcher("Other.exe") != null) throw new Exception("installing one image instrumented another");

            // An image that reaches nothing is refused rather than recorded as instrumented.
            bool refused = false;
            try { service.Install(@"D:\engine\Plain.exe"); }
            catch (InvalidOperationException) { refused = true; }
            if (!refused) throw new Exception("an image with no transactional API was installed anyway");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    /// <summary>What a front end is told: a running program that would fail on the mount, with the
    /// command that fixes it — and, once it has been fixed, that this particular process predates
    /// the fix and has to be restarted.</summary>
    private static void Notices()
    {
        string temp = TempDirectory();
        try
        {
            var registry = new FakeImageFileExecutionOptions();
            // The image has to exist: what its bytes say is injected here, but whether there is a
            // file to look at is read from the disk, the way the real answer is.
            string image = Path.Combine(temp, "Editor.exe");
            File.WriteAllText(image, "not an image");
            var editor = new ShimTarget(image, "Editor.exe", "resolves CreateFileTransactedW at run time");
            var service = new TxFShimService(temp, registry, ScanOf(new[] { editor }), inspect: InspectOf(editor));
            DeployPayload(temp);

            var status = new MountStatus
            {
                Name = "demo",
                Mounted = true,
                MountPoint = @"D:\demo",
                Callers = { new MountCaller { Pid = 4242, Name = "Editor", ImagePath = editor.ImagePath, SecondsAgo = 3 } }
            };

            var notices = service.Notices(status);
            if (notices.Count != 1) throw new Exception($"expected one notice, got {notices.Count}");
            if (!notices[0].Id.Contains("Editor.exe", StringComparison.OrdinalIgnoreCase))
                throw new Exception("the notice does not name the program it is about: " + notices[0].Id);
            if (notices[0].Action is not { Cmd: "txf.shim", Action: "install" })
                throw new Exception("the notice does not carry the command that would fix it");
            if (notices[0].Action!.Path != editor.ImagePath)
                throw new Exception("the action names a different image: " + notices[0].Action!.Path);

            // Instrumented, but the process was already running when that happened: the shim is
            // injected at start-up, so this one still fails and has to be told to restart.
            service.Install(editor.ImagePath);
            status.Callers[0].StartedUtc = DateTime.UtcNow.AddMinutes(-5).ToString("O");
            notices = service.Notices(status);
            if (notices.Count != 1) throw new Exception($"expected a restart notice, got {notices.Count}");
            if (notices[0].Action != null) throw new Exception("a restart notice should not offer an action");

            // Started after the install: nothing to say.
            status.Callers[0].StartedUtc = DateTime.UtcNow.AddMinutes(5).ToString("O");
            if (service.Notices(status).Count != 0) throw new Exception("an instrumented, freshly started program was still reported");

            // A mount that is not running has no callers to advise about.
            status.Mounted = false;
            if (service.Notices(status).Count != 0) throw new Exception("a mount that is not mounted produced a notice");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static Func<string, TargetDiscovery.Result> ScanOf(IReadOnlyList<ShimTarget> targets) => _ => new TargetDiscovery.Result(targets, Array.Empty<string>());

    /// <summary>An inspection that answers for the paths a test names, and for nothing else —
    /// which is what a real one does for an image that reaches no transactional API.</summary>
    private static Func<string, ShimTarget?> InspectOf(params ShimTarget[] targets) => image =>
        targets.FirstOrDefault(target => string.Equals(target.ImagePath, image, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<ShimTarget> OneTarget() => new[]
    {
        new ShimTarget(@"D:\engine\Editor.exe", "Editor.exe", "loads Messiah_x64h.dll, which imports CreateFileTransactedW")
    };

    private static IReadOnlyList<ShimTarget> TwoTargets() => new[]
    {
        new ShimTarget(@"D:\engine\Editor.exe", "Editor.exe", "loads Messiah_x64h.dll, which imports CreateFileTransactedW"),
        new ShimTarget(@"D:\engine\Other.exe", "Other.exe", "imports CreateFileTransactedW")
    };

    /// <summary>The two files the launcher value may point at. Empty files are enough: what is
    /// checked is that they exist before anything is written.</summary>
    private static void DeployPayload(string directory)
    {
        File.WriteAllBytes(Path.Combine(directory, "PolyMount.TxFShim.dll"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(directory, "PolyMount.TxFShim.exe"), Array.Empty<byte>());
    }

    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "polymount-txfshim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
