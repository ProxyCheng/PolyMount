using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;

namespace PolyMount.Core.Tests;

/// <summary>The pure parts of the Mount Manager mount: which spelling a configured mount point
/// is, and the forms the volume APIs take. The mount itself needs an elevated process and a
/// real WinFsp volume, so it is exercised by running an instance, not here.</summary>
[TestClass]
public class MountPointVolumeTests
{
    [TestMethod]
    public void Drive_letters_are_recognized()
    {
        Assert.IsTrue(MountPointVolume.IsDriveLetter("X:", out string letter));
        Assert.AreEqual("X", letter);
        Assert.IsTrue(MountPointVolume.IsDriveLetter("x:\\", out letter));
        Assert.AreEqual("X", letter);
    }

    [TestMethod]
    public void Anything_else_is_a_directory_mount_point()
    {
        Assert.IsFalse(MountPointVolume.IsDriveLetter(@"D:\mounts\g83", out _));
        Assert.IsFalse(MountPointVolume.IsDriveLetter(@"\\server\share", out _));
        Assert.IsFalse(MountPointVolume.IsDriveLetter("XY:", out _));
        Assert.IsFalse(MountPointVolume.IsDriveLetter("", out _));
    }

    [TestMethod]
    public void Manager_name_is_the_mount_manager_spelling()
    {
        Assert.AreEqual(@"\\.\P:", MountPointVolume.ManagerName("P"));
        Assert.AreEqual(@"\\.\P:", MountPointVolume.ManagerName("p:\\"));
    }

    [TestMethod]
    public void Directory_paths_are_absolute_and_end_with_a_separator()
    {
        Assert.AreEqual(@"C:\mounts\g83\", MountPointVolume.DirectoryPath(@"C:\mounts\g83"));
        Assert.AreEqual(@"C:\mounts\g83\", MountPointVolume.DirectoryPath(@"C:\mounts\g83\"));
    }
}
