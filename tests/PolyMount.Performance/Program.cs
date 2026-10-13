using System.Diagnostics;
using PolyMount.Core;
using PolyMount.Layers;

string root = Path.Combine(Path.GetTempPath(), "PolyMount-perf-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
const int threads = 8, blocks = 128, blockSize = 65536;
byte[] payload = new byte[blockSize]; Random.Shared.NextBytes(payload);
var vfs = new VirtualFileSystem(new[] { new OverlayLayer(Path.Combine(root, "overlay"), Path.Combine(root, "state")) });
void Measure(string label, Action<int> operation)
{
    var watch = Stopwatch.StartNew();
    Parallel.For(0, threads, operation);
    watch.Stop();
    double mb = threads * blocks * blockSize / 1048576.0;
    Console.WriteLine($"{label}: {mb:0} MB, {watch.Elapsed.TotalSeconds:0.000}s, {mb / watch.Elapsed.TotalSeconds:0.0} MB/s, {threads} workers");
}
try
{
    Measure("Native write + Flush", worker =>
    {
        using var file = new FileStream(Path.Combine(root, worker + ".data"), FileMode.Create, FileAccess.Write);
        for (int i = 0; i < blocks; i++) file.Write(payload);
        file.Flush(true);
    });
    Measure("Object write + Flush", worker =>
    {
        using var file = vfs.OpenWrite(worker + ".data", true, out _);
        for (int i = 0; i < blocks; i++) file.Write(payload);
        file.Flush();
    });
    Measure("Native warm read", worker =>
    {
        using var file = File.OpenRead(Path.Combine(root, worker + ".data"));
        var buffer = new byte[blockSize];
        for (int i = 0; i < blocks; i++) file.ReadExactly(buffer);
    });
    Measure("Object warm read", worker =>
    {
        using var file = vfs.OpenRead(worker + ".data", out _);
        var buffer = new byte[blockSize];
        for (int i = 0; i < blocks; i++) file.ReadExactly(buffer);
    });
}
finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
