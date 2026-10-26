using System.Runtime.InteropServices;
using System.Text;
using Fsp;

namespace PolyMount.Core;

/// <summary>
/// Mount points Windows can resolve as a volume.
/// <para>
/// A WinFsp file system mounted the ordinary way is not registered with the Windows Mount
/// Manager: a drive letter is only a DOS device, and a directory mount point is a junction to
/// the raw volume device. Anything that asks Windows for the volume behind a path then fails —
/// AppLocker's file information and the UAC consent path both report
/// ERROR_UNRECOGNIZED_VOLUME (0x800703ED) — so programs stored on the mount cannot be started,
/// even though reading and writing them works.
/// </para>
/// <para>
/// WinFsp registers the volume when the file system is mounted through the Mount Manager, with
/// a mount point of the form <c>\\.\X:</c>. That form needs an elevated process. The drive
/// letter is only how the volume gets registered: the configured mount point is then attached
/// to the same volume with <see cref="SetVolumeMountPoint"/> and the letter is released, so an
/// instance costs no drive letter in steady state and keeps the directory its configuration
/// names.
/// </para>
/// </summary>
internal static class MountPointVolume
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetVolumeMountPoint(string mountPoint, string volumeName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteVolumeMountPoint(string mountPoint);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, uint size);

    /// <summary>One registration at a time: the transient drive letter is picked from the
    /// letters nobody has, and two mounts running at once would pick the same one.</summary>
    private static readonly object RegistrationGate = new();

    /// <summary>Whether this process can register a volume with the Mount Manager, which is
    /// what mounting as <c>\\.\X:</c> needs.</summary>
    public static bool CanRegister => Elevation.IsElevated;

    /// <summary>Whether a mount point is a drive letter (<c>X:</c> or <c>X:\</c>).</summary>
    public static bool IsDriveLetter(string mountPoint, out string letter)
    {
        letter = string.Empty;
        string trimmed = mountPoint.Trim().TrimEnd('\\', '/');
        if (trimmed.Length != 2 || trimmed[1] != ':' || !char.IsAsciiLetter(trimmed[0])) return false;
        letter = char.ToUpperInvariant(trimmed[0]).ToString();
        return true;
    }

    /// <summary>The mount point spelling that asks WinFsp for a Mount Manager volume.</summary>
    public static string ManagerName(string driveLetter)
    {
        string letter = driveLetter.TrimEnd(':', '\\', '/');
        if (letter.Length == 1) letter = char.ToUpperInvariant(letter[0]).ToString();
        return @"\\.\" + letter + ":";
    }

    /// <summary>A mount point path in the form the volume APIs require: absolute, with a
    /// trailing backslash.</summary>
    public static string DirectoryPath(string directory) =>
        Path.GetFullPath(directory).TrimEnd('\\', '/') + "\\";

    /// <summary>Mounts the file system as a Mount Manager volume and attaches it to
    /// <paramref name="directory"/>, releasing the transient drive letter. Throws when either
    /// step fails, leaving the file system unmounted.</summary>
    public static void MountAtDirectory(FileSystemHost host, string directory)
    {
        lock (RegistrationGate)
        {
            PrepareDirectory(directory);
            var failures = new List<string>();
            // A letter can be taken between looking at the drives and asking WinFsp to mount,
            // and a drive the table lists can still refuse the mount. Any of those moves on to
            // the next letter instead of failing the instance's mount.
            foreach (string letter in FreeLetters())
            {
                int rc = host.Mount(ManagerName(letter), null, false, 0);
                if (rc < 0)
                {
                    failures.Add($"{letter}: 0x{rc:X8}");
                    continue;
                }

                string? volume = VolumeName(letter);
                if (volume == null)
                {
                    failures.Add($"{letter}: not registered (error {Marshal.GetLastWin32Error()})");
                    host.Unmount();
                    continue;
                }

                if (!SetVolumeMountPoint(DirectoryPath(directory), volume))
                {
                    // The directory is the problem, not the letter: trying another one would
                    // fail the same way. Report what Windows said and leave nothing mounted.
                    int error = Marshal.GetLastWin32Error();
                    host.Unmount();
                    throw new IOException(
                        $"cannot attach the mounted volume to '{directory}' (error {error}); " +
                        "the mount point must be an empty directory on a local NTFS volume");
                }

                // The letter was only how the volume got registered. The directory is the
                // mount point now; releasing the letter keeps an instance from consuming one.
                DeleteVolumeMountPoint(letter + ":\\");
                return;
            }

            throw new IOException(
                "no drive letter could be used to register the volume with the Windows Mount Manager" +
                (failures.Count == 0 ? string.Empty : " (tried " + string.Join(", ", failures) + ")"));
        }
    }

    /// <summary>Removes the directory mount point, so the volume is not left attached to a
    /// volume that is going away. A mount point that is already gone is not an error.</summary>
    public static void DetachDirectory(string directory)
    {
        try { DeleteVolumeMountPoint(DirectoryPath(directory)); }
        catch (Exception)
        {
            // Unmounting must not fail because the mount point was already taken apart.
        }
    }

    /// <summary>Ensures the directory can become a mount point: nothing attached to it, no
    /// files in it, and it exists.</summary>
    private static void PrepareDirectory(string directory)
    {
        string path = DirectoryPath(directory);
        // A mount point left behind by a previous run points at a volume that no longer
        // exists; DeleteVolumeMountPoint is what turns it back into a directory.
        DeleteVolumeMountPoint(path);

        if (Directory.Exists(directory))
        {
            // Name what is in the way. The usual case is another instance's source having
            // materialised a folder chain here while this mount was down, and "not empty" alone
            // leaves the operator to find that out themselves.
            var occupants = Directory.EnumerateFileSystemEntries(directory).Take(4).Select(Path.GetFileName).ToArray();
            if (occupants.Length > 0)
                throw new IOException(
                    $"mount point '{directory}' already exists and is not empty (found: {string.Join(", ", occupants)}); " +
                    "choose an empty or non-existent path, or remove what is there");
            return;
        }
        Directory.CreateDirectory(directory);
    }

    /// <summary>The letters that look free, most likely usable first. A letter in use by a
    /// drive, a mapped network drive or a subst is not offered; a letter that vanishes from
    /// this list between here and the mount is handled by trying the next one.</summary>
    private static IEnumerable<string> FreeLetters()
    {
        var used = DriveInfo.GetDrives()
            .Select(drive => char.ToUpperInvariant(drive.Name[0]))
            .ToHashSet();
        for (char letter = 'Z'; letter >= 'D'; letter--)
            if (!used.Contains(letter)) yield return letter.ToString();
    }

    private static string? VolumeName(string driveLetter)
    {
        var buffer = new StringBuilder(64);
        return GetVolumeNameForVolumeMountPoint(driveLetter + ":\\", buffer, (uint)buffer.Capacity)
            ? buffer.ToString() : null;
    }
}
