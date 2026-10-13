using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Layers;
using System.Text.Json;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class MemoryCacheLayerTests
{
    private sealed class CountingStream(byte[] bytes) : MemoryStream
    {
        public int Reads, Writes;
        public bool FailWrite;
        public CountingStream Initialize() { base.Write(bytes, 0, bytes.Length); Position = 0; return this; }
        public override int Read(byte[] buffer, int offset, int count) { Reads++; return base.Read(buffer, offset, count); }
        public override void Write(byte[] buffer, int offset, int count)
        { if (FailWrite) throw new IOException("write failed"); Writes++; base.Write(buffer, offset, count); }
    }

    [TestMethod]
    public void Repeated_ranges_share_blocks_across_handles_and_evict_lru()
    {
        byte[] bytes = Enumerable.Range(0, 16384).Select(i => (byte)i).ToArray();
        using var cache = new MemoryCacheLayer(8192, 4096);
        var first = new CountingStream(bytes).Initialize();
        using (var stream = cache.WrapStream("file", "v1", first, false))
        {
            var read = new byte[128]; stream.Position = 25; stream.ReadExactly(read);
            CollectionAssert.AreEqual(bytes.Skip(25).Take(128).ToArray(), read);
            stream.Position = 100; stream.ReadExactly(read);
            Assert.AreEqual(1, first.Reads);
            stream.Position = 4096; stream.ReadByte();
            stream.Position = 0; stream.ReadByte(); // block zero is most recently used
            stream.Position = 8192; stream.ReadByte(); // block one is evicted
            Assert.AreEqual(8192L, cache.MemoryBytes);
        }
        var second = new CountingStream(bytes).Initialize();
        using (var stream = cache.WrapStream("FILE", "v1", second, false))
        {
            Assert.AreEqual(bytes[0], stream.ReadByte());
            Assert.AreEqual(0, second.Reads);
            stream.Position = 4096; stream.ReadByte();
            Assert.AreEqual(1, second.Reads);
        }
        using var changed = cache.WrapStream("file", "v2", new CountingStream(Enumerable.Repeat((byte)42, 16384).ToArray()).Initialize(), false);
        Assert.AreEqual(42, changed.ReadByte());
    }

    [TestMethod]
    public void Repeated_writes_coalesce_and_dirty_eviction_and_clear_preserve_data()
    {
        using var cache = new MemoryCacheLayer(4096, 4096);
        var backing = new CountingStream(new byte[8192]).Initialize();
        using var stream = cache.WrapStream("file", "staging", backing, true);
        for (int i = 0; i < 100; i++) { stream.Position = 17; stream.WriteByte((byte)i); }
        Assert.AreEqual(0, backing.Writes);
        stream.Position = 17; Assert.AreEqual(99, stream.ReadByte());
        stream.Position = 4097; stream.WriteByte(23);
        Assert.AreEqual(1, backing.Writes);
        Assert.AreEqual(99, backing.ToArray()[17]);
        cache.Clear();
        Assert.AreEqual(0L, cache.MemoryBytes);
        Assert.AreEqual(23, backing.ToArray()[4097]);
        stream.Flush();
    }

    [TestMethod]
    public void Failed_dirty_eviction_retains_page_for_retry()
    {
        using var cache = new MemoryCacheLayer(4096, 4096);
        var backing = new CountingStream(new byte[8192]).Initialize();
        using var stream = cache.WrapStream("file", "staging", backing, true);
        stream.WriteByte(42); backing.FailWrite = true;
        stream.Position = 4096;
        Assert.ThrowsException<IOException>(() => stream.WriteByte(7));
        stream.Position = 0; Assert.AreEqual(42, stream.ReadByte());
        backing.FailWrite = false; stream.Flush();
        Assert.AreEqual(42, backing.ToArray()[0]);
    }

    [TestMethod]
    public void Sparse_extension_truncation_and_cross_block_writes_match_reference()
    {
        using var cache = new MemoryCacheLayer(8192, 4096);
        var backing = new CountingStream(new byte[4096]).Initialize();
        using var stream = cache.WrapStream("file", "staging", backing, true);
        using var reference = new MemoryStream(); reference.SetLength(4096);
        var random = new Random(1234);
        for (int i = 0; i < 150; i++)
        {
            int position = random.Next(20000), count = random.Next(1, 6000);
            byte[] bytes = new byte[count]; random.NextBytes(bytes);
            stream.Position = reference.Position = position;
            stream.Write(bytes); reference.Write(bytes);
            if (i % 7 == 0) { int length = random.Next(16000); stream.SetLength(length); reference.SetLength(length); }
            Assert.IsTrue(cache.MemoryBytes <= 8192);
        }
        stream.Flush();
        CollectionAssert.AreEqual(reference.ToArray(), backing.ToArray());
    }

    [TestMethod]
    public void Vfs_shared_handles_observe_writes_and_flush_close_persist_to_overlay()
    {
        using var temp = new TempDir();
        var profile = new MountProfile { Name = "memory", StoreRoot = temp.Sub("store"), Layers = new()
        {
            new() { Id = "ram", Type = "memory-cache", Parameters = new() {
                ["CapacityBytes"] = JsonSerializer.SerializeToElement(8192), ["BlockSize"] = JsonSerializer.SerializeToElement(4096) } },
            new() { Id = "local", Type = "overlay" }
        } };
        using var mount = new MountInstance(profile);
        var vfs = mount.Vfs;
        using (var writer = vfs.OpenWrite("file", true, out _))
        {
            writer.SetLength(16384);
            using var reader = vfs.OpenRead("file", out _);
            Parallel.For(0, 4, i =>
            {
                using var handle = vfs.OpenWrite("file", false, out _);
                handle.Position = i * 4096 + 20; handle.WriteByte((byte)(i + 1));
            });
            reader.Position = 20; Assert.AreEqual(1, reader.ReadByte());
            writer.Position = 21; writer.WriteByte(77);
            vfs.InvalidateCaches(); // dirty pages are not discarded
            reader.Position = 21; Assert.AreEqual(77, reader.ReadByte());
            mount.ClearCache("ram");
            writer.Flush();
            using var durable = mount.Overlay!.TryOpenRead("file", out _, out var persisted) ? persisted : throw new Exception("missing file");
            durable.Position = 21; Assert.AreEqual(77, durable.ReadByte());
            writer.Position = 22; writer.WriteByte(88); // Close commits without explicit Flush
        }
        using var read = vfs.OpenRead("file", out _);
        read.Position = 22; Assert.AreEqual(88, read.ReadByte());
        Assert.AreEqual("local", mount.Probe("file").EffectiveLayer);
        Assert.AreEqual(0L, mount.GetStatus().Providers.Single(p => p.Name == "ram").StorageUsage!.DiskBytes);
        Assert.IsFalse(Directory.Exists(Path.Combine(profile.GetStoreRoot(), "ram")));
    }

    [TestMethod]
    public void Memory_only_chain_does_not_accept_durable_writes_and_described_defaults_build()
    {
        using var cache = new MemoryCacheLayer();
        var vfs = new VirtualFileSystem(new[] { cache });
        Assert.IsFalse(vfs.Writable);
        Assert.ThrowsException<UnauthorizedAccessException>(() => vfs.OpenWrite("file", true, out _));
        var config = new LayerConfig { Type = "memory-cache" };
        var fields = LayerRegistry.Describe(config.Type).Fields;
        LayerProperties.Apply(config, fields.Select(f => (f, f.DefaultValue!)));
        LayerRegistry.Validate(config);
        using var built = (MemoryCacheLayer)LayerRegistry.Create(config, new("unused", "unused", () => "v1"));
        Assert.AreEqual(16 * 1048576L, built.Capacity);
        Assert.AreEqual(65536, built.BlockSize);
    }
}
