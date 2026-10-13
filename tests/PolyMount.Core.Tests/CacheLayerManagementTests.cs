using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Core.Ipc;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class CacheLayerManagementTests
{
    private sealed class CustomCache : CacheLayer
    {
        public override string Name => "custom-management-test";
        public override string Generation => "1";
        public override bool Writable => false;
        public override bool TryGetEntry(string path, out VfsEntry entry) { entry = null!; return false; }
        public override IReadOnlyList<VfsEntry> ListOwn(string path) => Array.Empty<VfsEntry>();
        public override bool TryOpenRead(string path, out VfsEntry entry, out Stream stream)
        { entry = null!; stream = null!; return false; }
        public override void Clear() { }
    }
    [TestMethod]
    public void Cache_registration_supplies_one_clear_action_without_per_layer_definitions()
    {
        const string type = "custom-management-test";
        try
        {
            LayerRegistry.RegisterCache(type, (_, _) => new CustomCache(), null, new LayerDescriptor());
            Assert.AreEqual(ControlCommands.ClearCache, LayerRegistry.Describe(type).Actions.Single().Command);
            foreach (string builtIn in new[] { "cache", "memory-cache", "prediction-cache" })
                Assert.AreEqual(1, LayerRegistry.Describe(builtIn).Actions.Count(a => a.Command == ControlCommands.ClearCache));
            Assert.IsFalse(LayerRegistry.Describe("overlay").Actions.Any(a => a.Command == ControlCommands.ClearCache));
        }
        finally { LayerRegistry.Unregister(type); }
    }
}
