#nullable disable
using System.Runtime.InteropServices;
using Fsp;
using Fsp.Interop;
using FspFileInfo = Fsp.Interop.FileInfo;
using FspVolumeInfo = Fsp.Interop.VolumeInfo;

namespace PolyMount.Core;

internal sealed class FsNode
{
    public string Rel = "";
    public bool IsDir;
    public string LocalPath;
}

internal sealed class FsHandle : IDisposable
{
    public Stream Stream;
    public bool Writable;
    public void Dispose()
    {
        if (Stream is FileStream fs)
        {
            try { fs.Flush(true); } catch { }
        }
        try { Stream?.Dispose(); } catch { }
        Stream = null;
    }
}

internal sealed class DirCursor
{
    public List<VfsEntry> Items = new();
    public int Index;
}

public sealed class WinFspMount : FileSystemBase
{
    private readonly VirtualFileSystem _vfs;
    private readonly string _logPath;
    private readonly object _logGate = new();

    public WinFspMount(VirtualFileSystem vfs, string logPath)
    {
        _vfs = vfs;
        _logPath = logPath;
    }

    public override int GetVolumeInfo(out FspVolumeInfo volumeInfo)
    {
        volumeInfo = new FspVolumeInfo { TotalSize = 1UL << 40, FreeSize = 1UL << 40 };
        return NtStatus.Success;
    }

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        fileAttributes = 0;
        securityDescriptor = null;
        if (!_vfs.TryLookup(VfsPath.Normalize(fileName), out var entry)) return NtStatus.ObjectNameNotFound;
        fileAttributes = entry.IsDir ? Win32.FILE_ATTRIBUTE_DIRECTORY : Win32.FILE_ATTRIBUTE_NORMAL;
        return NtStatus.Success;
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess,
        out object fileNode, out object fileDesc, out FspFileInfo fileInfo, out string normalizedName)
    {
        fileNode = null;
        fileDesc = null;
        fileInfo = default;
        normalizedName = fileName;

        try
        {
            var rel = VfsPath.Normalize(fileName);
            bool wantDir = (createOptions & Win32.FILE_DIRECTORY_FILE) != 0;
            bool wantFile = (createOptions & Win32.FILE_NON_DIRECTORY_FILE) != 0;

            if (!_vfs.TryLookup(rel, out var entry)) return NtStatus.ObjectNameNotFound;

            if (entry.IsDir)
            {
                if (wantFile) return NtStatus.FileIsADirectory;
                fileNode = new FsNode { Rel = rel, IsDir = true, LocalPath = null };
                fileInfo = BuildDirInfo(rel);
                return NtStatus.Success;
            }

            if (wantDir) return NtStatus.NotADirectory;

            bool write =
                (grantedAccess & (Win32.GENERIC_WRITE | Win32.FILE_WRITE_DATA | Win32.FILE_APPEND_DATA)) != 0 ||
                (grantedAccess & Win32.DELETE) != 0;

            Stream stream = write ? _vfs.OpenWrite(rel, false, out entry) : _vfs.OpenRead(rel, out entry);

            fileNode = new FsNode { Rel = rel, IsDir = false };
            fileDesc = new FsHandle { Stream = stream, Writable = write };
            fileInfo = BuildStreamInfo(rel, stream);
            return NtStatus.Success;
        }
        catch (VfsNotFoundException) { return NtStatus.ObjectNameNotFound; }
        catch (UnauthorizedAccessException) { return NtStatus.AccessDenied; }
        catch (Exception ex) { Log("Open " + fileName, ex); return NtStatus.UnexpectedIoError; }
    }

    public override int Create(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes,
        byte[] securityDescriptor, ulong allocationSize,
        out object fileNode, out object fileDesc, out FspFileInfo fileInfo, out string normalizedName)
    {
        fileNode = null;
        fileDesc = null;
        fileInfo = default;
        normalizedName = fileName;

        try
        {
            var rel = VfsPath.Normalize(fileName);

            if ((createOptions & Win32.FILE_DIRECTORY_FILE) != 0)
            {
                if (_vfs.TryLookup(rel, out _)) return NtStatus.ObjectNameCollision;
                _vfs.CreateDirectory(rel);
                fileNode = new FsNode { Rel = rel, IsDir = true };
                fileInfo = BuildDirInfo(rel);
                return NtStatus.Success;
            }

            uint disposition = createOptions & 0x7;
            bool truncate = disposition is 0x0 or 0x4 or 0x5;

            var stream = _vfs.OpenWrite(rel, truncate, out var entry);
            fileNode = new FsNode { Rel = rel, IsDir = false };
            fileDesc = new FsHandle { Stream = stream, Writable = true };
            fileInfo = BuildStreamInfo(rel, stream);
            return NtStatus.Success;
        }
        catch (UnauthorizedAccessException) { return NtStatus.AccessDenied; }
        catch (Exception ex) { Log("Create " + fileName, ex); return NtStatus.UnexpectedIoError; }
    }

    public override int Overwrite(object fileNode, object fileDesc, uint fileAttributes, bool replaceFileAttributes,
        ulong allocationSize, out FspFileInfo fileInfo)
    {
        fileInfo = default;
        var h = fileDesc as FsHandle;
        if (h?.Stream == null) return NtStatus.InvalidParameter;
        try
        {
            h.Stream.SetLength(0);
            fileInfo = BuildStreamInfo((fileNode as FsNode)?.Rel ?? "", h.Stream);
            return NtStatus.Success;
        }
        catch (Exception ex) { Log("Overwrite", ex); return NtStatus.UnexpectedIoError; }
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FspFileInfo fileInfo)
    {
        fileInfo = default;
        var node = fileNode as FsNode;
        if (node == null) return NtStatus.InvalidParameter;

        if ((fileDesc as FsHandle)?.Stream is { } stream)
        {
            fileInfo = BuildStreamInfo(node.Rel, stream);
            return NtStatus.Success;
        }
        if (node.IsDir)
        {
            fileInfo = BuildDirInfo(node.Rel);
            return NtStatus.Success;
        }
        if (!_vfs.TryLookup(node.Rel, out var entry)) return NtStatus.ObjectNameNotFound;
        fileInfo = BuildEntryInfo(entry);
        return NtStatus.Success;
    }

    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length,
        out uint bytesTransferred)
    {
        bytesTransferred = 0;
        var h = fileDesc as FsHandle;
        if (h?.Stream == null) return NtStatus.InvalidParameter;

        try
        {
            var stream = h.Stream;
            if ((long)offset != stream.Position) stream.Seek((long)offset, SeekOrigin.Begin);

            var temp = new byte[length];
            int read = stream.Read(temp, 0, (int)length);
            if (read > 0) Marshal.Copy(temp, 0, buffer, read);
            bytesTransferred = (uint)read;
            return read == 0 && length > 0 ? NtStatus.EndOfFile : NtStatus.Success;
        }
        catch (Exception ex) { Log("Read", ex); return NtStatus.UnexpectedIoError; }
    }

    public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length,
        bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred, out FspFileInfo fileInfo)
    {
        bytesTransferred = 0;
        fileInfo = default;
        var h = fileDesc as FsHandle;
        if (h?.Stream == null || !h.Writable) return NtStatus.AccessDenied;

        try
        {
            var stream = h.Stream;
            long pos = writeToEndOfFile ? stream.Length : (long)offset;
            stream.Seek(pos, SeekOrigin.Begin);

            var temp = new byte[length];
            Marshal.Copy(buffer, temp, 0, (int)length);
            stream.Write(temp, 0, (int)length);
            bytesTransferred = length;

            fileInfo = BuildStreamInfo((fileNode as FsNode)?.Rel ?? "", stream);
            return NtStatus.Success;
        }
        catch (Exception ex) { Log("Write", ex); return NtStatus.UnexpectedIoError; }
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize,
        out FspFileInfo fileInfo)
    {
        fileInfo = default;
        var h = fileDesc as FsHandle;
        if (h?.Stream == null || !h.Writable) return NtStatus.AccessDenied;
        try
        {
            h.Stream.SetLength((long)newSize);
            fileInfo = BuildStreamInfo((fileNode as FsNode)?.Rel ?? "", h.Stream);
            return NtStatus.Success;
        }
        catch (Exception ex) { Log("SetFileSize", ex); return NtStatus.UnexpectedIoError; }
    }

    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes, ulong creationTime,
        ulong lastAccessTime, ulong lastWriteTime, ulong changeTime, out FspFileInfo fileInfo)
    {
        fileInfo = default;
        var node = fileNode as FsNode;
        if (node == null) return NtStatus.InvalidParameter;
        try
        {
            if ((fileDesc as FsHandle)?.Stream is FileStream fs && lastWriteTime != 0 && !node.IsDir)
            {
                try { File.SetLastWriteTimeUtc(fs.Name, DateTime.FromFileTimeUtc((long)lastWriteTime)); } catch { }
            }
            return GetFileInfo(fileNode, fileDesc, out fileInfo);
        }
        catch (Exception ex) { Log("SetBasicInfo", ex); return NtStatus.UnexpectedIoError; }
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName) => _vfs.Writable ? NtStatus.Success : NtStatus.AccessDenied;

    public override int SetDelete(object fileNode, object fileDesc, string fileName, bool deleteFile)
    {
        if (!deleteFile) return NtStatus.Success;
        try
        {
            var rel = VfsPath.Normalize(fileName);
            if (rel.Length == 0) return NtStatus.AccessDenied;
            _vfs.Delete(rel);
            return NtStatus.Success;
        }
        catch (VfsNotFoundException) { return NtStatus.ObjectNameNotFound; }
        catch (UnauthorizedAccessException) { return NtStatus.AccessDenied; }
        catch (Exception ex) { Log("SetDelete " + fileName, ex); return NtStatus.UnexpectedIoError; }
    }

    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists)
    {
        try
        {
            var from = VfsPath.Normalize(fileName);
            var to = VfsPath.Normalize(newFileName);
            if (from.Length == 0 || to.Length == 0) return NtStatus.AccessDenied;
            _vfs.Rename(from, to, replaceIfExists);
            return NtStatus.Success;
        }
        catch (VfsNotFoundException) { return NtStatus.ObjectNameNotFound; }
        catch (UnauthorizedAccessException) { return NtStatus.AccessDenied; }
        catch (Exception ex) { Log("Rename", ex); return NtStatus.UnexpectedIoError; }
    }

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker,
        ref object context, out string fileName, out FspFileInfo fileInfo)
    {
        fileName = null;
        fileInfo = default;

        var node = fileNode as FsNode;
        if (node == null || !node.IsDir) return false;

        try
        {
            if (context is not DirCursor cursor)
            {
                var items = _vfs.List(node.Rel);
                Log($"ReadDirectoryEntry rel='{node.Rel}' marker='{marker}' -> {items.Count} items");
                cursor = new DirCursor { Items = items };
                if (!string.IsNullOrEmpty(marker))
                {
                    int i = 0;
                    while (i < items.Count &&
                           string.Compare(items[i].Name, marker, StringComparison.OrdinalIgnoreCase) <= 0) i++;
                    cursor.Index = i;
                }
                context = cursor;
            }

            if (cursor.Index >= cursor.Items.Count) return false;

            var entry = cursor.Items[cursor.Index++];
            fileName = entry.Name;
            fileInfo = BuildEntryInfo(entry);
            return true;
        }
        catch (Exception ex)
        {
            Log("ReadDirectoryEntry rel='" + node.Rel + "'", ex);
            return false;
        }
    }

    public override int Flush(object fileNode, object fileDesc, out FspFileInfo fileInfo)
    {
        fileInfo = default;
        try
        {
            if ((fileDesc as FsHandle)?.Stream is FileStream fs) fs.Flush(true);
            return GetFileInfo(fileNode, fileDesc, out fileInfo);
        }
        catch (Exception ex) { Log("Flush", ex); return NtStatus.UnexpectedIoError; }
    }

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags) { }

    public override void Close(object fileNode, object fileDesc) => (fileDesc as FsHandle)?.Dispose();

    public override int GetSecurity(object fileNode, object fileDesc, ref byte[] securityDescriptor)
    {
        securityDescriptor = null;
        return NtStatus.Success;
    }

    // ---- helpers ----

    private static ulong ToFileTime(DateTime utc)
    {
        if (utc.Kind != DateTimeKind.Utc) utc = utc.ToUniversalTime();
        long ft = utc.ToFileTimeUtc();
        return ft < 0 ? 0UL : (ulong)ft;
    }

    private static ulong IndexOf(string rel)
    {
        ulong hash = 1469598103934665603UL;
        foreach (var c in rel) { hash ^= c; hash *= 1099511628211UL; }
        return hash == 0 ? 1 : hash;
    }

    private static long RoundUp(long size) => size <= 0 ? 0 : (size + 4095) / 4096 * 4096;

    private FspFileInfo BuildEntryInfo(VfsEntry e)
    {
        var timeUtc = e.TimeUtc == default ? DateTime.UtcNow : e.TimeUtc;
        var ft = ToFileTime(timeUtc);
        long size = e.IsDir ? 0 : e.Size;
        return new FspFileInfo
        {
            FileAttributes = e.IsDir ? Win32.FILE_ATTRIBUTE_DIRECTORY : Win32.FILE_ATTRIBUTE_NORMAL,
            ReparseTag = 0,
            FileSize = (ulong)size,
            AllocationSize = (ulong)RoundUp(size),
            CreationTime = ft,
            LastAccessTime = ft,
            LastWriteTime = ft,
            ChangeTime = ft,
            IndexNumber = IndexOf(e.Name),
            HardLinks = 0,
            EaSize = 0
        };
    }

    private FspFileInfo BuildStreamInfo(string rel, Stream stream)
    {
        DateTime t;
        try { t = stream is FileStream fs ? File.GetLastWriteTimeUtc(fs.Name) : DateTime.UtcNow; }
        catch { t = DateTime.UtcNow; }
        var ft = ToFileTime(t);
        long size = 0;
        try { size = stream.Length; } catch { }
        return new FspFileInfo
        {
            FileAttributes = Win32.FILE_ATTRIBUTE_NORMAL,
            ReparseTag = 0,
            FileSize = (ulong)size,
            AllocationSize = (ulong)RoundUp(size),
            CreationTime = ft,
            LastAccessTime = ft,
            LastWriteTime = ft,
            ChangeTime = ft,
            IndexNumber = IndexOf(rel),
            HardLinks = 0,
            EaSize = 0
        };
    }

    private FspFileInfo BuildDirInfo(string rel)
    {
        var ft = ToFileTime(DateTime.UtcNow);
        return new FspFileInfo
        {
            FileAttributes = Win32.FILE_ATTRIBUTE_DIRECTORY,
            ReparseTag = 0,
            FileSize = 0,
            AllocationSize = 0,
            CreationTime = ft,
            LastAccessTime = ft,
            LastWriteTime = ft,
            ChangeTime = ft,
            IndexNumber = IndexOf(rel),
            HardLinks = 0,
            EaSize = 0
        };
    }

    private void Log(string message, Exception ex)
    {
        try
        {
            lock (_logGate)
                File.AppendAllText(_logPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}: {ex.GetType().Name}: {ex.Message}\n");
        }
        catch { }
    }

    private void Log(string message)
    {
        try
        {
            lock (_logGate)
                File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}\n");
        }
        catch { }
    }
}
