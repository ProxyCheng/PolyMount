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
            var errors = PluginLoader.LoadConfigured(new[] { new PluginConfig { AssemblyPath = path } }, AppContext.BaseDirectory);
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

            PluginLoader.LoadConfigured(Array.Empty<PluginConfig>(), AppContext.BaseDirectory);
            if (ControlOperations.Find("get_txf_shim") != null)
                throw new Exception("A disabled plugin's operation remains registered");
            PluginLoader.LoadConfigured(new[] { new PluginConfig { AssemblyPath = path } }, AppContext.BaseDirectory);
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

            // The engine this was written for, when this machine has it: its launchers import one
            // runtime DLL and that DLL resolves the APIs at run time, so the evidence has to be
            // the quoted name rather than an import.
            const string engine = @"D:\demo\messiah\Engine\Binaries\Win64";
            if (!Directory.Exists(engine)) throw new Skip($"no engine binaries at '{engine}'");
            var scan = TargetDiscovery.Scan(engine);
            if (scan.Targets.Count == 0) throw new Exception("no engine image was recognized");
            if (!scan.Targets.Any(t =>
                    t.Evidence.Contains("Messiah_x64h.dll", StringComparison.OrdinalIgnoreCase) &&
                    t.Evidence.Contains("at run time", StringComparison.Ordinal)))
                throw new Exception("the engine's runtime DLL was not recognized through its quoted API names: "
                    + string.Join("; ", scan.Targets.Select(t => t.ImageName + " = " + t.Evidence)));
            if (scan.Targets.Any(t => !t.ImageName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                throw new Exception("something that is not an executable became a target");
        });

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

    private static Func<string, TargetDiscovery.Result> ScanOf(IReadOnlyList<ShimTarget> targets) => _ => new TargetDiscovery.Result(targets, Array.Empty<string>());

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
