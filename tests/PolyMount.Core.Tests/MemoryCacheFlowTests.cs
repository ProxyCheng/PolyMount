using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

/// <summary>
/// Where a repeated file read is really served from, in the production chain shape:
/// memory-cache above overlay above the durable read cache above the source.
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
        var disk = new LruCacheLayer(temp.Sub("store"), () => "v1", 4L * 1024 * 1024 * 1024);
        return new(new VirtualFileSystem(new ILayer[] { memory, overlay, disk, source }), memory, source);
    }

    private static byte[] Bytes(int length) => Enumerable.Repeat((byte)7, length).ToArray();

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
    public void Repeated_reads_hit_blocks_only_from_the_third_open()
    {
        using var temp = new TempDir();
        var chain = Build(temp);
        chain.Source.AddFileBytes("dir/file.bin", Bytes(4 * Block));

        var opens = Enumerable.Range(0, 4).Select(_ => Open(chain, "dir/file.bin")).ToList();

        // Open 1 is served by the source, open 2 by the disk cache, and the two see different
        // provider identities, so each captures its own set of pages. Only the third open
        // reuses the pages the second one kept.
        Assert.AreEqual("0/4 0/8 4/8 8/8", Sequence(opens));
        Assert.AreEqual(1, chain.Source.OpenReadCount, "the disk cache answers every later open");
    }

    [TestMethod]
    public void A_write_blanks_every_resident_page()
    {
        using var temp = new TempDir();
        var chain = Build(temp);
        chain.Source.AddFileBytes("dir/file.bin", Bytes(4 * Block));
        var opens = Enumerable.Range(0, 3).Select(_ => Open(chain, "dir/file.bin")).ToList();
        Assert.AreEqual("0/4 0/8 4/8", Sequence(opens), "blocks are resident while nothing changes");

        using (var writer = chain.Vfs.OpenWrite("other.bin", true, out _)) writer.WriteByte(1);
        var afterWrite = (chain.Memory.Hits, chain.Memory.Misses);

        var next = Open(chain, "dir/file.bin");
        Assert.AreEqual(afterWrite.Hits, next.Hits, "the write blanked every page above it");
        Assert.AreEqual(afterWrite.Misses + 4, next.Misses);
        var then = Open(chain, "dir/file.bin");
        Assert.AreEqual(next.Hits + 4, then.Hits, "the pass after that is resident again");
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
    }
}
