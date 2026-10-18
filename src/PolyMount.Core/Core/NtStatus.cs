namespace PolyMount.Core;

internal static class NtStatus
{
    public const int Success = 0;
    public const int NotImplemented = unchecked((int)0xC0000002);
    public const int InvalidParameter = unchecked((int)0xC000000D);
    public const int DiskFull = unchecked((int)0xC000007F);
    public const int EndOfFile = unchecked((int)0xC0000011);
    public const int AccessDenied = unchecked((int)0xC0000022);
    public const int ObjectNameNotFound = unchecked((int)0xC0000034);
    public const int ObjectNameCollision = unchecked((int)0xC0000035);
    public const int ObjectPathNotFound = unchecked((int)0xC000003A);
    public const int NotADirectory = unchecked((int)0xC0000103);
    public const int FileIsADirectory = unchecked((int)0xC00000BA);
    public const int DirectoryNotEmpty = unchecked((int)0xC0000101);
    public const int NotSupported = unchecked((int)0xC00000BB);
    public const int SharingViolation = unchecked((int)0xC0000043);
    public const int UnexpectedIoError = unchecked((int)0xC00000E9);
}

internal static class Win32
{
    public const uint FILE_ATTRIBUTE_READONLY = 0x0001;
    public const uint FILE_ATTRIBUTE_HIDDEN = 0x0002;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x0010;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x0080;

    public const uint FILE_DIRECTORY_FILE = 0x0001;
    public const uint FILE_NON_DIRECTORY_FILE = 0x0040;

    public const uint FILE_READ_DATA = 0x0001;
    public const uint FILE_WRITE_DATA = 0x0002;
    public const uint FILE_APPEND_DATA = 0x0004;
    public const uint DELETE = 0x00010000;
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint GENERIC_ALL = 0x10000000;
}
