#include "log.h"

#ifdef LOG_ENABLED

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdarg.h>

static FILE* g_log = nullptr;

void dbg(const char* fmt, ...) {
    char buf[1024];
    va_list ap; va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    OutputDebugStringA(buf);
    if (g_log) { fputs(buf, g_log); fflush(g_log); }
}

void open_log() {
    wchar_t tempDir[MAX_PATH];
    DWORD len = GetTempPathW(MAX_PATH, tempDir);
    if (len == 0 || len > MAX_PATH) return;
    wchar_t path[MAX_PATH];
    if (swprintf(path, MAX_PATH, L"%stalon-boot.log", tempDir) < 0) return;
    g_log = _wfopen(path, L"a");
}

#endif // LOG_ENABLED
