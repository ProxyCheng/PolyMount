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
    public int ProcessId;
    public List<VfsEntry> DirectoryItems;
    public readonly object DirectoryGate = new();
}

internal sealed class FsHandle : IDisposable
{
    public Stream Stream;
    public bool Writable;
    public DateTime TimeUtc;
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

    private RequestDiagnostics.Scope BeginRequest(string operation, string path, int fallbackProcessId = 0)
    {
        if (_vfs.Diagnostics == null) return null;
        int processId = GetOperationProcessId();
        return RequestDiagnostics.Begin(operation, path, _vfs.Diagnostics, processId != 0 ? processId : fallbackProcessId);
    }

    public WinFspMount(VirtualFileSystem vfs, string logPath)
    {
        _vfs = vfs;
    }

    public override int GetVolumeInfo(out FspVolumeInfo volumeInfo)
    {
        volumeInfo = new FspVolumeInfo { TotalSize = 1UL << 40, FreeSize = 1UL << 40 };
        return NtStatus.Success;
    }

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        using var request = BeginRequest("WinFsp.GetSecurityByName", fileName);
        fileAttributes = 0;
        securityDescriptor = null;
        if (!_vfs.TryLookup(VfsPath.Normalize(fileName), out var entry)) return NtStatus.ObjectNameNotFound;
        fileAttributes = entry.IsDir ? Win32.FILE_ATTRIBUTE_DIRECTORY : Win32.FILE_ATTRIBUTE_NORMAL;
        return NtStatus.Success;
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess,
        out object fileNode, out object fileDesc, out FspFileInfo fileInfo, out string normalizedName)
    {
        using var request = BeginRequest("WinFsp.Open", fileName);
        request?.Stage($"access=0x{grantedAccess:X} options=0x{createOptions:X}");
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
                fileNode = new FsNode { Rel = rel, IsDir = true, LocalPath = null, ProcessId = request?.ProcessId ?? 0 };
                fileInfo = BuildDirInfo(rel);
                return NtStatus.Success;
            }

            if (wantDir) return NtStatus.NotADirectory;

            // Shell metadata opens (READ_ATTRIBUTES/READ_CONTROL/SYNCHRONIZE)
            // must not download content or copy it up just to query a file.
            bool contentAccess = (grantedAccess & (Win32.GENERIC_READ | Win32.GENERIC_WRITE |
                Win32.GENERIC_ALL | Win32.FILE_READ_DATA | Win32.FILE_WRITE_DATA | Win32.FILE_APPEND_DATA)) != 0;
            if (!contentAccess)
            {
                fileNode = new FsNode { Rel = rel, IsDir = false, ProcessId = request?.ProcessId ?? 0 };
                fileInfo = BuildEntryInfo(entry, rel);
                return NtStatus.Success;
            }

            bool write =
                (grantedAccess & (Win32.GENERIC_WRITE | Win32.FILE_WRITE_DATA | Win32.FILE_APPEND_DATA)) != 0 ||
                (grantedAccess & Win32.DELETE) != 0;

            Stream stream = write ? _vfs.OpenWrite(rel, false, out entry) : _vfs.OpenRead(rel, out entry);

            fileNode = new FsNode { Rel = rel, IsDir = false, ProcessId = request?.ProcessId ?? 0 };
            fileDesc = new FsHandle { Stream = stream, Writable = write, TimeUtc = entry.TimeUtc };
            fileInfo = BuildStreamInfo(rel, stream, entry.TimeUtc);
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
        using var request = BeginRequest("WinFsp.Create", fileName);
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
                fileNode = new FsNode { Rel = rel, IsDir = true, ProcessId = request?.ProcessId ?? 0 };
                fileInfo = BuildDirInfo(rel);
                return NtStatus.Success;
            }

            uint disposition = createOptions & 0x7;
            bool truncate = disposition is 0x0 or 0x4 or 0x5;

            var stream = _vfs.OpenWrite(rel, truncate, out var entry);
            fileNode = new FsNode { Rel = rel, IsDir = false, ProcessId = request?.ProcessId ?? 0 };
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
        using var request = BeginRequest("WinFsp.GetFileInfo", (fileNode as FsNode)?.Rel ?? "", (fileNode as FsNode)?.ProcessId ?? 0);
        fileInfo = default;
        var node = fileNode as FsNode;
        if (node == null) return NtStatus.InvalidParameter;

        if ((fileDesc as FsHandle)?.Stream is { } stream)
        {
            var handle = fileDesc as FsHandle;
            fileInfo = BuildStreamInfo(node.Rel, stream, handle?.Writable == false ? handle.TimeUtc : default);
            return NtStatus.Success;
        }
        if (node.IsDir)
        {
            fileInfo = BuildDirInfo(node.Rel);
            return NtStatus.Success;
        }
        if (!_vfs.TryLookup(node.Rel, out var entry)) return NtStatus.ObjectNameNotFound;
        fileInfo = BuildEntryInfo(entry, node.Rel);
        return NtStatus.Success;
    }

    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length,
        out uint bytesTransferred)
    {
        using var request = BeginRequest("WinFsp.Read", (fileNode as FsNode)?.Rel ?? "", (fileNode as FsNode)?.ProcessId ?? 0);
        request?.Stage($"offset={offset} length={length}");
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
        using var request = BeginRequest("WinFsp.Write", (fileNode as FsNode)?.Rel ?? "", (fileNode as FsNode)?.ProcessId ?? 0);
        request?.Stage($"offset={offset} length={length}");
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
        using var request = BeginRequest("WinFsp.ReadDirectoryEntry", (fileNode as FsNode)?.Rel ?? "", (fileNode as FsNode)?.ProcessId ?? 0);
        fileName = null;
        fileInfo = default;

        var node = fileNode as FsNode;
        if (node == null || !node.IsDir) return false;

        try
        {
            if (context is not DirCursor cursor)
            {
                List<VfsEntry> items;
                lock (node.DirectoryGate)
                {
                    // WinFsp can reset the callback context at each buffer boundary.
                    // Continue the same handle's snapshot when a marker is supplied;
                    // only a fresh scan rebuilds the authoritative directory list.
                    if (string.IsNullOrEmpty(marker) || node.DirectoryItems == null)
                        node.DirectoryItems = _vfs.List(node.Rel);
                    items = node.DirectoryItems;
                }
                request?.Stage($"marker='{marker}' items={items.Count}");
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
            fileInfo = BuildEntryInfo(entry, VfsPath.Child(node.Rel, entry.Name));
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
            if ((fileDesc as FsHandle)?.Stream is { } stream) stream.Flush();
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

    private FspFileInfo BuildEntryInfo(VfsEntry e, string rel = null)
    {
        var timeUtc = e.TimeUtc == default ? DateTime.UnixEpoch : e.TimeUtc;
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
            IndexNumber = IndexOf(rel ?? e.Name),
            HardLinks = 0,
            EaSize = 0
        };
    }

    private FspFileInfo BuildStreamInfo(string rel, Stream stream, DateTime timeUtc = default)
    {
        DateTime t = timeUtc;
        if (t == default && _vfs.TryLookup(rel, out var entry)) t = entry.TimeUtc;
        if (t == default) t = DateTime.UnixEpoch;
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
        var time = _vfs.TryLookup(rel, out var entry) && entry.TimeUtc != default ? entry.TimeUtc : DateTime.UnixEpoch;
        var ft = ToFileTime(time);
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
            _vfs.Diagnostics?.Error(message, ex);
        }
        catch { }
    }

    private void Log(string message)
    {
        try
        {
            using var trace = RequestDiagnostics.Begin("WinFsp.Message", message, _vfs.Diagnostics);
        }
        catch { }
    }
}
