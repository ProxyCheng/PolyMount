#nullable disable
using System.Runtime.InteropServices;
using System.Text;
using Fsp;
using Fsp.Interop;
using FspFileInfo = Fsp.Interop.FileInfo;
using FspVolumeInfo = Fsp.Interop.VolumeInfo;

namespace PolyMount.Core;

internal sealed class FsNode
{
    /// <summary>Volatile because a rename on one handle moves it while another callback reads it.</summary>
    public volatile string Rel = "";
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
    /// <summary>A stream has one position, and a handle can be read and written from several
    /// threads at once (paging I/O, overlapped callers). Seek-then-transfer has to be one step.</summary>
    public readonly object Gate = new();
    /// <summary>Releases the stream. A commit that fails here is reported, not swallowed: the
    /// caller decides whether it can still say so to anyone.</summary>
    public void Dispose()
    {
        Stream stream;
        lock (Gate) { stream = Stream; Stream = null; }
        if (stream == null) return;
        if (stream is FileStream fs)
        {
            try { fs.Flush(true); }
            catch { stream.Dispose(); throw; }
        }
        stream.Dispose();
    }
}

internal sealed class DirCursor
{
    public List<VfsEntry> Items = new();
    public int Index;
    public Action CountCallback;
}

public sealed class WinFspMount : FileSystemBase
{
    private readonly VirtualFileSystem _vfs;

    /// <summary>The Win32 path this volume is mounted at. A link whose target stays under it
    /// can be resolved here instead of handing the name back to Windows: CreateProcess opens an
    /// image through Open directly, and a reparse status it never saw resolved leaves the image
    /// unlaunchable. Empty when the adapter is used outside a mount (tests).</summary>
    public string MountPoint { get; set; } = string.Empty;

    private RequestDiagnostics.Scope BeginRequest(string operation, string path, int fallbackProcessId = 0)
    {
        if (_vfs.Diagnostics == null) return null;
        int processId = GetOperationProcessId();
        return RequestDiagnostics.Begin(operation, path, _vfs.Diagnostics, processId != 0 ? processId : fallbackProcessId);
    }

    /// <summary>Charges one request to the process that made it, for the performance page's pies.
    /// The id comes from the scope the callback already opened, which is where WinFsp's own answer
    /// for a Create, an Open and a Rename is kept, and where the id the mount saved on the handle
    /// stands in for the callbacks that cannot ask — so this adds no interop call to the request
    /// path, and when nobody is watching the whole thing is one comparison (see
    /// <see cref="MetricsCollector.RecordProcessAccess"/>). Zero means nobody to name: what the
    /// service fetches for its own read-ahead is work done for a guess, not for a caller.
    ///
    /// It is charged at the request rather than where the layers are, because who is using the mount
    /// is a question about the caller — the layers only know what answered them.</summary>
    private void Charge(int processId, bool write, long bytes = 0) =>
        _vfs.Metrics?.RecordProcessAccess(processId, write, bytes);

    private readonly string _storeVolume;

    public WinFspMount(VirtualFileSystem vfs, string logPath)
    {
        _vfs = vfs;
        _storeVolume = string.IsNullOrEmpty(logPath) ? null : Path.GetPathRoot(Path.GetFullPath(logPath));
    }

    /// <summary>The NTSTATUS a failure means to the caller. A collision, a full disk or a refused
    /// write are answers an application acts on; "unexpected I/O error" is not.</summary>
    internal static int StatusOf(Exception ex) => ex switch
    {
        VfsNotFoundException => NtStatus.ObjectNameNotFound,
        VfsDirectoryNotEmptyException => NtStatus.DirectoryNotEmpty,
        VfsNameCollisionException => NtStatus.ObjectNameCollision,
        UnauthorizedAccessException => NtStatus.AccessDenied,
        FileNotFoundException => NtStatus.ObjectNameNotFound,
        DirectoryNotFoundException => NtStatus.ObjectPathNotFound,
        // A source that reports the operating system's own "not there" rather than the VFS
        // exception still means the same thing. The distinction matters: a loader probing for a
        // DLL treats "not found" as "keep searching" and an I/O error as a broken disk.
        IOException io when (io.HResult & 0xFFFF) is 0x02 or 0x03 => NtStatus.ObjectNameNotFound,
        IOException io when (io.HResult & 0xFFFF) is 0x70 or 0x27 => NtStatus.DiskFull,
        IOException io when (io.HResult & 0xFFFF) is 0x20 or 0x21 => NtStatus.SharingViolation,
        _ => NtStatus.UnexpectedIoError
    };

    /// <summary>Maps a failure and logs it when it was not an answer the caller expects.</summary>
    private int Fail(string operation, Exception ex)
    {
        int status = StatusOf(ex);
        if (status == NtStatus.UnexpectedIoError || status == NtStatus.DiskFull) Log(operation, ex);
        return status;
    }

    private long _volumeCheckedAt = long.MinValue;
    private ulong _volumeTotal = 1UL << 40, _volumeFree = 1UL << 40;

    public override int GetVolumeInfo(out FspVolumeInfo volumeInfo)
    {
        // Writes land in the store, so its volume is the honest answer to "how much fits". Read
        // at most every few seconds: Explorer asks for this constantly and it changes slowly.
        long now = Environment.TickCount64;
        if (_storeVolume != null && now - Interlocked.Read(ref _volumeCheckedAt) >= 5000)
        {
            Interlocked.Exchange(ref _volumeCheckedAt, now);
            try
            {
                var drive = new DriveInfo(_storeVolume);
                _volumeTotal = (ulong)drive.TotalSize;
                _volumeFree = (ulong)drive.AvailableFreeSpace;
            }
            catch (Exception) { }
        }
        volumeInfo = new FspVolumeInfo { TotalSize = _volumeTotal, FreeSize = _vfs.Writable ? _volumeFree : 0 };
        return NtStatus.Success;
    }

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        fileAttributes = 0;
        securityDescriptor = null;
        try
        {
            using var request = BeginRequest("WinFsp.GetSecurityByName", fileName);
            Charge(request?.ProcessId ?? 0, write: false);
            var rel = VfsPath.Normalize(fileName);
            if (!_vfs.TryLookup(rel, out var entry, recordAccess: true))
            {
                // A link whose target stays inside this mount is resolved here; only a link that
                // leaves the mount is handed back to Windows (see TryResolveInMountLink).
                bool resolved = false;
                for (int hops = 0; hops < 8 && !resolved; hops++)
                {
                    if (!TryResolveInMountLink(rel, out var resolvedRel)) break;
                    rel = resolvedRel;
                    resolved = _vfs.TryLookup(rel, out entry, recordAccess: true);
                }
                if (!resolved)
                {
                    // The name itself is not an object, but a component of it may be a link. Windows
                    // opens "\link\sub\file" by asking about that whole name; saying "not found" ends
                    // the search, so name the component instead and let reparse resolution restart the
                    // call. WinFsp carries the offset back to us in FileAttributes (security.c).
                    if (_vfs.TryFindReparsePoint(fileName, out int index))
                    {
                        fileAttributes = (uint)index;
                        return NtStatus.Reparse;
                    }
                    return NtStatus.ObjectNameNotFound;
                }
            }
            fileAttributes = (entry.IsDir ? Win32.FILE_ATTRIBUTE_DIRECTORY : Win32.FILE_ATTRIBUTE_NORMAL) |
                (entry.IsReparsePoint ? Win32.FILE_ATTRIBUTE_REPARSE_POINT : 0);
            return NtStatus.Success;
        }
        catch (Exception ex) { return Fail("GetSecurityByName " + fileName, ex); }
    }

    /// <summary>Maps a path that crosses a link whose target is inside this mount to the target's
    /// mount-relative path. Windows resolves name surrogates itself, but CreateProcess opens an
    /// image through Open and does not restart after a reparse status, so an executable below an
    /// in-mount directory link was unlaunchable. Resolving the link here keeps the handle a plain
    /// file; links that leave the mount still go back to Windows as a reparse answer.</summary>
    private bool TryResolveInMountLink(string path, out string targetRel)
    {
        targetRel = string.Empty;
        if (MountPoint.Length == 0) return false;
        string rel = VfsPath.Normalize(path);
        if (!_vfs.TryFindReparsePoint(rel, out int index)) return false;
        int end = rel.IndexOf('/', index);
        if (end < 0) end = rel.Length;
        string linkPath = rel[..end];
        string remainder = end < rel.Length ? rel[end..] : string.Empty;
        if (linkPath.Length == 0) return false;
        if (!_vfs.TryGetReparsePoint(linkPath, out var data) || data is not { Length: >= 16 }) return false;
        uint tag = BitConverter.ToUInt32(data, 0);
        if (!ReparseTags.IsNameSurrogate(tag)) return false;
        int pathBase = tag == ReparseTags.Symlink ? 20 : 16;
        if (data.Length < pathBase) return false;
        int substituteOffset = BitConverter.ToUInt16(data, 8);
        int substituteLength = BitConverter.ToUInt16(data, 10);
        if (substituteLength <= 0 || pathBase + substituteOffset + substituteLength > data.Length) return false;
        string substitute = Encoding.Unicode.GetString(data, pathBase + substituteOffset, substituteLength).TrimEnd('\0');
        if (substitute.StartsWith(@"\??\", StringComparison.Ordinal)) substitute = substitute[4..];
        bool relative = tag == ReparseTags.Symlink && data.Length >= 20 && (BitConverter.ToUInt32(data, 16) & 1) != 0;
        string target;
        try
        {
            string root = Path.GetFullPath(MountPoint).TrimEnd('\\', '/');
            string linkWin32 = Path.Combine(root, linkPath.Replace('/', '\\'));
            target = Path.GetFullPath(relative
                ? Path.Combine(Path.GetDirectoryName(linkWin32)!, substitute)
                : substitute.TrimEnd('\\', '/'));
            if (!target.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return false;
            string mapped = target[(root.Length + 1)..].Replace('\\', '/');
            targetRel = VfsPath.Normalize(mapped + remainder);
            return targetRel.Length > 0;
        }
        catch (Exception) { return false; }
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess,
        out object fileNode, out object fileDesc, out FspFileInfo fileInfo, out string normalizedName)
    {
        using var request = BeginRequest("WinFsp.Open", fileName);
        request?.Stage($"access=0x{grantedAccess:X} options=0x{createOptions:X}");
        // An open is an access in its own right whatever it is for, and its direction is what it
        // asked for: everything else this mount does for that handle is that process' doing too.
        bool openedForWriting =
            (grantedAccess & (Win32.GENERIC_WRITE | Win32.FILE_WRITE_DATA | Win32.FILE_APPEND_DATA)) != 0 ||
            (grantedAccess & Win32.DELETE) != 0;
        Charge(request?.ProcessId ?? 0, write: openedForWriting);
        fileNode = null;
        fileDesc = null;
        fileInfo = default;
        normalizedName = fileName;

        try
        {
            var rel = VfsPath.Normalize(fileName);
            bool wantDir = (createOptions & Win32.FILE_DIRECTORY_FILE) != 0;
            bool wantFile = (createOptions & Win32.FILE_NON_DIRECTORY_FILE) != 0;

            if (!_vfs.TryLookup(rel, out var entry))
            {
                // Resolve an in-mount link here instead of handing it back to Windows: the image
                // loader opens through this call and does not restart after a reparse status.
                bool resolved = false;
                for (int hops = 0; hops < 8 && !resolved; hops++)
                {
                    if (!TryResolveInMountLink(rel, out var resolvedRel)) break;
                    rel = resolvedRel;
                    resolved = _vfs.TryLookup(rel, out entry);
                }
                if (!resolved)
                {
                    // Same reparse hand-off as GetSecurityByName: a name that crosses a link was
                    // never a file system object, and saying "not found" here ends the search
                    // before Windows tries the link.
                    if (_vfs.TryFindReparsePoint(fileName, out int reparseIndex))
                    {
                        fileInfo = new FspFileInfo { FileAttributes = (uint)reparseIndex };
                        return NtStatus.Reparse;
                    }
                    return NtStatus.ObjectNameNotFound;
                }
            }

            if (entry.IsDir)
            {
                if (wantFile) return NtStatus.FileIsADirectory;
                fileNode = new FsNode { Rel = rel, IsDir = true, LocalPath = null, ProcessId = request?.ProcessId ?? 0 };
                fileInfo = BuildDirInfo(rel);
                return NtStatus.Success;
            }

            if (wantDir) return NtStatus.NotADirectory;

            // Shell metadata opens (READ_ATTRIBUTES/READ_CONTROL/SYNCHRONIZE)
            // must not download content or copy it up just to query a file. Execute access is
            // not metadata: CreateProcess maps the image from this handle, so it needs a stream.
            bool contentAccess = (grantedAccess & (Win32.GENERIC_READ | Win32.GENERIC_WRITE |
                Win32.GENERIC_ALL | Win32.FILE_READ_DATA | Win32.FILE_WRITE_DATA | Win32.FILE_APPEND_DATA |
                Win32.FILE_EXECUTE)) != 0;
            if (!contentAccess)
            {
                fileNode = new FsNode { Rel = rel, IsDir = false, ProcessId = request?.ProcessId ?? 0 };
                fileInfo = BuildEntryInfo(entry, rel);
                return NtStatus.Success;
            }

            bool write = openedForWriting;

            Stream stream = write ? _vfs.OpenWrite(rel, false, out entry) : _vfs.OpenRead(rel, out entry);

            fileNode = new FsNode { Rel = rel, IsDir = false, ProcessId = request?.ProcessId ?? 0 };
            fileDesc = new FsHandle { Stream = stream, Writable = write, TimeUtc = entry.TimeUtc };
            fileInfo = BuildStreamInfo(rel, stream, entry.TimeUtc);
            return NtStatus.Success;
        }
        catch (Exception ex) { return Fail("Open " + fileName, ex); }
    }

    public override int Create(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes,
        byte[] securityDescriptor, ulong allocationSize,
        out object fileNode, out object fileDesc, out FspFileInfo fileInfo, out string normalizedName)
    {
        using var request = BeginRequest("WinFsp.Create", fileName);
        Charge(request?.ProcessId ?? 0, write: true);
        fileNode = null;
        fileDesc = null;
        fileInfo = default;
        normalizedName = fileName;

        try
        {
            var rel = VfsPath.Normalize(fileName);
            // WinFsp calls Create only to make something new; the create disposition is not in
            // createOptions (whose low bits are DIRECTORY_FILE/WRITE_THROUGH/SEQUENTIAL_ONLY).
            // An existing name is a collision, and the FSD turns that into OPEN_IF/OVERWRITE_IF.
            if (_vfs.TryLookup(rel, out _)) return NtStatus.ObjectNameCollision;

            if ((createOptions & Win32.FILE_DIRECTORY_FILE) != 0)
            {
                _vfs.CreateDirectory(rel);
                fileNode = new FsNode { Rel = rel, IsDir = true, ProcessId = request?.ProcessId ?? 0 };
                fileInfo = BuildDirInfo(rel);
                return NtStatus.Success;
            }

            var stream = _vfs.OpenWrite(rel, truncate: true, out _);
            fileNode = new FsNode { Rel = rel, IsDir = false, ProcessId = request?.ProcessId ?? 0 };
            fileDesc = new FsHandle { Stream = stream, Writable = true };
            fileInfo = BuildStreamInfo(rel, stream);
            return NtStatus.Success;
        }
        catch (Exception ex) { return Fail("Create " + fileName, ex); }
    }

    public override int Overwrite(object fileNode, object fileDesc, uint fileAttributes, bool replaceFileAttributes,
        ulong allocationSize, out FspFileInfo fileInfo)
    {
        Charge((fileNode as FsNode)?.ProcessId ?? 0, write: true);
        fileInfo = default;
        var h = fileDesc as FsHandle;
        if (h?.Stream == null) return NtStatus.InvalidParameter;
        if (!h.Writable) return NtStatus.AccessDenied;
        try
        {
            lock (h.Gate)
            {
                h.Stream.SetLength(0);
                fileInfo = BuildStreamInfo((fileNode as FsNode)?.Rel ?? "", h.Stream);
            }
            return NtStatus.Success;
        }
        catch (Exception ex) { return Fail("Overwrite", ex); }
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FspFileInfo fileInfo)
    {
        fileInfo = default;
        try
        {
            using var request = BeginRequest("WinFsp.GetFileInfo", (fileNode as FsNode)?.Rel ?? "", (fileNode as FsNode)?.ProcessId ?? 0);
            Charge(request?.ProcessId ?? 0, write: false);
            var node = fileNode as FsNode;
            if (node == null) return NtStatus.InvalidParameter;

            if (fileDesc is FsHandle handle && handle.Stream is { } stream)
            {
                fileInfo = BuildStreamInfo(node.Rel, stream, handle.Writable == false ? handle.TimeUtc : default);
                return NtStatus.Success;
            }
            if (node.IsDir)
            {
                fileInfo = BuildDirInfo(node.Rel);
                return NtStatus.Success;
            }
            if (!_vfs.TryLookup(node.Rel, out var entry, recordAccess: true)) return NtStatus.ObjectNameNotFound;
            fileInfo = BuildEntryInfo(entry, node.Rel);
            return NtStatus.Success;
        }
        catch (Exception ex) { return Fail("GetFileInfo", ex); }
    }

    public override unsafe int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length,
        out uint bytesTransferred)
    {
        using var request = BeginRequest("WinFsp.Read", (fileNode as FsNode)?.Rel ?? "", (fileNode as FsNode)?.ProcessId ?? 0);
        request?.Stage($"offset={offset} length={length}");
        bytesTransferred = 0;
        var h = fileDesc as FsHandle;
        if (h?.Stream == null) return NtStatus.InvalidParameter;

        try
        {
            int read;
            lock (h.Gate)
            {
                var stream = h.Stream ?? throw new ObjectDisposedException("handle");
                if ((long)offset != stream.Position) stream.Seek((long)offset, SeekOrigin.Begin);

                // WinFsp owns this buffer until the synchronous callback returns.
                // Read directly into it instead of allocating and copying each request.
                read = stream.Read(new Span<byte>(buffer.ToPointer(), checked((int)length)));
            }
            bytesTransferred = (uint)read;
            Charge(request?.ProcessId ?? 0, write: false, read);
            return read == 0 && length > 0 ? NtStatus.EndOfFile : NtStatus.Success;
        }
        catch (Exception ex) { return Fail("Read", ex); }
    }

    public override unsafe int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length,
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
            lock (h.Gate)
            {
                var stream = h.Stream ?? throw new ObjectDisposedException("handle");
                long size = stream.Length;
                long pos = writeToEndOfFile ? size : (long)offset;
                int count = checked((int)length);
                // Paging I/O must not grow the file: past the end it is ignored, across it truncated.
                if (constrainedIo)
                {
                    if (pos >= size) { fileInfo = BuildStreamInfo((fileNode as FsNode)?.Rel ?? "", stream); return NtStatus.Success; }
                    count = (int)Math.Min(count, size - pos);
                }
                stream.Seek(pos, SeekOrigin.Begin);
                stream.Write(new ReadOnlySpan<byte>(buffer.ToPointer(), count));
                bytesTransferred = (uint)count;
                fileInfo = BuildStreamInfo((fileNode as FsNode)?.Rel ?? "", stream);
            }
            Charge(request?.ProcessId ?? 0, write: true, bytesTransferred);
            return NtStatus.Success;
        }
        catch (Exception ex) { return Fail("Write", ex); }
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize,
        out FspFileInfo fileInfo)
    {
        Charge((fileNode as FsNode)?.ProcessId ?? 0, write: true);
        fileInfo = default;
        var h = fileDesc as FsHandle;
        if (h?.Stream == null || !h.Writable) return NtStatus.AccessDenied;
        try
        {
            lock (h.Gate)
            {
                var stream = h.Stream ?? throw new ObjectDisposedException("handle");
                // An allocation request reserves space; it changes the size only when the
                // reservation is smaller than the file, which truncates it.
                if (!setAllocationSize || (long)newSize < stream.Length) stream.SetLength((long)newSize);
                fileInfo = BuildStreamInfo((fileNode as FsNode)?.Rel ?? "", stream);
            }
            return NtStatus.Success;
        }
        catch (Exception ex) { return Fail("SetFileSize", ex); }
    }

    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes, ulong creationTime,
        ulong lastAccessTime, ulong lastWriteTime, ulong changeTime, out FspFileInfo fileInfo)
    {
        Charge((fileNode as FsNode)?.ProcessId ?? 0, write: true);
        fileInfo = default;
        if (fileNode is not FsNode) return NtStatus.InvalidParameter;
        // Objects carry no attributes or caller-set timestamps: a timestamp is the time the
        // object was committed. Accepted (copy tools set them on every file and treat a refusal
        // as a failed copy), and answered with what the file actually reports.
        return GetFileInfo(fileNode, fileDesc, out fileInfo);
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName)
    {
        try
        {
            _vfs.CheckDelete(VfsPath.Normalize(fileName));
            return NtStatus.Success;
        }
        catch (Exception ex) { return Fail("CanDelete " + fileName, ex); }
    }

    /// <summary>Marks or unmarks delete-on-close. Nothing is deleted here: the name stays until
    /// the last handle is cleaned up (see <see cref="Cleanup"/>), and a caller may still change
    /// its mind - which is what a delete disposition is.</summary>
    public override int SetDelete(object fileNode, object fileDesc, string fileName, bool deleteFile)
    {
        if (!deleteFile) return NtStatus.Success;
        Charge((fileNode as FsNode)?.ProcessId ?? 0, write: true);
        return CanDelete(fileNode, fileDesc, fileName);
    }

    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists)
    {
        int renaming = GetOperationProcessId();
        Charge(renaming != 0 ? renaming : (fileNode as FsNode)?.ProcessId ?? 0, write: true);
        try
        {
            var from = VfsPath.Normalize(fileName);
            var to = VfsPath.Normalize(newFileName);
            if (from.Length == 0 || to.Length == 0) return NtStatus.AccessDenied;
            _vfs.Rename(from, to, replaceIfExists);
            // The handle that renamed keeps working under its new name.
            if (fileNode is FsNode node) { node.Rel = to; node.DirectoryItems = null; }
            return NtStatus.Success;
        }
        catch (Exception ex) { return Fail("Rename", ex); }
    }

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker,
        ref object context, out string fileName, out FspFileInfo fileInfo)
    {
        // A cursor's continuation only copies an already resolved entry. Trace
        // the query boundary, not thousands of in-process entry callbacks.
        using var request = context is DirCursor ? null : BeginRequest("WinFsp.ReadDirectoryEntry", (fileNode as FsNode)?.Rel ?? "", (fileNode as FsNode)?.ProcessId ?? 0);
        if (context is DirCursor continuation) continuation.CountCallback?.Invoke();
        fileName = null;
        fileInfo = default;

        var node = fileNode as FsNode;
        if (node == null || !node.IsDir) return false;

        try
        {
            if (context is not DirCursor cursor)
            {
                // The query, not the thousands of entries it hands back: a cursor's continuations
                // are the same caller being served what was already resolved.
                Charge(request?.ProcessId ?? node.ProcessId, write: false);
                List<VfsEntry> items;
                bool exactName = !string.IsNullOrEmpty(pattern) && pattern.IndexOfAny(new[] { '*', '?', '<', '>', '"' }) < 0;
                request?.Stage($"pattern='{pattern}' marker='{marker}' resolving query");
                if (exactName)
                {
                    items = _vfs.TryLookup(VfsPath.Child(node.Rel, pattern), out var match, recordAccess: true)
                        ? new List<VfsEntry> { match } : new List<VfsEntry>();
                }
                else
                {
                lock (node.DirectoryGate)
                {
                    // WinFsp can reset the callback context at each buffer boundary.
                    // Continue the same handle's snapshot when a marker is supplied;
                    // only a fresh scan rebuilds the authoritative directory list.
                    if (string.IsNullOrEmpty(marker) || node.DirectoryItems == null)
                        node.DirectoryItems = _vfs.List(node.Rel);
                    items = node.DirectoryItems;
                }
                }
                if (!string.IsNullOrEmpty(pattern) && pattern != "*")
                    items = items.Where(entry => System.IO.Enumeration.FileSystemName.MatchesWin32Expression(
                        pattern, entry.Name, ignoreCase: true)).ToList();
                request?.Stage($"pattern='{pattern}' marker='{marker}' items={items.Count}");
                cursor = new DirCursor { Items = items,
                    CountCallback = _vfs.Diagnostics?.CallbackCounter("WinFsp.ReadDirectoryEntry", request?.ProcessId ?? node.ProcessId) };
                if (!string.IsNullOrEmpty(marker))
                {
                    int low = 0, high = items.Count;
                    while (low < high)
                    {
                        int middle = low + (high - low) / 2;
                        if (string.Compare(items[middle].Name, marker, StringComparison.OrdinalIgnoreCase) <= 0) low = middle + 1;
                        else high = middle;
                    }
                    cursor.Index = low;
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

    public override int GetDirInfoByName(object fileNode, object fileDesc, string fileName,
        out string normalizedName, out FspFileInfo fileInfo)
    {
        normalizedName = fileName;
        fileInfo = default;
        var node = fileNode as FsNode;
        if (node == null || !node.IsDir) return NtStatus.NotADirectory;
        try
        {
            using var request = BeginRequest("WinFsp.GetDirInfoByName", VfsPath.Child(node.Rel, fileName), node.ProcessId);
        Charge(request?.ProcessId ?? 0, write: false);
            if (!_vfs.TryLookup(VfsPath.Child(node.Rel, fileName), out var entry, recordAccess: true)) return NtStatus.ObjectNameNotFound;
            normalizedName = entry.Name;
            fileInfo = BuildEntryInfo(entry, VfsPath.Child(node.Rel, entry.Name));
            return NtStatus.Success;
        }
        catch (Exception ex) { Log("GetDirInfoByName " + fileName, ex); return NtStatus.UnexpectedIoError; }
    }

    public override int Flush(object fileNode, object fileDesc, out FspFileInfo fileInfo)
    {
        fileInfo = default;
        try
        {
            if (fileDesc is FsHandle handle)
                lock (handle.Gate) handle.Stream?.Flush();
            return GetFileInfo(fileNode, fileDesc, out fileInfo);
        }
        catch (Exception ex) { return Fail("Flush", ex); }
    }

    /// <summary>The reparse data stored for a name, or STATUS_NOT_A_REPARSE_POINT. The bytes are
    /// the ones a client wrote (mklink, an installer); Windows resolves a name-surrogate target
    /// itself, so the mount only has to remember them.</summary>
    public override int GetReparsePoint(object fileNode, object fileDesc, string fileName, ref byte[] reparseData)
    {
        reparseData = null!;
        using var request = BeginRequest("WinFsp.GetReparsePoint", (fileNode as FsNode)?.Rel ?? "");
        try
        {
            if (fileNode is not FsNode node) return NtStatus.InvalidParameter;
            if (!_vfs.TryGetReparsePoint(node.Rel, out var data) || data == null) return NtStatus.NotAReparsePoint;
            reparseData = data;
            return NtStatus.Success;
        }
        catch (Exception ex) { return Fail("GetReparsePoint", ex); }
    }

    /// <summary>The same answer for a caller that names the path rather than holding a node —
    /// the file system's own reparse resolution asks this way.</summary>
    public override int GetReparsePointByName(string fileName, bool isDirectory, ref byte[] reparseData)
    {
        reparseData = null!;
        using var request = BeginRequest("WinFsp.GetReparsePointByName", fileName);
        try
        {
            if (!_vfs.TryGetReparsePoint(VfsPath.Normalize(fileName), out var data) || data == null)
                return NtStatus.NotAReparsePoint;
            reparseData = data;
            return NtStatus.Success;
        }
        catch (Exception ex) { return Fail("GetReparsePointByName", ex); }
    }

    public override int SetReparsePoint(object fileNode, object fileDesc, string fileName, byte[] reparseData)
    {
        using var request = BeginRequest("WinFsp.SetReparsePoint", (fileNode as FsNode)?.Rel ?? "");
        try
        {
            if (fileNode is not FsNode node) return NtStatus.InvalidParameter;
            // Without a layer that accepts writes there is nowhere to keep the bytes; say so
            // rather than pretend the link was made.
            return _vfs.SetReparsePoint(node.Rel, reparseData) ? NtStatus.Success : NtStatus.AccessDenied;
        }
        catch (Exception ex) { return Fail("SetReparsePoint", ex); }
    }


    public override int DeleteReparsePoint(object fileNode, object fileDesc, string fileName, byte[] reparseData)
    {
        using var request = BeginRequest("WinFsp.DeleteReparsePoint", (fileNode as FsNode)?.Rel ?? "");
        try
        {
            if (fileNode is not FsNode node) return NtStatus.InvalidParameter;
            return _vfs.DeleteReparsePoint(node.Rel) ? NtStatus.Success : NtStatus.NotAReparsePoint;
        }
        catch (Exception ex) { return Fail("DeleteReparsePoint", ex); }
    }

    /// <summary>The caller's last handle is gone. This is where a delete disposition takes
    /// effect, and where a written file is committed: CloseHandle waits for this callback, so
    /// the bytes are durable when the caller's close returns, rather than whenever the system
    /// finally drops its last reference (Close). Cleanup cannot fail a close, so a commit that
    /// fails is logged; Close tries once more.</summary>
    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        try
        {
            if ((flags & CleanupDelete) != 0 && fileName != null)
            {
                var rel = VfsPath.Normalize(fileName);
                if (rel.Length > 0)
                {
                    try { _vfs.Delete(rel); }
                    catch (VfsNotFoundException) { }
                }
                return;
            }
            if (fileDesc is FsHandle { Writable: true } handle)
                lock (handle.Gate) handle.Stream?.Flush();
        }
        catch (Exception ex) { Log("Cleanup " + fileName, ex); }
    }

    public override void Close(object fileNode, object fileDesc)
    {
        try { (fileDesc as FsHandle)?.Dispose(); }
        catch (Exception ex) { Log("Close " + (fileNode as FsNode)?.Rel, ex); }
    }

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
            FileAttributes = (e.IsDir ? Win32.FILE_ATTRIBUTE_DIRECTORY : Win32.FILE_ATTRIBUTE_NORMAL) |
                (e.IsReparsePoint ? Win32.FILE_ATTRIBUTE_REPARSE_POINT : 0),
            ReparseTag = e.ReparseTag,
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
        bool found = _vfs.TryLookup(rel, out var entry);
        var time = found && entry.TimeUtc != default ? entry.TimeUtc : DateTime.UnixEpoch;
        var ft = ToFileTime(time);
        return new FspFileInfo
        {
            FileAttributes = Win32.FILE_ATTRIBUTE_DIRECTORY |
                (found && entry.IsReparsePoint ? Win32.FILE_ATTRIBUTE_REPARSE_POINT : 0),
            ReparseTag = found ? entry.ReparseTag : 0u,
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
