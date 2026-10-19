using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;
using PolyMount.Layers;

namespace PolyMount.Core.Tests;

/// <summary>A link in the middle of a path. Windows opens "\link\sub\file" by asking the mount
/// about that whole name once; the mount has to name the link component instead, because
/// "not found" ends the search before anything gets resolved.</summary>
[TestClass]
public class ReparseResolutionTests
{
    [TestMethod]
    public void A_path_through_a_link_names_the_link_component()
    {
        using var temp = new TempDir();
        var vfs = NewChain(temp);
        vfs.CreateDirectory("link");
        vfs.SetReparsePoint("link", Symlink("real"));

        Assert.IsTrue(vfs.TryFindReparsePoint(@"\link\sub\file.txt", out int index));
        Assert.AreEqual(1, index);
    }

    [TestMethod]
    public void The_offset_counts_characters_of_the_name_as_given()
    {
        using var temp = new TempDir();
        var vfs = NewChain(temp);
        vfs.CreateDirectory("a");
        vfs.SetReparsePoint("a/link", Symlink("real"));

        Assert.IsTrue(vfs.TryFindReparsePoint(@"\a\link\b\c", out int index));
        Assert.AreEqual(3, index);
    }

    [TestMethod]
    public void The_first_link_in_the_path_wins()
    {
        using var temp = new TempDir();
        var vfs = NewChain(temp);
        vfs.CreateDirectory("one");
        vfs.CreateDirectory("one/two");
        vfs.SetReparsePoint("one", Symlink("elsewhere"));
        vfs.SetReparsePoint("one/two", Symlink("elsewhere"));

        Assert.IsTrue(vfs.TryFindReparsePoint(@"\one\two\file", out int index));
        Assert.AreEqual(1, index);
    }

    [TestMethod]
    public void A_junction_is_followed_like_a_symlink()
    {
        using var temp = new TempDir();
        var vfs = NewChain(temp);
        vfs.CreateDirectory("junction");
        vfs.SetReparsePoint("junction", Junction(@"\??\C:\Windows"));

        Assert.IsTrue(vfs.TryFindReparsePoint(@"\junction\System32", out int index));
        Assert.AreEqual(1, index);
    }

    [TestMethod]
    public void A_reparse_point_that_is_not_a_name_is_not_followed()
    {
        using var temp = new TempDir();
        var vfs = NewChain(temp);
        vfs.CreateDirectory("cloud");
        vfs.SetReparsePoint("cloud", Data(0x9000001A));

        Assert.IsFalse(vfs.TryFindReparsePoint(@"\cloud\file", out _));
    }

    [TestMethod]
    public void A_path_without_links_is_not_a_reparse_point()
    {
        using var temp = new TempDir();
        var vfs = NewChain(temp);
        vfs.CreateDirectory("one");
        vfs.CreateDirectory("one/two");

        Assert.IsFalse(vfs.TryFindReparsePoint(@"\one\two\file", out _));
        Assert.IsFalse(vfs.TryFindReparsePoint(@"\one", out _));
    }

    [TestMethod]
    public void The_link_itself_is_answered_by_the_lookup_not_by_resolution()
    {
        using var temp = new TempDir();
        var vfs = NewChain(temp);
        vfs.CreateDirectory("link");
        vfs.SetReparsePoint("link", Symlink("real"));

        // A single-component name is the link itself: it exists, so it is found without resolve.
        Assert.IsTrue(vfs.TryLookup("link", out var entry));
        Assert.IsTrue(entry.IsReparsePoint);
        Assert.IsFalse(vfs.TryFindReparsePoint(@"\link", out _));
    }

    [TestMethod]
    public void The_mount_answers_the_access_check_with_the_component_to_resolve()
    {
        using var temp = new TempDir();
        var vfs = NewChain(temp);
        vfs.CreateDirectory("link");
        vfs.SetReparsePoint("link", Symlink("real"));
        var mount = new WinFspMount(vfs, temp.Sub("mount.log"));
        byte[] descriptor = null!;

        Assert.AreEqual(NtStatus.Reparse, mount.GetSecurityByName(@"\link\file.txt", out uint attributes, ref descriptor));
        Assert.AreEqual(1u, attributes, "the offset is a character index into the name");

        Assert.AreEqual(NtStatus.Success, mount.GetSecurityByName(@"\link", out attributes, ref descriptor));
        Assert.AreEqual(Win32.FILE_ATTRIBUTE_DIRECTORY | Win32.FILE_ATTRIBUTE_REPARSE_POINT, attributes);

        Assert.AreEqual(NtStatus.ObjectNameNotFound, mount.GetSecurityByName(@"\nothing\file.txt", out _, ref descriptor));
    }

    [TestMethod]
    public void The_mount_answers_an_open_that_crosses_a_link_with_the_component_to_resolve()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        source.AddFile("link/child.txt", "x");
        var vfs = new VirtualFileSystem(new ILayer[] { new OverlayLayer(temp.Sub("overlay")), source });
        vfs.SetReparsePoint("link", Junction(@"\??\C:\Windows"));
        var mount = new WinFspMount(vfs, temp.Sub("mount.log"));

        // CreateProcess opens the image through Open directly; a name that crosses the link is
        // not an object, so the mount has to name the component instead of saying not found.
        Assert.AreEqual(NtStatus.Reparse,
            mount.Open(@"\link\missing.txt", 0, 0, out _, out _, out var info, out _));
        Assert.AreEqual(1u, info.FileAttributes, "the offset is a character index into the name");
    }

    [TestMethod]
    public void A_link_over_a_real_directory_can_be_deleted_and_takes_its_reparse_data()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        source.AddFile("link/child.txt", "x");
        var vfs = new VirtualFileSystem(new ILayer[] { new OverlayLayer(temp.Sub("overlay")), source });
        vfs.SetReparsePoint("link", Junction(@"\??\C:\Windows"));

        Assert.IsTrue(vfs.TryLookup("link", out var entry));
        Assert.IsTrue(entry.IsReparsePoint);
        // The source directory below the link has a child; the link is still a deletable name.
        vfs.CheckDelete("link");
        vfs.Delete("link");
        Assert.IsFalse(vfs.TryLookup("link", out _));
        Assert.IsFalse(vfs.TryGetReparsePoint("link", out _));
    }

    [TestMethod]
    public void An_in_mount_link_is_resolved_before_the_image_loader_sees_it()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        source.AddFile("real/NeoX.exe", "MZ");
        var vfs = new VirtualFileSystem(new ILayer[] { new OverlayLayer(temp.Sub("overlay")), source });
        vfs.CreateDirectory("link");
        vfs.SetReparsePoint("link", Junction(@"\??\C:\mnt\real"));
        var mount = new WinFspMount(vfs, temp.Sub("mount.log")) { MountPoint = @"C:\mnt" };

        // CreateProcess does not restart after a reparse answer; a link that stays inside the
        // mount must be opened as the target here, or the image is found but cannot be launched.
        Assert.AreEqual(NtStatus.Success,
            mount.Open(@"\link\NeoX.exe", 0, Win32.FILE_EXECUTE, out _, out var fileDesc, out var info, out _));
        Assert.IsNotNull(fileDesc);
        Assert.AreEqual(2UL, info.FileSize);
    }

    [TestMethod]
    public void An_execute_open_gets_a_stream_backed_handle()
    {
        using var temp = new TempDir();
        var source = new FakeSourceLayer();
        source.AddFile("app.exe", "MZ");
        var vfs = new VirtualFileSystem(new ILayer[] { new OverlayLayer(temp.Sub("overlay")), source });
        var mount = new WinFspMount(vfs, temp.Sub("mount.log"));

        // CreateProcess opens the image with execute access; the loader maps it from the handle,
        // so it has to carry a readable stream rather than being treated as a metadata open.
        Assert.AreEqual(NtStatus.Success,
            mount.Open(@"\app.exe", 0, Win32.FILE_EXECUTE, out _, out var fileDesc, out var info, out _));
        Assert.IsNotNull(fileDesc, "an execute-only open must still open the content");
        Assert.AreEqual(2UL, info.FileSize);
    }

    private static VirtualFileSystem NewChain(TempDir temp) =>
        new(new ILayer[] { new OverlayLayer(temp.Sub("overlay")) });

    private static byte[] Symlink(string target) =>
        NameSurrogate(ReparseTags.Symlink, target, relative: true, pathOffset: 20);

    private static byte[] Junction(string target) =>
        NameSurrogate(ReparseTags.MountPoint, target, relative: false, pathOffset: 16);

    /// <summary>A symbol whose target is <paramref name="target"/>. A symlink keeps a flags word
    /// between its header and its path; a junction does not.</summary>
    private static byte[] NameSurrogate(uint tag, string target, bool relative, int pathOffset)
    {
        byte[] substitute = System.Text.Encoding.Unicode.GetBytes(target + "\0");
        var data = new byte[pathOffset + substitute.Length];
        BitConverter.GetBytes(tag).CopyTo(data, 0);
        BitConverter.GetBytes((ushort)(data.Length - 8)).CopyTo(data, 4);
        BitConverter.GetBytes((ushort)0).CopyTo(data, 8);
        BitConverter.GetBytes((ushort)(target.Length * 2)).CopyTo(data, 10);
        BitConverter.GetBytes((ushort)substitute.Length).CopyTo(data, 12);
        BitConverter.GetBytes((ushort)(target.Length * 2)).CopyTo(data, 14);
        if (tag == ReparseTags.Symlink) BitConverter.GetBytes(relative ? 1u : 0u).CopyTo(data, 16);
        substitute.CopyTo(data, pathOffset);
        return data;
    }

    /// <summary>A tag with no name attached, as a cloud placeholder is.</summary>
    private static byte[] Data(uint tag)
    {
        var data = new byte[8];
        BitConverter.GetBytes(tag).CopyTo(data, 0);
        return data;
    }
}
