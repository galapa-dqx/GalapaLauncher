// Talon.Boot — Phase 0 injected payload (no-op stub).
//
// This is the native DLL that Talon.Injector loads into the target process via
// an early-bird APC. In later phases this file grows into the CLR bootstrap
// (hostfxr) that hands off to the managed Talon framework. For Phase 0 its only
// job is to prove — from inside the 32-bit game process — that injection worked.
//
// Loader-lock discipline: DllMain runs under the loader lock, so it does the
// bare minimum. Anything heavy (starting the CLR, spawning worker threads) must
// happen off this path in a later phase. The single log write here is safe.

#include <windows.h>
#include <cstdio>

namespace
{
    // Writes one proof-of-life line to %TEMP%\talon-boot.log (absolute path, so
    // it never depends on the target's current working directory).
    void WriteProofOfLife(const char* reason)
    {
        wchar_t tempDir[MAX_PATH];
        const DWORD len = GetTempPathW(MAX_PATH, tempDir);
        if (len == 0 || len > MAX_PATH)
            return;

        wchar_t logPath[MAX_PATH];
        if (swprintf(logPath, MAX_PATH, L"%stalon-boot.log", tempDir) < 0)
            return;

        const HANDLE file = CreateFileW(
            logPath,
            FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr,
            OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (file == INVALID_HANDLE_VALUE)
            return;

        SYSTEMTIME st;
        GetSystemTime(&st);

        char line[256];
        const int n = snprintf(
            line, sizeof(line),
            "[%04u-%02u-%02u %02u:%02u:%02u.%03uZ] Talon.Boot loaded (pid=%lu, %s)\r\n",
            st.wYear, st.wMonth, st.wDay,
            st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
            GetCurrentProcessId(),
            reason);
        if (n > 0)
        {
            DWORD written = 0;
            WriteFile(file, line, static_cast<DWORD>(n), &written, nullptr);
        }

        CloseHandle(file);

        // Secondary signal, visible in DebugView without touching the filesystem.
        OutputDebugStringA(line);
    }
}

// Explicit hand-off entrypoint. Not required for Phase 0 (the DllMain log below
// already proves the load), but shipped now because it is the export the CLR
// bootstrap and entrypoint-rewrite path will call in Phase 1.
extern "C" __declspec(dllexport) void TalonInit()
{
    WriteProofOfLife("TalonInit");
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID /*reserved*/)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(hModule);
        WriteProofOfLife("DllMain");
    }
    return TRUE;
}
