// PolyMount.TxFShim.dll -- start every transactional open in this process as an ordinary open.
//
// Injected into a program at CREATE_SUSPENDED, so that it is in place before the program resolves
// anything. Two things are patched: the import tables of every loaded module that is not part of
// Windows, and, through those, GetProcAddress and the LoadLibrary entry points.
//
// The export table of kernel32 is deliberately left alone. Patching an export means writing a
// 32-bit RVA into kernel32's image, and our module is not guaranteed to live within 4 GB of it.
// Hooking GetProcAddress covers the same callers without that restriction -- which matters here,
// because the program this was written for imports none of the transactional APIs and resolves the
// whole family by name at start-up.
//
// The replacement does what the volume would have done had it advertised transactions: it creates
// the transaction object the caller asked for, opens the object without it, and returns both.
// Commit succeeds on an empty transaction; rollback undoes nothing. That is a lie, and it is the
// same lie the volume flag would have told -- scoped to the processes this was installed for, and
// written to a log so the next person can see exactly which calls were downgraded.
//
// The ANSI forms are not intercepted: nothing measured calls them, and answering them with the wide
// implementation would reinterpret an ANSI path as UTF-16.
//
// Built with /MT: this runs inside a program that may carry its own MSVCP140 of any vintage, and
// nothing here may depend on matching it.

#include <windows.h>
#include <tlhelp32.h>
#include <stdio.h>
#include <string.h>

namespace {

using PFN_GetProcAddress = FARPROC(WINAPI*)(HMODULE, LPCSTR);
using PFN_LoadLibraryExW = HMODULE(WINAPI*)(LPCWSTR, HANDLE, DWORD);
using PFN_LoadLibraryW = HMODULE(WINAPI*)(LPCWSTR);

PFN_GetProcAddress RealGetProcAddress;
PFN_LoadLibraryExW RealLoadLibraryExW;
PFN_LoadLibraryW RealLoadLibraryW;

FARPROC WINAPI GetProcAddress_Hook(HMODULE module, LPCSTR name);
HMODULE WINAPI LoadLibraryExW_Hook(LPCWSTR path, HANDLE file, DWORD flags);
HMODULE WINAPI LoadLibraryW_Hook(LPCWSTR path);
void PatchModule(HMODULE module);

// ---------------------------------------------------------------------------------------------
// The log. One line per intercepted call, opened and closed per line: a hit is rare, and nothing
// here should hold a handle for the lifetime of a game.

SRWLOCK g_logLock = SRWLOCK_INIT;

void LogHit(const wchar_t* api, LPCWSTR path, DWORD access, BOOL ok)
{
    wchar_t directory[MAX_PATH];
    DWORD length = GetEnvironmentVariableW(L"ProgramData", directory, ARRAYSIZE(directory));
    if (length == 0 || length >= ARRAYSIZE(directory)) return;

    wchar_t file[MAX_PATH];
    if (swprintf_s(file, L"%s\\PolyMount\\txf-shim\\hits.log", directory) < 0) return;

    SYSTEMTIME now;
    GetSystemTime(&now);

    wchar_t exe[MAX_PATH];
    if (!GetModuleFileNameW(nullptr, exe, ARRAYSIZE(exe))) exe[0] = L'\0';
    const wchar_t* name = wcsrchr(exe, L'\\');
    name = name ? name + 1 : exe;

    wchar_t line[2048];
    int written = swprintf_s(line,
        L"%04d-%02d-%02dT%02d:%02d:%02d.%03dZ pid=%lu exe=%s api=%s access=0x%08lX ok=%d path=%s\r\n",
        now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond, now.wMilliseconds,
        GetCurrentProcessId(), name, api, access, ok ? 1 : 0, path ? path : L"");
    if (written <= 0) return;

    // UTF-8, not the wide characters themselves: the log is read by a managed host and by anyone
    // with Notepad, and a UTF-16 file that starts without a BOM is a file that opens as noise.
    char utf8[6144];
    int bytes = WideCharToMultiByte(CP_UTF8, 0, line, written, utf8, sizeof(utf8), nullptr, nullptr);
    if (bytes <= 0) return;

    AcquireSRWLockExclusive(&g_logLock);
    HANDLE handle = CreateFileW(file, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (handle != INVALID_HANDLE_VALUE)
    {
        DWORD ignored = 0;
        WriteFile(handle, utf8, (DWORD)bytes, &ignored, nullptr);
        CloseHandle(handle);
    }
    ReleaseSRWLockExclusive(&g_logLock);
}

// ---------------------------------------------------------------------------------------------
// Replacements. Each opens the object the ordinary way and ignores the transaction the caller
// already owns: CreateFileTransactedW takes a handle in and has no way to hand one back, so there
// is nothing here to create, and the caller's commit or rollback simply finds nothing to do.

HANDLE WINAPI Shim_CreateFileTransactedW(LPCWSTR path, DWORD access, DWORD share,
    LPSECURITY_ATTRIBUTES attributes, DWORD disposition, DWORD flags, HANDLE templateFile,
    HANDLE /*transaction*/, PUSHORT /*miniVersion*/, PVOID /*extendedParameters*/)
{
    HANDLE file = CreateFileW(path, access, share, attributes, disposition, flags, templateFile);
    LogHit(L"CreateFileTransactedW", path, access, file != INVALID_HANDLE_VALUE);
    return file;
}

BOOL WINAPI Shim_CreateDirectoryTransactedW(LPCWSTR /*template*/, LPCWSTR path,
    LPSECURITY_ATTRIBUTES attributes, HANDLE /*transaction*/)
{
    BOOL ok = CreateDirectoryW(path, attributes);
    LogHit(L"CreateDirectoryTransactedW", path, 0, ok);
    return ok;
}

BOOL WINAPI Shim_MoveFileTransactedW(LPCWSTR from, LPCWSTR to, LPPROGRESS_ROUTINE /*progress*/,
    LPVOID /*data*/, DWORD flags, HANDLE /*transaction*/)
{
    BOOL ok = MoveFileExW(from, to, flags);
    LogHit(L"MoveFileTransactedW", from, 0, ok);
    return ok;
}

BOOL WINAPI Shim_DeleteFileTransactedW(LPCWSTR path, HANDLE /*transaction*/)
{
    BOOL ok = DeleteFileW(path);
    LogHit(L"DeleteFileTransactedW", path, 0, ok);
    return ok;
}

BOOL WINAPI Shim_CopyFileTransactedW(LPCWSTR from, LPCWSTR to, LPPROGRESS_ROUTINE progress,
    LPVOID data, LPBOOL cancel, DWORD flags, HANDLE /*transaction*/)
{
    BOOL ok = CopyFileExW(from, to, progress, data, cancel, flags);
    LogHit(L"CopyFileTransactedW", from, 0, ok);
    return ok;
}

HANDLE WINAPI Shim_FindFirstFileTransactedW(LPCWSTR pattern, FINDEX_INFO_LEVELS infoLevel,
    LPVOID findData, FINDEX_SEARCH_OPS search, LPVOID filter, DWORD flags, HANDLE /*transaction*/)
{
    // The transacted form is FindFirstFileExW with a transaction handle appended, so this is the
    // same call with the same argument order.
    HANDLE handle = FindFirstFileExW(pattern, infoLevel, findData, search, filter, flags);
    LogHit(L"FindFirstFileTransactedW", pattern, 0, handle != INVALID_HANDLE_VALUE);
    return handle;
}

BOOL WINAPI Shim_RemoveDirectoryTransactedW(LPCWSTR path, HANDLE /*transaction*/)
{
    BOOL ok = RemoveDirectoryW(path);
    LogHit(L"RemoveDirectoryTransactedW", path, 0, ok);
    return ok;
}

// ---------------------------------------------------------------------------------------------
// Import-table patching.

FARPROC ReplacementFor(const char* name)
{
    if (_stricmp(name, "CreateFileTransactedW") == 0) return (FARPROC)&Shim_CreateFileTransactedW;
    if (_stricmp(name, "CreateDirectoryTransactedW") == 0) return (FARPROC)&Shim_CreateDirectoryTransactedW;
    if (_stricmp(name, "MoveFileTransactedW") == 0) return (FARPROC)&Shim_MoveFileTransactedW;
    if (_stricmp(name, "DeleteFileTransactedW") == 0) return (FARPROC)&Shim_DeleteFileTransactedW;
    if (_stricmp(name, "CopyFileTransactedW") == 0) return (FARPROC)&Shim_CopyFileTransactedW;
    if (_stricmp(name, "FindFirstFileTransactedW") == 0) return (FARPROC)&Shim_FindFirstFileTransactedW;
    if (_stricmp(name, "RemoveDirectoryTransactedW") == 0) return (FARPROC)&Shim_RemoveDirectoryTransactedW;
    if (_stricmp(name, "GetProcAddress") == 0) return (FARPROC)&GetProcAddress_Hook;
    if (_stricmp(name, "LoadLibraryExW") == 0) return (FARPROC)&LoadLibraryExW_Hook;
    if (_stricmp(name, "LoadLibraryW") == 0) return (FARPROC)&LoadLibraryW_Hook;
    return nullptr;
}

bool IsWindowsModule(HMODULE module)
{
    wchar_t path[MAX_PATH];
    if (!GetModuleFileNameW(module, path, ARRAYSIZE(path))) return false;
    wchar_t system[MAX_PATH];
    DWORD length = GetSystemDirectoryW(system, ARRAYSIZE(system));
    if (length == 0 || length >= ARRAYSIZE(system)) return false;
    return _wcsnicmp(path, system, length) == 0;
}

void PatchImportTable(HMODULE module)
{
    auto base = (BYTE*)module;
    auto dos = (IMAGE_DOS_HEADER*)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return;
    if (dos->e_lfanew <= 0 || dos->e_lfanew > 0x10000) return;
    auto nt = (IMAGE_NT_HEADERS*)(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return;
    // A 64-bit shim only ever sees 64-bit images; anything else here is not an image at all.
    if (nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC) return;

    auto& directory = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (directory.VirtualAddress == 0 || directory.Size == 0) return;
    // Walking an RVA needs the RVA to lie inside the image. A mapping that is not an image (see
    // LoadLibraryExW_Hook) answers with file offsets, and reading them as addresses faults.
    if (directory.VirtualAddress + directory.Size > nt->OptionalHeader.SizeOfImage) return;

    auto end = (IMAGE_IMPORT_DESCRIPTOR*)(base + directory.VirtualAddress + directory.Size);
    for (auto descriptor = (IMAGE_IMPORT_DESCRIPTOR*)(base + directory.VirtualAddress);
         descriptor < end && descriptor->Name != 0; descriptor++)
    {
        // Only the loader's resolution matters here: the names live in the original thunk array.
        if (descriptor->OriginalFirstThunk == 0) continue;
        const char* dll = (const char*)(base + descriptor->Name);
        bool kernel = _stricmp(dll, "kernel32.dll") == 0 || _stricmp(dll, "kernelbase.dll") == 0;
        if (!kernel) continue;

        auto names = (IMAGE_THUNK_DATA*)(base + descriptor->OriginalFirstThunk);
        auto imports = (IMAGE_THUNK_DATA*)(base + descriptor->FirstThunk);

        SIZE_T count = 0;
        while (names[count].u1.AddressOfData != 0) count++;

        DWORD previous = 0;
        VirtualProtect(imports, (count + 1) * sizeof(IMAGE_THUNK_DATA), PAGE_READWRITE, &previous);
        for (SIZE_T index = 0; index < count; index++)
        {
            if (IMAGE_SNAP_BY_ORDINAL(names[index].u1.Ordinal)) continue;
            auto byName = (IMAGE_IMPORT_BY_NAME*)(base + names[index].u1.AddressOfData);
            if (FARPROC replacement = ReplacementFor((const char*)byName->Name))
                imports[index].u1.Function = (ULONG_PTR)replacement;
        }
        DWORD ignored = 0;
        VirtualProtect(imports, (count + 1) * sizeof(IMAGE_THUNK_DATA), previous, &ignored);
    }
}

void PatchModule(HMODULE module)
{
    static HMODULE self = GetModuleHandleW(L"PolyMount.TxFShim.dll");
    if (module == nullptr || module == self) return;
    if (IsWindowsModule(module)) return; // the calls that matter are the application's
    PatchImportTable(module);
}

void PatchLoadedModules()
{
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, GetCurrentProcessId());
    if (snapshot == INVALID_HANDLE_VALUE)
    {
        PatchModule(GetModuleHandleW(nullptr));
        return;
    }
    MODULEENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    if (Module32FirstW(snapshot, &entry))
        do { PatchModule(entry.hModule); } while (Module32NextW(snapshot, &entry));
    CloseHandle(snapshot);
}

// ---------------------------------------------------------------------------------------------
// Hooks for the two ways a module can arrive at a function after it has loaded.

FARPROC WINAPI GetProcAddress_Hook(HMODULE module, LPCSTR name)
{
    // Two forms arrive here, and only one is a string: a name, and an ordinal packed into the
    // pointer itself (MAKEINTRESOURCE). Reading the ordinal as a string is a fault in our own code,
    // not in the caller's -- a host that resolves an ordinal export died at start-up with
    // 0xc0000005 inside the comparison before this check existed.
    if (name != nullptr && !IS_INTRESOURCE(name))
        if (FARPROC replacement = ReplacementFor(name)) return replacement;
    return RealGetProcAddress(module, name);
}

HMODULE WINAPI LoadLibraryExW_Hook(LPCWSTR path, HANDLE file, DWORD flags)
{
    HMODULE module = RealLoadLibraryExW(path, file, flags);
    // A datafile load maps the file, not the image: its section RVAs are file offsets, so the
    // import directory cannot be walked, and nothing is called through such a module anyway. A
    // host that reads a DLL's resources this way is not a host this shim can or should patch.
    const DWORD notAnImage = LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE | LOAD_LIBRARY_AS_DATAFILE_EXCLUSIVE;
    if (module && (flags & notAnImage) == 0) PatchModule(module);
    return module;
}

HMODULE WINAPI LoadLibraryW_Hook(LPCWSTR path)
{
    HMODULE module = RealLoadLibraryW(path);
    if (module) PatchModule(module);
    return module;
}

void Initialize()
{
    HMODULE kernel32 = GetModuleHandleW(L"kernel32.dll");
    if (!kernel32) return;

    // Resolved before anything is patched: these are the addresses the hooks forward to.
    RealGetProcAddress = (PFN_GetProcAddress)GetProcAddress(kernel32, "GetProcAddress");
    RealLoadLibraryExW = (PFN_LoadLibraryExW)GetProcAddress(kernel32, "LoadLibraryExW");
    RealLoadLibraryW = (PFN_LoadLibraryW)GetProcAddress(kernel32, "LoadLibraryW");
    if (!RealGetProcAddress) return;

    // One line when the shim arrives, so a log can tell "the shim was never in this process" from
    // "it was there and nothing called anything transacted".
    LogHit(L"attach", L"", 0, TRUE);

    PatchLoadedModules();
}

} // namespace

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(instance);
        Initialize();
    }
    return TRUE;
}
