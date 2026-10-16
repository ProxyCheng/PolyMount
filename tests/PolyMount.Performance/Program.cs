using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PolyMount.Core;
using PolyMount.Layers;

string Option(string name, string fallback) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault() ?? fallback;
if (args.Contains("--metrics-cost"))
{
    // What recording a read costs the thread doing it, and what answering a window costs the
    // process. This is the whole point of the collector's shape: a write is a slot calculation and
    // an atomic add, and a window is a walk over the cells it covers rather than over the traffic.
    var collector = new MetricsCollector();
    var elapsed = TimeSpan.FromMicroseconds(120);
    const int count = 2_000_000;
    for (int i = 0; i < 200_000; i++) collector.RecordRead("cache", elapsed, 4096, true);
    long before = GC.GetAllocatedBytesForCurrentThread();
    var watch = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < count; i++) collector.RecordRead("cache", elapsed, 4096, cached: true);
    watch.Stop();
    long allocatedIdle = GC.GetAllocatedBytesForCurrentThread() - before;
    double recordIdle = watch.Elapsed.TotalNanoseconds / count;
    Console.WriteLine($"record, nobody watching : {recordIdle:0.0} ns/call  ({count:N0} calls, one thread)");
    collector.Watch();
    for (int i = 0; i < 200_000; i++) collector.RecordRead("cache", elapsed, 4096, true);
    before = GC.GetAllocatedBytesForCurrentThread();
    watch.Restart();
    for (int i = 0; i < count; i++) collector.RecordRead("cache", elapsed, 4096, cached: true);
    watch.Stop();
    long allocatedWatching = GC.GetAllocatedBytesForCurrentThread() - before;
    double recordWatching = watch.Elapsed.TotalNanoseconds / count;
    Console.WriteLine($"record, watching        : {recordWatching:0.0} ns/call  ({count:N0} calls, one thread)");
    foreach (int threads in new[] { 1, 4, 8 })
    {
        var per = count / threads;
        var start = new ManualResetEventSlim();
        var threadsRun = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            start.Wait();
            for (int i = 0; i < per; i++) collector.RecordRead("cache", elapsed, 4096, cached: true);
        })).ToList();
        foreach (var thread in threadsRun) thread.Start();
        var concurrent = System.Diagnostics.Stopwatch.StartNew();
        start.Set();
        foreach (var thread in threadsRun) thread.Join();
        concurrent.Stop();
        Console.WriteLine($"record x{threads,-2}    : {concurrent.Elapsed.TotalNanoseconds / (per * threads):0.0} ns/call  (aggregate, watching)");
    }
    // What one request costs on top of the layer it was served by: every request is counted once,
    // whatever answered it, so that the time in requests no layer answered for is visible at all.
    // This is the number that decides whether that is worth having while a recording runs.
    double requestIdle = 0, requestWatching = 0;
    foreach (bool watching in new[] { false, true })
    {
        if (watching) collector.Watch();
        else collector = new MetricsCollector();
        for (int i = 0; i < 200_000; i++) collector.RecordRequest(elapsed);
        watch.Restart();
        for (int i = 0; i < count; i++) collector.RecordRequest(elapsed);
        watch.Stop();
        double per = watch.Elapsed.TotalNanoseconds / count;
        if (watching) requestWatching = per; else requestIdle = per;
        Console.WriteLine($"request, {(watching ? "watching" : "nobody watching"),-15}: {per:0.0} ns/call");
    }
    // Who asked. Two shapes, because the second one is why the per-thread shard remembers the last
    // process it saw: one reader on a thread is a comparison, and a thread serving several pays a
    // short scan. The id is already in hand at every call site, so what is measured is the counter.
    double callerIdle = 0, callerOne = 0, callerMany = 0, allocatedCallers = 0;
    {
        collector = new MetricsCollector();
        for (int i = 0; i < 200_000; i++) collector.RecordProcessAccess(4321, false, 4096);
        watch.Restart();
        for (int i = 0; i < count; i++) collector.RecordProcessAccess(4321, false, 4096);
        watch.Stop();
        callerIdle = watch.Elapsed.TotalNanoseconds / count;
        collector.Watch();
        for (int i = 0; i < 200_000; i++) collector.RecordProcessAccess(4321, false, 4096);
        watch.Restart();
        for (int i = 0; i < count; i++) collector.RecordProcessAccess(4321, false, 4096);
        watch.Stop();
        callerOne = watch.Elapsed.TotalNanoseconds / count;
        int[] pids = { 100, 200, 300, 400, 500, 600, 700, 800 };
        for (int i = 0; i < 200_000; i++) collector.RecordProcessAccess(pids[i & 7], false, 4096);
        before = GC.GetAllocatedBytesForCurrentThread();
        watch.Restart();
        for (int i = 0; i < count; i++) collector.RecordProcessAccess(pids[i & 7], false, 4096);
        watch.Stop();
        allocatedCallers = GC.GetAllocatedBytesForCurrentThread() - before;
        callerMany = watch.Elapsed.TotalNanoseconds / count;
        Console.WriteLine($"caller, nobody watching : {callerIdle:0.0} ns/call  ({count:N0} calls, one thread)");
        Console.WriteLine($"caller, watching, 1 pid : {callerOne:0.0} ns/call  ({count:N0} calls, one thread)");
        Console.WriteLine($"caller, watching, 8 pids: {callerMany:0.0} ns/call  ({count:N0} calls, one thread)");
    }
    if (args.Contains("--assert"))
    {
        // A gate, not a benchmark: the budgets are loose enough for a busy machine and tight
        // enough to catch the shapes this repository has already paid for once - a record that
        // allocates, a record that grows with the threads counting beside it, a scope that grows
        // back towards the 345 bytes it was. Run it after touching the collector.
        var failures = new List<string>();
        void Within(string what, double value, double budget, string unit = "ns/call")
        {
            if (value > budget) failures.Add($"{what}: {value:0.0} {unit} > {budget:0.0}");
        }
        Within("record, nobody watching", recordIdle, 40);
        Within("record, watching", recordWatching, 150);
        Within("request, nobody watching", requestIdle, 40);
        Within("request, watching", requestWatching, 120);
        // Per call, not in total: a few tens of bytes once in two million calls is the JIT
        // recompiling the loop, while an allocation on the path is megabytes by then.
        if (allocatedIdle > count / 1000) failures.Add($"record, nobody watching allocated {allocatedIdle} bytes over {count:N0} calls");
        if (allocatedWatching > count / 1000) failures.Add($"record, watching allocated {allocatedWatching} bytes over {count:N0} calls");
        // What one request costs to attribute to its caller. The budgets are what this dimension
        // may add to a request: the id is already in hand where it is recorded, so the counter is
        // the whole of it. The allocation is one object per (thread, process, second) rather than
        // one per call, which is what the allowance here says.
        Within("caller, nobody watching", callerIdle, 40);
        Within("caller, one pid", callerOne, 60);
        Within("caller, eight pids", callerMany, 90);
        if (allocatedCallers > count / 1000) failures.Add($"caller record allocated {allocatedCallers} bytes over {count:N0} calls");
        if (failures.Count == 0)
            Console.WriteLine($"assert: within budget (record {recordIdle:0.#}/{recordWatching:0.#} ns, request {requestIdle:0.#}/{requestWatching:0.#} ns, no allocation)");
        else
        {
            foreach (var failure in failures) Console.Error.WriteLine("assert failed - " + failure);
            return 1;
        }
    }
    collector.Watch();
    for (int i = 0; i < 200_000; i++) { collector.RecordRequest(elapsed); collector.RecordRead("cache", elapsed, 0, true); }
    watch.Restart();
    for (int i = 0; i < count; i++) { collector.RecordRequest(elapsed); collector.RecordRead("cache", elapsed, 0, true); }
    watch.Stop();
    Console.WriteLine($"lookup (layer + request), watching: {watch.Elapsed.TotalNanoseconds / count:0.0} ns/call");
    foreach (int threads in new[] { 4, 8 })
    {
        var per = count / threads;
        var start = new ManualResetEventSlim();
        var running = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            start.Wait();
            for (int i = 0; i < per; i++) collector.RecordRequest(elapsed);
        })).ToList();
        foreach (var thread in running) thread.Start();
        var concurrent = System.Diagnostics.Stopwatch.StartNew();
        start.Set();
        foreach (var thread in running) thread.Join();
        concurrent.Stop();
        Console.WriteLine($"request x{threads,-2}    : {concurrent.Elapsed.TotalNanoseconds / (per * threads):0.0} ns/call  (aggregate, watching)");
    }
    foreach (double window in new[] { 0.1, 1.0, 60.0 })
    {
        const int snapshots = 2000;
        var begin = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < snapshots; i++) collector.Snapshot("m", window);
        begin.Stop();
        Console.WriteLine($"snapshot {window,5:0.#}s : {begin.Elapsed.TotalMicroseconds / snapshots:0.0} us/call");
    }
    var measured = collector.Snapshot("m", 60);
    Console.WriteLine($"window says  : asked 60s, measured {measured.MeasuredSeconds:0.##}s, {measured.TotalReads:N0} reads over {measured.Layers.Count} layer(s), {measured.RequestCount:N0} requests / {measured.RequestMs:0.0} ms");
    return 0;
}
if (args.Contains("--diagnostics-cost"))
{
    // What the per-request diagnostics hook costs the thread serving the request, with and without
    // a recording attached. Every request builds a scope whether or not a line will be written
    // about it — the sampling rule decides what is logged, not what is measured — and a recording
    // additionally stops the layer probes being dropped, so this is the instrumentation that runs
    // on the request threads whatever else is switched off.
    string benchRoot = Path.Combine(Path.GetTempPath(), "polymount-diagnostics-bench");
    Directory.CreateDirectory(benchRoot);
    const int count = 1_000_000;
    const string path = "Repository/11_3606.local/resource.repository/data/asset.pkg";

    foreach (bool recording in new[] { false, true })
    {
        using var diagnostics = new RequestDiagnostics(Path.Combine(benchRoot, "requests.log"))
        {
            Channel = new RecordingChannel(Path.Combine(benchRoot, "recording"))
        };
        if (recording) diagnostics.Channel.Start("bench", "bench");
        // Warm up, so the measurement is of the steady state rather than of first-call paths.
        for (int i = 0; i < 100_000; i++) { using var warm = RequestDiagnostics.Begin("WinFsp.Read", path, diagnostics); }

        long before = GC.GetAllocatedBytesForCurrentThread();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            using var scope = RequestDiagnostics.Begin("WinFsp.Read", path, diagnostics);
            scope?.Stage("reading");
        }
        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"request scope, {(recording ? "recording" : "idle     "),-10}: {watch.Elapsed.TotalNanoseconds / count:0.0} ns/call, {allocated / (double)count:0.0} B/call allocated");

        // A layer probe: dropped outright unless a recording is attached, and the thing that
        // dominates a cached directory walk when one is.
        for (int i = 0; i < 100_000; i++) { using var outer = RequestDiagnostics.Begin("VFS.List", path, diagnostics); using var probe = RequestDiagnostics.Begin("Layer.GetEntry:cache", path, diagnostics); }
        before = GC.GetAllocatedBytesForCurrentThread();
        watch.Restart();
        for (int i = 0; i < count; i++)
        {
            using var outer = RequestDiagnostics.Begin("VFS.List", path, diagnostics);
            using var probe = RequestDiagnostics.Begin("Layer.GetEntry:cache", path, diagnostics);
        }
        watch.Stop();
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"list + layer probe, {(recording ? "recording" : "idle     "),-10}: {watch.Elapsed.TotalNanoseconds / count:0.0} ns/call, {allocated / (double)count:0.0} B/call allocated");

        // The probe's own name. A probe is the most frequent operation there is, so how the caller
        // spells its name matters more than anything inside the scope: concatenating it at the call
        // site builds a string for every probe, including the ones the rule drops unread, while
        // keeping one string per layer builds none.
        string layer = "memory-cache";
        for (int i = 0; i < 100_000; i++) { using var outer = RequestDiagnostics.Begin("VFS.List", path, diagnostics); using var probe = RequestDiagnostics.Begin("Layer.GetEntry:" + layer, path, diagnostics); }
        before = GC.GetAllocatedBytesForCurrentThread();
        watch.Restart();
        for (int i = 0; i < count; i++)
        {
            using var outer = RequestDiagnostics.Begin("VFS.List", path, diagnostics);
            using var probe = RequestDiagnostics.Begin("Layer.GetEntry:" + layer, path, diagnostics);
        }
        watch.Stop();
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"probe, name built per call: {watch.Elapsed.TotalNanoseconds / count:0.0} ns/call, {allocated / (double)count:0.0} B/call allocated");

        string kept = "Layer.GetEntry:" + layer;
        for (int i = 0; i < 100_000; i++) { using var outer = RequestDiagnostics.Begin("VFS.List", path, diagnostics); using var probe = RequestDiagnostics.Begin(kept, path, diagnostics); }
        before = GC.GetAllocatedBytesForCurrentThread();
        watch.Restart();
        for (int i = 0; i < count; i++)
        {
            using var outer = RequestDiagnostics.Begin("VFS.List", path, diagnostics);
            using var probe = RequestDiagnostics.Begin(kept, path, diagnostics);
        }
        watch.Stop();
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"probe, name kept per layer: {watch.Elapsed.TotalNanoseconds / count:0.0} ns/call, {allocated / (double)count:0.0} B/call allocated");
    }
    // What is left of the scope's cost once nothing is written: the object itself, the stopwatch
    // inside it, the entry in the active map, and the ambient marker. The last one is measured on
    // its own because it is the part that is easy to overlook - an AsyncLocal write copies the
    // execution context, which is an allocation, and a scope does it twice.
    const int markerCount = 1_000_000;
    var ambient = new AsyncLocal<object?>();
    var value = new object();
    for (int i = 0; i < 100_000; i++) { ambient.Value = value; ambient.Value = null; }
    long beforeMarker = GC.GetAllocatedBytesForCurrentThread();
    var markerWatch = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < markerCount; i++) { ambient.Value = value; ambient.Value = null; }
    markerWatch.Stop();
    long markerBytes = GC.GetAllocatedBytesForCurrentThread() - beforeMarker;
    Console.WriteLine($"ambient marker, set+clear: {markerWatch.Elapsed.TotalNanoseconds / markerCount:0.0} ns/call, {markerBytes / (double)markerCount:0.0} B/call allocated");

    var plain = new Stopwatch();
    for (int i = 0; i < 100_000; i++) { plain = Stopwatch.StartNew(); }
    long beforeWatch = GC.GetAllocatedBytesForCurrentThread();
    var stopwatchWatch = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < markerCount; i++) { plain = Stopwatch.StartNew(); }
    stopwatchWatch.Stop();
    long watchBytes = GC.GetAllocatedBytesForCurrentThread() - beforeWatch;
    Console.WriteLine($"a stopwatch per call    : {stopwatchWatch.Elapsed.TotalNanoseconds / markerCount:0.0} ns/call, {watchBytes / (double)markerCount:0.0} B/call allocated");
    GC.KeepAlive(plain);
    try { Directory.Delete(benchRoot, true); } catch (IOException) { }
    return 0;
}
if (args.Contains("--warm-probe"))
{
    WarmPathProbe.Run(Option("--config", ""), Option("--mount-name", "TX4Package"),
        Option("--path", "Repository/11_3606.local/resource.repository"));
    return 0;
}
int repeats = int.Parse(Option("--repeats", "3"), CultureInfo.InvariantCulture);
int fileMb = int.Parse(Option("--file-mb", "8"), CultureInfo.InvariantCulture);
int smallFiles = int.Parse(Option("--small-files", "32"), CultureInfo.InvariantCulture);
int[] workers = Option("--workers", "1,4,8").Split(',').Select(int.Parse).ToArray();
int engineDirectories = int.Parse(Option("--engine-directories", "128"));
int engineFiles = int.Parse(Option("--engine-files", "16"));
int engineRounds = int.Parse(Option("--engine-rounds", "2"));
int remoteDelayMs = int.Parse(Option("--remote-delay-ms", "5"));
bool engineOnly = args.Contains("--engine-only");
// Which collector the process is running under, said out loud: a run's numbers are only
// comparable to another run's when this line matches. DOTNET_gcServer=1 is how the other mode is
// asked for, and a comparison that did not say which one it measured is not a comparison.
Console.WriteLine($"GC: server={System.Runtime.GCSettings.IsServerGC} latencyMode={System.Runtime.GCSettings.LatencyMode} heapHardLimit={(System.AppContext.GetData("GCHeapHardLimit") ?? "none")}");
bool breakdown = args.Contains("--engine-breakdown");
if (engineDirectories <= 0 || engineFiles <= 0 || engineRounds <= 0 || remoteDelayMs < 0) throw new ArgumentException("Invalid engine workload sizes");
if (repeats <= 0 || fileMb <= 0 || smallFiles <= 0 || workers.Any(w => w <= 0)) throw new ArgumentException("All workload sizes must be positive");
string reportDirectory = Path.GetFullPath(Option("--report", "artifacts/performance"));
string root = Path.Combine(Path.GetTempPath(), "PolyMount-perf-" + Guid.NewGuid().ToString("N"));
string? mounted = args.Contains("--mount-root") ? Path.GetFullPath(Option("--mount-root", "")) : null;
Directory.CreateDirectory(root); Directory.CreateDirectory(reportDirectory);
var results = new List<RunResult>();
const int blockSize = 65536;
int blocks = checked(fileMb * 1048576 / blockSize);
var payload = new byte[blockSize]; new Random(42).NextBytes(payload);
using var process = Process.GetCurrentProcess();

void Run(string backend, string scenario, int threads, int repeat, long bytes, int expectedOperations, Action<int, Action<Action>> worker)
{
    var latencies = new ConcurrentBag<double>();
    var errors = new ConcurrentBag<string>();
    var cpu = process.TotalProcessorTime;
    long allocated = GC.GetTotalAllocatedBytes();
    var watch = Stopwatch.StartNew();
    long peakPrivate = process.PrivateMemorySize64;
    int peakHandles = process.HandleCount;
    using var resourceSampler = new Timer(_ =>
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            Interlocked.Exchange(ref peakPrivate, Math.Max(Interlocked.Read(ref peakPrivate), current.PrivateMemorySize64));
            Interlocked.Exchange(ref peakHandles, Math.Max(Volatile.Read(ref peakHandles), current.HandleCount));
        }
        catch { }
    }, null, 0, 50);
    int active = 0, peak = 0;
    void ExecuteWorker(int index)
    {
        try
        {
            worker(index, action =>
            {
                long start = Stopwatch.GetTimestamp();
                int concurrent = Interlocked.Increment(ref active);
                int old;
                do { old = Volatile.Read(ref peak); } while (concurrent > old && Interlocked.CompareExchange(ref peak, concurrent, old) != old);
                try { action(); }
                finally { Interlocked.Decrement(ref active); latencies.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
            });
        }
        catch (Exception ex) { errors.Add(ex.GetType().Name + ": " + ex.Message); }
    }
    if (scenario.StartsWith("Engine", StringComparison.Ordinal))
    {
        using var ready = new CountdownEvent(threads);
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, threads).Select(index => Task.Factory.StartNew(() =>
        {
            ready.Signal(); start.Wait(); ExecuteWorker(index);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        ready.Wait();
        watch.Restart();
        start.Set();
        Task.WaitAll(tasks);
    }
    else Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, ExecuteWorker);
    watch.Stop();
    var samples = latencies.Order().ToArray();
    double Percentile(double q) => samples.Length == 0 ? 0 : samples[Math.Clamp((int)Math.Ceiling(q * samples.Length) - 1, 0, samples.Length - 1)];
    var result = new RunResult(backend, scenario, threads, repeat, bytes, expectedOperations, samples.Length,
        watch.Elapsed.TotalSeconds, errors.Count == 0 ? bytes / 1048576.0 / watch.Elapsed.TotalSeconds : 0,
        samples.Length / watch.Elapsed.TotalSeconds, samples.Length == 0 ? 0 : samples.Average(), Percentile(.50), Percentile(.95), Percentile(.99),
        samples.Length == 0 ? 0 : samples[^1], (process.TotalProcessorTime - cpu).TotalMilliseconds, GC.GetTotalAllocatedBytes() - allocated,
        process.PeakWorkingSet64, errors.ToArray());
    result = result with { PeakConcurrentOperations = peak, SlowOperations = samples.Count(ms => ms >= 1000),
        PeakPrivateBytes = peakPrivate, PeakHandles = peakHandles };
    results.Add(result);
    Console.WriteLine($"{backend,-10} {scenario,-18} threads={threads} run={repeat} {result.MBps:0.0} MB/s {result.OpsPerSecond:0.0} ops/s p95={result.P95Ms:0.000}ms errors={errors.Count}");
    Console.WriteLine($"  resources: peakPrivate={result.PeakPrivateBytes / 1048576.0:0.0}MiB peakHandles={result.PeakHandles}");
}

try
{
    foreach (int threads in workers)
    for (int repeat = 1; repeat <= repeats; repeat++)
    foreach (string backend in new[] { "Native", "ObjectVFS", "SimulatedRemote" })
    {
        string corpus = Path.Combine(root, "engine-" + Guid.NewGuid().ToString("N"));
        using var engine = new EngineWorkload(corpus, backend, engineDirectories, engineFiles, remoteDelayMs);
        long bytes = (long)engineDirectories * engineFiles * engineRounds * 8192;
        int operations = checked(engineDirectories * engineRounds * (1 + engineFiles * 2));
        Run(backend, "EngineFirstPass", threads, repeat, bytes, operations,
            (index, measure) => engine.Load(index, threads, engineRounds, measure));
        if (breakdown) engine.PrintBreakdown("FirstPass");
        engine.WaitForPersistence();
        Run(backend, "EngineWarmPass", threads, repeat, bytes, operations,
            (index, measure) => engine.Load(index, threads, engineRounds, measure));
        if (breakdown)
        {
            engine.PrintBreakdown("WarmPass");
            engine.PrewarmFully();
            Run(backend, "EngineFullyWarm", threads, repeat, bytes, operations,
                (index, measure) => engine.Load(index, threads, engineRounds, measure));
            engine.PrintBreakdown("FullyWarm");
            engine.WaitForPersistence();
            engine.MetadataMicrobench(threads);
            Run(backend, "EngineHandleWarm", threads, repeat, bytes, operations,
                (index, measure) => engine.Load(index, threads, engineRounds, measure));
            engine.PrintBreakdown("HandleWarm");
        }
    }
    if (!engineOnly)
    foreach (int threads in workers)
    for (int repeat = 1; repeat <= repeats; repeat++)
    foreach (string backend in mounted == null ? new[] { "Native", "ObjectVFS" } : new[] { "Native", "ObjectVFS", "WinFsp" })
    {
        string directory = Path.Combine(backend == "WinFsp" ? mounted! : root, "PolyMount-stress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        VirtualFileSystem? vfs = backend == "ObjectVFS"
            ? new(new[] { new OverlayLayer(Path.Combine(directory, "objects")) }) : null;
        Stream Write(string path, bool truncate) => vfs != null ? vfs.OpenWrite(path, truncate, out _)
            : new FileStream(Path.Combine(directory, path), truncate ? FileMode.Create : FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        Stream Read(string path) => vfs != null ? vfs.OpenRead(path, out _) : new FileStream(Path.Combine(directory, path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        void Flush(Stream stream) { if (stream is FileStream file) file.Flush(true); else stream.Flush(); }
        long bytes = (long)threads * blocks * blockSize;
        try
        {
            Run(backend, "SequentialWrite", threads, repeat, bytes, threads * (blocks + 2), (index, measure) =>
            {
                Stream? file = null;
                try { measure(() => file = Write(index + ".data", true)); for (int i = 0; i < blocks; i++) measure(() => file!.Write(payload)); measure(() => Flush(file!)); }
                finally { file?.Dispose(); }
            });
            Run(backend, "WarmSequentialRead", threads, repeat, bytes, threads * (blocks + 1), (index, measure) =>
            {
                Stream? file = null; var buffer = new byte[blockSize];
                try { measure(() => file = Read(index + ".data")); for (int i = 0; i < blocks; i++) measure(() => file!.ReadExactly(buffer)); }
                finally { file?.Dispose(); }
                if (!buffer.SequenceEqual(payload)) throw new InvalidDataException("Sequential read mismatch");
            });
            Run(backend, "RandomRead4K", threads, repeat, (long)threads * blocks * 4096, threads * blocks, (index, measure) =>
            {
                using var file = Read(index + ".data"); var buffer = new byte[4096]; var random = new Random(index);
                for (int i = 0; i < blocks; i++) { long offset = random.Next(blocks * 16) * 4096L; measure(() => { file.Position = offset; file.ReadExactly(buffer); }); }
            });
            Run(backend, "PartialWriteAndFlush", threads, repeat, (long)threads * 4096, threads, (index, measure) =>
            {
                measure(() =>
                {
                    using var file = Write(index + ".data", false);
                    file.Position = (long)blocks * blockSize / 2;
                    file.Write(payload.AsSpan(0, 4096));
                    Flush(file);
                });
            });
            Run(backend, "SmallFileLifecycle", threads, repeat, (long)threads * smallFiles * 4096, threads * smallFiles, (index, measure) =>
            {
                for (int i = 0; i < smallFiles; i++)
                {
                    string path = $"small-{index}-{i}";
                    measure(() =>
                    {
                        using (var file = Write(path, true)) { file.Write(payload.AsSpan(0, 4096)); Flush(file); }
                        using (var file = Read(path)) { var buffer = new byte[4096]; file.ReadExactly(buffer); if (!buffer.AsSpan().SequenceEqual(payload.AsSpan(0, 4096))) throw new InvalidDataException("Small file mismatch"); }
                        if (vfs != null) vfs.Delete(path); else File.Delete(Path.Combine(directory, path));
                    });
                }
            });
            using (var file = Write("shared.data", true)) { file.SetLength((long)threads * blocks * blockSize); Flush(file); }
            Run(backend, "SharedFileWrite", threads, repeat, bytes, threads * (blocks + 1), (index, measure) =>
            {
                using var file = Write("shared.data", false); file.Position = (long)index * blocks * blockSize;
                byte[] segment = Enumerable.Repeat((byte)(index + 1), blockSize).ToArray();
                for (int i = 0; i < blocks; i++) measure(() => file.Write(segment));
                measure(() => Flush(file));
            });
            using (var file = Read("shared.data"))
            {
                var buffer = new byte[blockSize];
                for (int i = 0; i < threads; i++) for (int block = 0; block < blocks; block++)
                { file.ReadExactly(buffer); if (buffer.Any(b => b != (byte)(i + 1))) throw new InvalidDataException("Concurrent writes lost data"); }
            }
        }
        catch (Exception ex) { results.Add(RunResult.Failure(backend, "WorkloadSetup", threads, repeat, ex.ToString())); }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
    string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    string report = Path.Combine(reportDirectory, "stress-" + stamp);
    var environment = new { StartedUtc = stamp, OS = Environment.OSVersion.ToString(), Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        Processors = Environment.ProcessorCount, Workers = workers, Repeats = repeats, FileMB = fileMb, SmallFiles = smallFiles, MountRoot = mounted,
        EngineDirectories = engineDirectories, EngineFiles = engineFiles, EngineRounds = engineRounds, RemoteDelayMs = remoteDelayMs,
        Caveat = "Warm OS-cache workloads. ObjectVFS bypasses WinFsp; WinFsp is included only with --mount-root. Latencies include open/flush where noted. Not cold disk throughput." };
    File.WriteAllText(report + ".json", JsonSerializer.Serialize(new { Environment = environment, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
    var markdown = new StringBuilder("# PolyMount automated stress report\n\n").AppendLine(JsonSerializer.Serialize(environment)).AppendLine()
        .AppendLine("| Backend | Scenario | Workers | Run | Seconds | MB/s | Ops/s | Mean ms | P95 ms | P99 ms | Max ms | Peak concurrent | >=1s ops | Errors |")
        .AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
    foreach (var r in results) markdown.AppendLine(FormattableString.Invariant($"| {r.Backend} | {r.Scenario} | {r.Workers} | {r.Repeat} | {r.Seconds:0.000} | {r.MBps:0.0} | {r.OpsPerSecond:0.0} | {r.MeanMs:0.000} | {r.P95Ms:0.000} | {r.P99Ms:0.000} | {r.MaxMs:0.000} | {r.PeakConcurrentOperations} | {r.SlowOperations} | {r.Errors.Length} |"));
    markdown.AppendLine("\n## Repeat summary (median throughput)\n")
        .AppendLine("| Backend | Scenario | Workers | Median MB/s | Median ops/s | Relative to native throughput |")
        .AppendLine("|---|---|---:|---:|---:|---:|");
    double Median(IEnumerable<double> values) { var sorted = values.Order().ToArray(); return sorted.Length == 0 ? 0 : (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2; }
    foreach (var group in results.Where(r => r.Errors.Length == 0).GroupBy(r => (r.Backend, r.Scenario, r.Workers)))
    {
        double rate = Median(group.Select(r => r.MBps));
        double native = Median(results.Where(r => r.Backend == "Native" && r.Scenario == group.Key.Scenario && r.Workers == group.Key.Workers && r.Errors.Length == 0).Select(r => r.MBps));
        markdown.AppendLine(FormattableString.Invariant($"| {group.Key.Backend} | {group.Key.Scenario} | {group.Key.Workers} | {rate:0.0} | {Median(group.Select(r => r.OpsPerSecond)):0.0} | {(native == 0 ? 0 : rate / native):0.00}x |"));
    }
    markdown.AppendLine("\n## Errors\n");
    foreach (var r in results.Where(r => r.Errors.Length > 0)) markdown.AppendLine($"- {r.Backend}/{r.Scenario}: {string.Join("; ", r.Errors)}");
    File.WriteAllText(report + ".md", markdown.ToString());
    var csv = new StringBuilder("Backend,Scenario,Workers,Repeat,Bytes,Operations,Seconds,MBps,OpsPerSecond,MeanMs,P50Ms,P95Ms,P99Ms,MaxMs,CpuMs,AllocatedBytes,PeakWorkingSetBytes,PeakConcurrentOperations,SlowOperations,ErrorCount\n");
    foreach (var r in results) csv.AppendLine(FormattableString.Invariant($"{r.Backend},{r.Scenario},{r.Workers},{r.Repeat},{r.Bytes},{r.Operations},{r.Seconds},{r.MBps},{r.OpsPerSecond},{r.MeanMs},{r.P50Ms},{r.P95Ms},{r.P99Ms},{r.MaxMs},{r.CpuMs},{r.AllocatedBytes},{r.PeakWorkingSetBytes},{r.PeakConcurrentOperations},{r.SlowOperations},{r.Errors.Length}"));
    File.WriteAllText(report + ".csv", csv.ToString());
    Console.WriteLine("Reports: " + report + ".{json,md,csv}");
}
return results.Any(r => r.Errors.Length > 0) ? 1 : 0;

internal sealed record RunResult(string Backend, string Scenario, int Workers, int Repeat, long Bytes, int ExpectedOperations, int Operations,
    double Seconds, double MBps, double OpsPerSecond, double MeanMs, double P50Ms, double P95Ms, double P99Ms, double MaxMs, double CpuMs, long AllocatedBytes, long PeakWorkingSetBytes, string[] Errors)
{
    public int PeakConcurrentOperations { get; init; }
    public int SlowOperations { get; init; }
    public long PeakPrivateBytes { get; init; }
    public int PeakHandles { get; init; }
    public static RunResult Failure(string backend, string scenario, int workers, int repeat, string error) => new(backend, scenario, workers, repeat, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, new[] { error });
}
