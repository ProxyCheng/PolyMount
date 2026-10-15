using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

/// <summary>
/// Where a repeated file read is really served from, in the production chain shape:
/// memory-cache above overlay above the durable read cache above the source. The block
/// cache answers no open of its own — it wraps whatever provider served one — so these
/// tests read the block counters rather than the per-layer read metrics.
/// </summary>
[TestClass]
public sealed class MemoryCacheFlowTests
{
    private const int Block = 64 * 1024;
    private const long MemoryCapacity = 16 * 1024 * 1024;

    private sealed record Chain(VirtualFileSystem Vfs, MemoryCacheLayer Memory, FakeSourceLayer Source);

    private static Chain Build(TempDir temp, long memoryCapacity = MemoryCapacity)
    {
        var source = new FakeSourceLayer();
        var memory = new MemoryCacheLayer(memoryCapacity, Block);
        var overlay = new OverlayLayer(temp.Sub("overlay"), temp.Sub("state"));
        // Production passes the layer the hash of its whole lower chain, so a source change
        // reaches every cache above it.
        var disk = new LruCacheLayer(temp.Sub("store"), () => "v1-" + source.Generation, 4L * 1024 * 1024 * 1024);
        return new(new VirtualFileSystem(new ILayer[] { memory, overlay, disk, source }), memory, source);
    }

    private static byte[] Bytes(int length, byte seed = 7) => Enumerable.Repeat(seed, length).ToArray();

    /// <summary>Opens the file and reads it end to end through the chain.</summary>
    private static long Read(VirtualFileSystem vfs, string path)
    {
        using var stream = vfs.OpenRead(path, out _);
        var buffer = new byte[Block];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) total += read;
        return total;
    }

    private static (long Hits, long Misses) Open(Chain chain, string path, long expectedBytes = 4 * Block)
    {
        Assert.AreEqual(expectedBytes, Read(chain.Vfs, path));
        return (chain.Memory.Hits, chain.Memory.Misses);
    }

    private static string Sequence(IEnumerable<(long Hits, long Misses)> opens) =>
        string.Join(" ", opens.Select(o => $"{o.Hits}/{o.Misses}"));

    [TestMethod]
    public void Repeated_reads_hit_the_blocks_the_first_open_captured()
    {
        using var temp = new TempDir();
        var chain = Build(temp);
        chain.Source.AddFileBytes("dir/file.bin", Bytes(4 * Block));

        var opens = Enumerable.Range(0, 4).Select(_ => Open(chain, "dir/file.bin")).ToList();

        // The first open is served by the source and the rest by the disk cache above it.
        // Blocks are keyed by which bytes are visible, not by which provider served them, so
        // the pages the source read captured are the pages every later open reuses.
        Assert.AreEqual("0/4 4/4 8/4 12/4", Sequence(opens));
        Assert.AreEqual(1, chain.Source.OpenReadCount, "the disk cache answers every later open");
    }

    [TestMethod]
    public void The_blocks_of_a_file_survive_a_write_to_another_file()
    {
        using var temp = new TempDir();
        var chain = Build(temp);
        chain.Source.AddFileBytes("dir/file.bin", Bytes(4 * Block));
        var steady = Open(chain, "dir/file.bin");
        var warmed = Open(chain, "dir/file.bin");
        Assert.AreEqual(4L, warmed.Hits - steady.Hits, "blocks are resident while nothing changes");

        using (var writer = chain.Vfs.OpenWrite("other.bin", true, out _)) writer.WriteByte(1);
        var afterWrite = (chain.Memory.Hits, chain.Memory.Misses);

        var after = Open(chain, "dir/file.bin");
        Assert.AreEqual(afterWrite.Hits + 4, after.Hits, "the sibling write left this file's blocks alone");
        Assert.AreEqual(afterWrite.Misses, after.Misses);
    }

    [TestMethod]
    public void A_write_to_the_file_itself_costs_its_own_blocks()
    {
        using var temp = new TempDir();
        var chain = Build(temp);
        chain.Source.AddFileBytes("dir/file.bin", Bytes(4 * Block));
        Open(chain, "dir/file.bin");
        var before = Open(chain, "dir/file.bin");
        Assert.AreEqual(4L, before.Hits, "the file is resident");

        using (var writer = chain.Vfs.OpenWrite("dir/file.bin", true, out _)) writer.WriteByte(9);
        var afterWrite = (chain.Memory.Hits, chain.Memory.Misses);

        var after = Open(chain, "dir/file.bin", 1);
        Assert.AreEqual(afterWrite.Hits, after.Hits, "the file's own blocks went with it");
        Assert.AreEqual(afterWrite.Misses + 1, after.Misses);
        var then = Open(chain, "dir/file.bin", 1);
        Assert.AreEqual(after.Hits + 1, then.Hits, "and the new content is resident in turn");
    }

    [TestMethod]
    public void A_source_change_moves_the_identity_the_blocks_are_kept_under()
    {
        using var temp = new TempDir();
        var chain = Build(temp);
        chain.Source.AddFileBytes("dir/file.bin", Bytes(4 * Block));
        Open(chain, "dir/file.bin");
        var before = Open(chain, "dir/file.bin");
        Assert.AreEqual(4L, before.Hits, "the file is resident");

        // Same path, same size, same timestamp: only the source's own version moved, which is
        // the one component of the identity the object itself cannot show.
        chain.Source.AddFileBytes("dir/file.bin", Bytes(4 * Block, seed: 42), keepTime: true);
        chain.Source.BumpGeneration("g1");

        using var stream = chain.Vfs.OpenRead("dir/file.bin", out _);
        Assert.AreEqual(42, stream.ReadByte(), "the changed source is what the reader sees");
        Assert.AreEqual(before.Hits, chain.Memory.Hits, "and the blocks kept for the old version were not reused");
    }

    [TestMethod]
    public void A_file_larger_than_the_memory_budget_evicts_itself_and_never_hits()
    {
        using var temp = new TempDir();
        var chain = Build(temp);
        int blocks = (int)(MemoryCapacity / Block * 2); // twice the resident budget
        chain.Source.AddFileBytes("big.bin", Bytes(blocks * Block));

        var first = Open(chain, "big.bin", (long)blocks * Block);
        var second = Open(chain, "big.bin", (long)blocks * Block);

        Assert.AreEqual(0L, first.Hits, "a sequential read never revisits its own head");
        Assert.AreEqual(0L, second.Hits, "one pass over a file this size evicts everything the previous one held");
    }

    [TestMethod]
    public void The_memory_cache_never_answers_an_open_itself()
    {
        using var temp = new TempDir();
        var chain = Build(temp);
        chain.Source.AddFileBytes("dir/file.bin", Bytes(Block));

        Assert.IsFalse(chain.Memory.TryOpenRead("dir/file.bin", out _, out _), "the block cache only wraps a stream");
        Assert.AreEqual(Block, Read(chain.Vfs, "dir/file.bin"));
        Assert.AreEqual(0L, chain.Memory.Hits);
        Assert.AreEqual(1L, chain.Memory.Misses, "one block, fetched once, straight from the disk cache's copy");
        Assert.AreEqual(Block, chain.Memory.FetchedBytes, "the fetch is what the accelerator reports");
        Assert.AreEqual(0L, chain.Memory.AcceleratedBytes);
    }
}
