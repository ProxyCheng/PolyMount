using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;

namespace PolyMount.Core.Tests;

[TestClass]
public class MountPointPreparationTests
{
    [TestMethod]
    public void Nonexistent_directory_is_left_for_winfsp_to_create()
    {
        using var t = new TempDir();
        var target = t.Sub("doesnotexist");
        MountInstance.PrepareMountPoint(target); // must not throw
        Assert.IsFalse(Directory.Exists(target));
    }

    [TestMethod]
    public void Existing_empty_directory_is_removed()
    {
        using var t = new TempDir();
        var target = t.Sub("empty");
        Directory.CreateDirectory(target);

        MountInstance.PrepareMountPoint(target);

        Assert.IsFalse(Directory.Exists(target), "empty mount point should be deleted so WinFsp can mount");
    }

    [TestMethod]
    public void Nonempty_directory_throws_clear_error()
    {
        using var t = new TempDir();
        var target = t.Sub("full");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "x");

        var ex = Assert.ThrowsException<IOException>(() => MountInstance.PrepareMountPoint(target));
        StringAssert.Contains(ex.Message, "not empty");
        Assert.IsTrue(Directory.Exists(target), "a non-empty mount point must not be touched");
    }

    [TestMethod]
    public void Drive_letter_mount_point_is_ignored()
    {
        // Should return immediately without touching the filesystem.
        MountInstance.PrepareMountPoint("X:");
        MountInstance.PrepareMountPoint("X:\\");
    }
}
