using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount;
using PolyMount.Core;

namespace PolyMount.Core.Tests;

[TestClass]
public sealed class RefreshHeadTests
{
    private sealed class RevisionSource : ILayer, IRevisionSource
    {
        public string Name => "rev";
        public bool Writable => false;
        public string Generation => Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public long Revision { get; private set; } = 10;
        public long HeadRevision() => 42;
        public void SetRevision(long rev) => Revision = rev;
        public bool TryGetEntry(string rel, out VfsEntry entry) { entry = null!; return false; }
        public IReadOnlyList<VfsEntry> ListOwn(string rel) => Array.Empty<VfsEntry>();
        public bool TryOpenRead(string rel, out VfsEntry entry, out Stream stream)
        {
            entry = null!;
            stream = null!;
            return false;
        }
    }

    [TestMethod]
    public void Refresh_head_reports_the_revision_it_moved_to()
    {
        // The editor writes this number into the pinned revision draft; if it came back empty
        // the button would move the mount and leave the field as it was.
        LayerRegistry.Register("test-revision", (_, _) => new RevisionSource(), null);
        using var temp = new TempDir();
        try
        {
            var profile = new MountProfile
            {
                Name = "t",
                MountPoint = Path.Combine(temp.Path, "mnt"),
                StoreRoot = Path.Combine(temp.Path, "store"),
                Layers = new() { new LayerConfig { Id = "rev", Type = "test-revision" } }
            };
            using var instance = new MountInstance(profile);
            Assert.AreEqual(42L, instance.RefreshHead("rev"));
        }
        finally { LayerRegistry.Unregister("test-revision"); }
    }
}
