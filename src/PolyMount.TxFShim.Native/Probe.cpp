// PolyMount.TxFShim.Probe.exe -- exercise the transactional file APIs and say what happened.
//
// This is the check the shim is judged by: run it directly against a folder on a mount that does
// not advertise transactions and every step fails; run it through PolyMount.TxFShim.exe and the
// same steps succeed. It is built beside the shim and never shipped with the plugin.
//
// The transaction is created here and passed in, which is the only shape the API has: the eighth
// argument of CreateFileTransactedW is a handle the caller already owns, and there is no way to ask
// the call to make one. Every step takes that same handle, which is what a program saving a
// document through a transaction does.

#include <windows.h>
#include <ktmw32.h>
#include <stdio.h>

#pragma comment(lib, "ktmw32.lib")

namespace {

HANDLE g_transaction;
int g_failures;
bool g_createFileWorked;

void Report(const wchar_t* step, BOOL ok, DWORD error = 0)
{
    if (ok) wprintf(L"%-10s ok\n", step);
    else { wprintf(L"%-10s failed: %lu\n", step, error); g_failures++; }
}

} // namespace

int wmain(int argc, wchar_t** argv)
{
    if (argc < 2)
    {
        fwprintf(stderr, L"usage: %s <folder-to-work-in>\n", argv[0]);
        return 2;
    }

    wchar_t folder[MAX_PATH];
    if (swprintf_s(folder, L"%s", argv[1]) < 0) return 2;
    wchar_t tag[64];
    swprintf_s(tag, L"%lu", GetCurrentProcessId());

    wchar_t file[MAX_PATH], directory[MAX_PATH], copy[MAX_PATH], moved[MAX_PATH], pattern[MAX_PATH];
    swprintf_s(file, L"%s\\txf-probe-%s.txt", folder, tag);
    swprintf_s(directory, L"%s\\txf-probe-dir-%s", folder, tag);
    swprintf_s(copy, L"%s\\txf-probe-copy-%s.txt", folder, tag);
    swprintf_s(moved, L"%s\\txf-probe-moved-%s.txt", folder, tag);
    swprintf_s(pattern, L"%s\\txf-probe-*-%s.txt", folder, tag);

    wprintf(L"shim: %s (pid %lu)\n",
        GetModuleHandleW(L"PolyMount.TxFShim.dll") != nullptr ? L"loaded" : L"absent", GetCurrentProcessId());

    // GetProcAddress takes a name or an ordinal packed into the pointer itself, and the shim hooks
    // that call. Read as a string, the ordinal form ends the process at start-up, before any step
    // below runs -- which is how a host that resolves an ordinal export died. What is asserted is
    // that the call returns and the name form still resolves; an ordinal that does not exist is
    // allowed to answer null.
    GetProcAddress(GetModuleHandleW(L"kernel32.dll"), MAKEINTRESOURCEA(1));
    Report(L"ordinal", GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW") != nullptr);

    g_transaction = CreateTransaction(nullptr, 0, 0, 0, 0, 0, nullptr);
    if (g_transaction == nullptr || g_transaction == INVALID_HANDLE_VALUE)
    {
        wprintf(L"CreateTransaction failed: %lu\n", GetLastError());
        return 1;
    }

    // The line the shim's test script reads: the single call the engine fails on.
    HANDLE handle = CreateFileTransactedW(file, GENERIC_READ | GENERIC_WRITE, 0, nullptr,
        CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr, g_transaction, nullptr, nullptr);
    DWORD error = GetLastError();
    if (handle == INVALID_HANDLE_VALUE)
    {
        wprintf(L"open failed: %lu\n", error);
        RollbackTransaction(g_transaction);
        CloseHandle(g_transaction);
        return 1;
    }
    DWORD written = 0;
    WriteFile(handle, "probe", 5, &written, nullptr);
    CloseHandle(handle);
    g_createFileWorked = true;
    wprintf(L"open succeeded\n");

    // The rest of the family, so "the shim covers the APIs this engine imports" is measured rather
    // than assumed.
    Report(L"mkdir", CreateDirectoryTransactedW(nullptr, directory, nullptr, g_transaction), GetLastError());
    Report(L"copy", CopyFileTransactedW(file, copy, nullptr, nullptr, nullptr, 0, g_transaction), GetLastError());
    Report(L"move", MoveFileTransactedW(copy, moved, nullptr, nullptr, 0, g_transaction), GetLastError());

    WIN32_FIND_DATAW data{};
    HANDLE found = FindFirstFileTransactedW(pattern, FindExInfoStandard, &data,
        FindExSearchNameMatch, nullptr, 0, g_transaction);
    DWORD findError = GetLastError();
    if (found == INVALID_HANDLE_VALUE) Report(L"find", FALSE, findError);
    else
    {
        int count = 0;
        do { count++; } while (FindNextFileW(found, &data));
        FindClose(found);
        // By now only the renamed copy still matches the pattern: the original never had a dash
        // before the process id, and the copy's old name is gone.
        if (count < 1) { wprintf(L"%-10s found %d, expected at least 1\n", L"find", count); g_failures++; }
        else wprintf(L"%-10s ok (%d)\n", L"find", count);
    }

    Report(L"delete", DeleteFileTransactedW(moved, g_transaction), GetLastError());
    Report(L"delete", DeleteFileTransactedW(file, g_transaction), GetLastError());
    Report(L"rmdir", RemoveDirectoryTransactedW(directory, g_transaction), GetLastError());

    if (g_failures == 0) CommitTransaction(g_transaction);
    else RollbackTransaction(g_transaction);
    CloseHandle(g_transaction);
    wprintf(L"%s\n", g_failures == 0 ? L"all ok" : L"some steps failed");
    return g_failures == 0 ? 0 : 1;
}
