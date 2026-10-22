// PolyMount.TxFShim.exe -- start a program with the shim already inside it.
//
// It is two things at once: the launcher a person can run by hand, and the Image File Execution
// Options Debugger value the plugin writes. In both cases Windows hands it the image path as its
// first argument and expects it to start the program -- which is all it does, with one addition:
// the shim is loaded into the child before a single instruction of the program runs.
//
// Starting the child is the hard part, and every step of it is here because the obvious version
// does not work:
//
//  * A plain CreateProcess recurses. The Debugger value applies to every create of that image, this
//    launcher's included, so the child would be this launcher again.
//  * Creating the child as a debuggee (DEBUG_ONLY_THIS_PROCESS) is how Windows expects a debugger
//    to start its target, and the option is skipped for that one call. The debugger is then
//    detached before the program resumes, so it never sees one.
//  * A debuggee is frozen while a debug event is pending, so a thread injected at that moment never
//    runs: the wait reads STILL_ACTIVE until it times out (measured). The injection thread is
//    therefore created suspended, the process is suspended around the transfer, and only then are
//    both released.
//  * The program must not run while the library loads. Resuming the program first and the injection
//    thread second is a race the program wins when it is short: a probe that finishes in two
//    milliseconds reports no shim at all.
//
// Injection failing is fatal on purpose. Starting the program without the shim is what produces the
// state this plugin exists to remove -- a program that runs for ten minutes and then cannot save,
// with nothing in any log to say why. A launch that fails loudly is a state someone can fix.

#include <windows.h>
#include <tlhelp32.h>
#include <stdio.h>
#include <string>

namespace {

constexpr wchar_t ShimDll[] = L"PolyMount.TxFShim.dll";
constexpr DWORD InjectionTimeoutMs = 30000;

using NtSuspendProcess_t = LONG(NTAPI*)(HANDLE);
using NtResumeProcess_t = LONG(NTAPI*)(HANDLE);
using NtCreateThreadEx_t = LONG(NTAPI*)(HANDLE*, ACCESS_MASK, LPVOID, HANDLE, LPVOID, LPVOID,
    ULONG, SIZE_T, SIZE_T, SIZE_T, LPVOID);

/// <summary>What the launcher did, next to the shim's own log. A launcher that fails silently is
/// the hardest state to diagnose: the program runs, and the only symptom is the error the shim
/// exists to prevent.</summary>
void Trace(const wchar_t* message, DWORD detail)
{
    wchar_t directory[MAX_PATH];
    DWORD length = GetEnvironmentVariableW(L"ProgramData", directory, ARRAYSIZE(directory));
    if (length == 0 || length >= ARRAYSIZE(directory)) return;
    wchar_t file[MAX_PATH];
    if (swprintf_s(file, L"%s\\PolyMount\\txf-shim\\launcher.log", directory) < 0) return;

    HANDLE handle = CreateFileW(file, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
        OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (handle == INVALID_HANDLE_VALUE) return;
    wchar_t line[1024];
    int written = swprintf_s(line, L"pid=%lu %s (detail %lu)\r\n", GetCurrentProcessId(), message, detail);
    if (written > 0)
    {
        char utf8[2048];
        int bytes = WideCharToMultiByte(CP_UTF8, 0, line, written, utf8, sizeof(utf8), nullptr, nullptr);
        DWORD ignored = 0;
        if (bytes > 0) WriteFile(handle, utf8, (DWORD)bytes, &ignored, nullptr);
    }
    CloseHandle(handle);
}

void AppendArgument(std::wstring& out, const wchar_t* argument)
{
    if (!out.empty()) out.push_back(L' ');
    if (*argument != L'\0' && wcspbrk(argument, L" \t\n\v\"") == nullptr)
    {
        out.append(argument);
        return;
    }

    // The quoting rules CommandLineToArgvW applies, so the program receives the arguments it would
    // have received without a launcher in between.
    out.push_back(L'"');
    for (const wchar_t* p = argument;; ++p)
    {
        size_t backslashes = 0;
        while (*p == L'\\') { ++p; ++backslashes; }
        if (*p == L'\0') { out.append(backslashes * 2, L'\\'); break; }
        if (*p == L'"') { out.append(backslashes * 2 + 1, L'\\'); out.push_back(L'"'); }
        else { out.append(backslashes, L'\\'); out.push_back(*p); }
    }
    out.push_back(L'"');
}

std::wstring DirectoryOfThisExecutable()
{
    wchar_t path[MAX_PATH];
    DWORD length = GetModuleFileNameW(nullptr, path, ARRAYSIZE(path));
    if (length == 0 || length >= ARRAYSIZE(path)) return {};
    wchar_t* slash = wcsrchr(path, L'\\');
    if (slash == nullptr) return {};
    *slash = L'\0';
    return path;
}

/// <summary>Where a module sits in another process.</summary>
HMODULE RemoteModuleBase(DWORD processId, const wchar_t* name)
{
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, processId);
    if (snapshot == INVALID_HANDLE_VALUE) return nullptr;
    MODULEENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    HMODULE base = nullptr;
    if (Module32FirstW(snapshot, &entry))
        do
        {
            if (_wcsicmp(entry.szModule, name) == 0) { base = (HMODULE)entry.modBaseAddr; break; }
        } while (Module32NextW(snapshot, &entry));
    CloseHandle(snapshot);
    return base;
}

bool IsLoaded(DWORD processId, const wchar_t* name)
{
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, processId);
    if (snapshot == INVALID_HANDLE_VALUE) return false;
    MODULEENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    bool found = false;
    if (Module32FirstW(snapshot, &entry))
        do
        {
            if (_wcsicmp(entry.szModule, name) == 0) { found = true; break; }
        } while (Module32NextW(snapshot, &entry));
    CloseHandle(snapshot);
    return found;
}

/// <summary>
/// The address a procedure has in the child. GetProcAddress on kernel32 often answers with a
/// forwarder target in kernelbase, so the module that owns the address is asked for by name rather
/// than assumed to be the one the procedure was asked of.
/// </summary>
FARPROC RemoteProcedure(DWORD processId, const wchar_t* module, const char* procedure)
{
    FARPROC local = GetProcAddress(GetModuleHandleW(module), procedure);
    if (local == nullptr) return nullptr;

    HMODULE owner = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            (LPCWSTR)local, &owner) || owner == nullptr)
        return nullptr;

    wchar_t ownerPath[MAX_PATH];
    if (GetModuleFileNameW(owner, ownerPath, ARRAYSIZE(ownerPath)) == 0) return nullptr;
    const wchar_t* ownerName = wcsrchr(ownerPath, L'\\');
    ownerName = ownerName ? ownerName + 1 : ownerPath;

    HMODULE remote = RemoteModuleBase(processId, ownerName);
    if (remote == nullptr) return nullptr;
    return (FARPROC)((BYTE*)remote + ((BYTE*)local - (BYTE*)owner));
}

/// <summary>Where the image's entry point sits in the child, and the byte that is there now. Read
/// from the child rather than from the file: the loader has already mapped it.</summary>
bool BreakAtEntryPoint(LPVOID imageBase, HANDLE process, LPVOID* entry, BYTE* original)
{
    IMAGE_DOS_HEADER dos{};
    if (!ReadProcessMemory(process, imageBase, &dos, sizeof(dos), nullptr)) return false;
    if (dos.e_magic != IMAGE_DOS_SIGNATURE) return false;

    IMAGE_NT_HEADERS64 headers{};
    if (!ReadProcessMemory(process, (BYTE*)imageBase + dos.e_lfanew, &headers, sizeof(headers), nullptr)) return false;
    if (headers.Signature != IMAGE_NT_SIGNATURE) return false;

    *entry = (BYTE*)imageBase + headers.OptionalHeader.AddressOfEntryPoint;
    return ReadProcessMemory(process, *entry, original, 1, nullptr) != FALSE;
}

/// <summary>Writes the path into the child and starts a suspended thread whose routine is
/// LoadLibraryW. The thread is created suspended so that it can be released after the debug event
/// has been answered and the program has been stopped again.</summary>
bool PrepareInjection(HANDLE process, DWORD processId, const std::wstring& dll,
    HANDLE* thread, void** remoteString)
{
    *thread = nullptr;
    *remoteString = nullptr;

    SIZE_T bytes = (dll.size() + 1) * sizeof(wchar_t);
    void* remote = VirtualAllocEx(process, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (remote == nullptr) { Trace(L"VirtualAllocEx failed", GetLastError()); return false; }
    if (!WriteProcessMemory(process, remote, dll.c_str(), bytes, nullptr))
    {
        Trace(L"WriteProcessMemory failed", GetLastError());
        VirtualFreeEx(process, remote, 0, MEM_RELEASE);
        return false;
    }

    FARPROC loadLibrary = RemoteProcedure(processId, L"kernel32.dll", "LoadLibraryW");
    if (loadLibrary == nullptr) { Trace(L"no LoadLibraryW in the child", 0); return false; }

    auto create = (NtCreateThreadEx_t)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtCreateThreadEx");
    if (create == nullptr) { Trace(L"no NtCreateThreadEx", 0); return false; }

    HANDLE started = nullptr;
    LONG status = create(&started, THREAD_ALL_ACCESS, nullptr, process,
        (LPVOID)loadLibrary, (LPVOID)remote, CREATE_SUSPENDED, 0, 0, 0, nullptr);
    if (status != 0 || started == nullptr)
    {
        Trace(L"NtCreateThreadEx failed", (DWORD)status);
        return false;
    }

    *thread = started;
    *remoteString = remote;
    return true;
}

} // namespace

int wmain(int argc, wchar_t** argv)
{
    if (argc < 2)
    {
        fwprintf(stderr, L"usage: %s <program> [arguments...]\n", argc > 0 ? argv[0] : L"PolyMount.TxFShim");
        return 2;
    }

    std::wstring dll = DirectoryOfThisExecutable();
    if (dll.empty())
    {
        fwprintf(stderr, L"PolyMount.TxFShim: cannot locate its own directory\n");
        return 3;
    }
    dll += L'\\';
    dll += ShimDll;
    if (GetFileAttributesW(dll.c_str()) == INVALID_FILE_ATTRIBUTES)
    {
        fwprintf(stderr, L"PolyMount.TxFShim: '%s' is missing; refusing to start without it\n", dll.c_str());
        return 4;
    }

    std::wstring commandLine;
    for (int index = 1; index < argc; index++) AppendArgument(commandLine, argv[index]);

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(argv[1], commandLine.data(), nullptr, nullptr, FALSE,
            DEBUG_ONLY_THIS_PROCESS, nullptr, nullptr, &startup, &process))
    {
        Trace(L"create failed", GetLastError());
        fwprintf(stderr, L"PolyMount.TxFShim: cannot start '%s' (error %lu)\n", argv[1], GetLastError());
        return 5;
    }
    Trace(L"created child", process.dwProcessId);

    auto suspend = (NtSuspendProcess_t)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtSuspendProcess");
    auto resume = (NtResumeProcess_t)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtResumeProcess");

    bool injected = false;
    bool onTarget = false;
    HANDLE entryThread = nullptr;
    LPVOID entryAddress = nullptr;
    BYTE entryByte = 0;

    for (int guard = 0; guard < 4096; guard++)
    {
        DEBUG_EVENT event{};
        if (!WaitForDebugEvent(&event, InjectionTimeoutMs)) break;

        if (event.dwDebugEventCode == CREATE_PROCESS_DEBUG_EVENT)
        {
            // The debuggee is stopped before its loader has run, so this is where a breakpoint at
            // the entry point can be placed.
            if (BreakAtEntryPoint(event.u.CreateProcessInfo.lpBaseOfImage, process.hProcess,
                    &entryAddress, &entryByte))
                WriteProcessMemory(process.hProcess, entryAddress, "\xCC", 1, nullptr);
            entryThread = OpenThread(THREAD_GET_CONTEXT | THREAD_SET_CONTEXT, FALSE, event.dwThreadId);
            ContinueDebugEvent(event.dwProcessId, event.dwThreadId, DBG_CONTINUE);
            continue;
        }

        if (event.dwDebugEventCode == EXCEPTION_DEBUG_EVENT &&
            event.u.Exception.ExceptionRecord.ExceptionCode == EXCEPTION_BREAKPOINT &&
            entryAddress != nullptr &&
            event.u.Exception.ExceptionRecord.ExceptionAddress == entryAddress)
        {
            onTarget = true;
            // The breakpoint was ours: put the byte back and step onto it, so the program never
            // finds it.
            WriteProcessMemory(process.hProcess, entryAddress, &entryByte, 1, nullptr);
            if (entryThread != nullptr)
            {
                CONTEXT context{};
                context.ContextFlags = CONTEXT_CONTROL;
                if (GetThreadContext(entryThread, &context))
                {
                    context.Rip -= 1;
                    SetThreadContext(entryThread, &context);
                }
            }

            void* remoteString = nullptr;
            HANDLE injectionThread = nullptr;
            bool prepared = PrepareInjection(process.hProcess, process.dwProcessId, dll,
                &injectionThread, &remoteString);

            // Answering the debug event is what un-freezes the program's threads, so they are
            // suspended first: the library has to be in place before the first instruction.
            if (suspend != nullptr) suspend(process.hProcess);
            ContinueDebugEvent(event.dwProcessId, event.dwThreadId, DBG_CONTINUE);
            DebugActiveProcessStop(process.dwProcessId);

            if (prepared)
            {
                ResumeThread(injectionThread);
                WaitForSingleObject(injectionThread, InjectionTimeoutMs);
                CloseHandle(injectionThread);
            }

            if (resume != nullptr) resume(process.hProcess);

            // The answer is about the module, not about the thread: LoadLibrary can fail on its own,
            // and a program without it will fail later instead, which is the whole thing this
            // exists to prevent.
            for (int spin = 0; spin < 200 && !IsLoaded(process.dwProcessId, L"PolyMount.TxFShim.dll"); spin++)
                Sleep(10);
            injected = IsLoaded(process.dwProcessId, L"PolyMount.TxFShim.dll");

            if (remoteString != nullptr) VirtualFreeEx(process.hProcess, remoteString, 0, MEM_RELEASE);
            break;
        }

        ContinueDebugEvent(event.dwProcessId, event.dwThreadId, DBG_CONTINUE);
    }

    if (entryThread != nullptr) CloseHandle(entryThread);
    Trace(onTarget ? L"entry breakpoint reached" : L"entry breakpoint missed", 0);
    Trace(injected ? L"injected" : L"injection failed", GetLastError());

    if (!injected)
    {
        fwprintf(stderr, L"PolyMount.TxFShim: could not instrument '%s'; not starting it\n", argv[1]);
        TerminateProcess(process.hProcess, 1);
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        return 6;
    }

    CloseHandle(process.hThread);
    WaitForSingleObject(process.hProcess, INFINITE);
    DWORD exitCode = 0;
    GetExitCodeProcess(process.hProcess, &exitCode);
    CloseHandle(process.hProcess);
    return (int)exitCode;
}
