// Talon.Recon — dynamic recon payload.
//
// Injected by the existing Talon.Injector (`--boot-dll Talon.Recon.dll`) via early-bird
// APC, so it is loaded before DQXGame.exe reaches its entry point and therefore beats the
// game to its first file access.
//
// Purpose: find the game's higher-level resource loader. We hook the low-level file I/O
// (the version-proof surface) and record, for every read against a .dat/.idx handle, a RAW
// dump of the stack. We deliberately do ZERO interpretation in-process: no deciding which
// DWORDs are return addresses, no call-site validation. Those are judgement calls, and
// judgement calls belong offline where they are free, re-runnable against one capture, and
// inspectable. A wrong heuristic compiled into this DLL would silently produce a clean-
// looking report missing the very frames we need.
//
// Why inline hooks: DQXGame.exe imports NO file APIs — it resolves CreateFileW/ReadFile via
// GetProcAddress at runtime — so there is no IAT to patch. We must hook kernel32/ntdll in
// place, which is exactly what dragonhook does.
//
// Outputs (both next to %TEMP%):
//   talon-recon-opens.log   text, every file the game opens
//   talon-recon-stacks.bin  binary, raw stack dumps for .dat/.idx reads
//
// RESULT (8.0.1): it worked — 197 stack records over 61 .dat/.idx opens produced a call
// chain that was stable across reads, and RtlCaptureStackBackTrace turned out to give
// ordered frames (the game is not FPO-hostile), so stack scanning was never needed. The
// chain resolved to the game's archive layer; the resource loader sits at RVA +0xFD0E0
// (idx lookup -> block*0x80 -> fseek/fread -> decompress). Rerun this after a patch to
// re-locate it: the loader is anchored by its own error strings, "ERROR: readerror0 %x"
// through "readerror5 %x, %x, %x", and by "../Ex%d000/Data/" one frame above.

#include <windows.h>
#include <winternl.h>
#include <intrin.h>
#include <cstdio>
#include <cstdint>
#include "MinHook.h"

namespace
{
    // ---- capture bounds -------------------------------------------------------------
    constexpr uint32_t kStackBytes  = 4096;   // raw stack captured per read
    constexpr uint32_t kMaxRecords  = 2000;   // ~8MB; enough to separate the real chain
                                              // from stale frames statistically
    constexpr uint32_t kMaxRtlFrames = 62;

    // ---- state ----------------------------------------------------------------------
    HANDLE           g_openLog   = INVALID_HANDLE_VALUE;
    HANDLE           g_stackLog  = INVALID_HANDLE_VALUE;
    CRITICAL_SECTION g_lock;
    bool             g_ready     = false;
    volatile LONG    g_records   = 0;

    uintptr_t g_moduleBase = 0;
    uint32_t  g_moduleSize = 0;

    // Handles opened on .dat/.idx archives. Small fixed table — avoids any allocation on
    // the I/O path, and the game only keeps a handful of archives open.
    constexpr int kMaxTracked = 64;
    HANDLE g_tracked[kMaxTracked] = {};

    using PFN_RtlCaptureStackBackTrace =
        USHORT (WINAPI*)(ULONG, ULONG, PVOID*, PULONG);
    PFN_RtlCaptureStackBackTrace g_rtlCapture = nullptr;

    // ---- originals ------------------------------------------------------------------
    using PFN_CreateFileW = HANDLE (WINAPI*)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES,
                                             DWORD, DWORD, HANDLE);
    using PFN_CreateFileA = HANDLE (WINAPI*)(LPCSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES,
                                             DWORD, DWORD, HANDLE);
    using PFN_ReadFile    = BOOL (WINAPI*)(HANDLE, LPVOID, DWORD, LPDWORD, LPOVERLAPPED);
    using PFN_CloseHandle = BOOL (WINAPI*)(HANDLE);
    using PFN_NtReadFile  = NTSTATUS (NTAPI*)(HANDLE, HANDLE, PIO_APC_ROUTINE, PVOID,
                                              PIO_STATUS_BLOCK, PVOID, ULONG,
                                              PLARGE_INTEGER, PULONG);
    // DQX opens its archives through the Nt* layer directly, bypassing kernel32 entirely
    // (kernel32's exports are only forwarder thunks into KernelBase anyway). Hooking
    // CreateFileW alone sees nothing but loader/CRT traffic.
    using PFN_NtCreateFile = NTSTATUS (NTAPI*)(PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES,
                                               PIO_STATUS_BLOCK, PLARGE_INTEGER, ULONG,
                                               ULONG, ULONG, ULONG, PVOID, ULONG);
    using PFN_NtOpenFile   = NTSTATUS (NTAPI*)(PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES,
                                               PIO_STATUS_BLOCK, ULONG, ULONG);

    PFN_CreateFileW  o_CreateFileW  = nullptr;
    PFN_CreateFileA  o_CreateFileA  = nullptr;
    PFN_ReadFile     o_ReadFile     = nullptr;
    PFN_CloseHandle  o_CloseHandle  = nullptr;
    PFN_NtReadFile   o_NtReadFile   = nullptr;
    PFN_NtCreateFile o_NtCreateFile = nullptr;
    PFN_NtOpenFile   o_NtOpenFile   = nullptr;

    volatile LONG g_ntReadCalls = 0;   // total NtReadFile traffic, tracked or not

    // ---- helpers --------------------------------------------------------------------

    // NOTE: we never hook WriteFile, so our own logging cannot re-enter our hooks.
    // Log handles are also opened before any hook is enabled.
    void WriteText(HANDLE h, const char* s)
    {
        if (h == INVALID_HANDLE_VALUE) return;
        DWORD w = 0;
        WriteFile(h, s, (DWORD)strlen(s), &w, nullptr);
    }

    bool IsArchivePathW(const wchar_t* p)
    {
        if (!p) return false;
        // match .dat / .datN / .idx anywhere (archives are data########.win32.dat0 etc.)
        for (const wchar_t* c = p; *c; ++c)
        {
            if ((c[0] == L'.') &&
                ((c[1] == L'd' && c[2] == L'a' && c[3] == L't') ||
                 (c[1] == L'i' && c[2] == L'd' && c[3] == L'x')))
                return true;
        }
        return false;
    }

    void Track(HANDLE h)
    {
        for (int i = 0; i < kMaxTracked; ++i)
            if (g_tracked[i] == nullptr) { g_tracked[i] = h; return; }
    }

    bool IsTracked(HANDLE h)
    {
        for (int i = 0; i < kMaxTracked; ++i)
            if (g_tracked[i] == h) return true;
        return false;
    }

    void Untrack(HANDLE h)
    {
        for (int i = 0; i < kMaxTracked; ++i)
            if (g_tracked[i] == h) { g_tracked[i] = nullptr; return; }
    }

    // Upper bound of this thread's stack, so a 4KB read can't run off the end.
    // winternl.h's TEB is a stub without NtTib, but on x86 the TIB is at FS:0 and
    // StackBase (the HIGH address the stack grows down from) sits at FS:[0x04].
    uintptr_t StackTop()
    {
        return (uintptr_t)__readfsdword(0x04);
    }

    // Appends one raw record. No filtering, no interpretation — see file header.
    void RecordRead(uint32_t kind, HANDLE h, uint64_t offset, uint32_t length)
    {
        if (!g_ready) return;
        if (InterlockedIncrement(&g_records) > (LONG)kMaxRecords) return;

        // ESP at our frame; the real return addresses live above this.
        uintptr_t esp = (uintptr_t)_AddressOfReturnAddress();
        uintptr_t top = StackTop();
        uint32_t  avail = (top > esp) ? (uint32_t)(top - esp) : 0;
        uint32_t  grab  = (avail < kStackBytes) ? avail : kStackBytes;

        void*  rtl[kMaxRtlFrames] = {};
        USHORT rtlCount = 0;
        if (g_rtlCapture)
            rtlCount = g_rtlCapture(0, kMaxRtlFrames, rtl, nullptr);

        struct Rec
        {
            uint32_t magic;      // 'TLRR'
            uint32_t kind;       // 1=ReadFile 2=NtReadFile
            uint32_t tid;
            uint32_t handle;
            uint64_t offset;
            uint32_t length;
            uint32_t esp;
            uint32_t stackBytes;
            uint32_t rtlFrames;
        } rec;

        rec.magic      = 0x544C5252; // TLRR
        rec.kind       = kind;
        rec.tid        = GetCurrentThreadId();
        rec.handle     = (uint32_t)(uintptr_t)h;
        rec.offset     = offset;
        rec.length     = length;
        rec.esp        = (uint32_t)esp;
        rec.stackBytes = grab;
        rec.rtlFrames  = rtlCount;

        EnterCriticalSection(&g_lock);
        DWORD w = 0;
        WriteFile(g_stackLog, &rec, sizeof(rec), &w, nullptr);
        if (rtlCount) WriteFile(g_stackLog, rtl, rtlCount * sizeof(void*), &w, nullptr);
        if (grab)     WriteFile(g_stackLog, (void*)esp, grab, &w, nullptr);
        LeaveCriticalSection(&g_lock);
    }

    // ---- hooks ----------------------------------------------------------------------

    HANDLE WINAPI h_CreateFileW(LPCWSTR name, DWORD access, DWORD share,
                                LPSECURITY_ATTRIBUTES sa, DWORD disp, DWORD flags, HANDLE tmpl)
    {
        HANDLE h = o_CreateFileW(name, access, share, sa, disp, flags, tmpl);
        if (g_ready && name)
        {
            char line[1024];
            _snprintf(line, sizeof(line), "OPEN  h=%08X  %ls\r\n", (unsigned)(uintptr_t)h, name);
            EnterCriticalSection(&g_lock);
            WriteText(g_openLog, line);
            if (h != INVALID_HANDLE_VALUE && IsArchivePathW(name)) Track(h);
            LeaveCriticalSection(&g_lock);
        }
        return h;
    }

    HANDLE WINAPI h_CreateFileA(LPCSTR name, DWORD access, DWORD share,
                                LPSECURITY_ATTRIBUTES sa, DWORD disp, DWORD flags, HANDLE tmpl)
    {
        HANDLE h = o_CreateFileA(name, access, share, sa, disp, flags, tmpl);
        if (g_ready && name)
        {
            char line[1024];
            _snprintf(line, sizeof(line), "OPENA h=%08X  %s\r\n", (unsigned)(uintptr_t)h, name);
            wchar_t wide[512] = {};
            MultiByteToWideChar(CP_ACP, 0, name, -1, wide, 511);
            EnterCriticalSection(&g_lock);
            WriteText(g_openLog, line);
            if (h != INVALID_HANDLE_VALUE && IsArchivePathW(wide)) Track(h);
            LeaveCriticalSection(&g_lock);
        }
        return h;
    }

    BOOL WINAPI h_ReadFile(HANDLE h, LPVOID buf, DWORD count, LPDWORD read, LPOVERLAPPED ov)
    {
        bool tracked = false;
        if (g_ready)
        {
            EnterCriticalSection(&g_lock);
            tracked = IsTracked(h);
            LeaveCriticalSection(&g_lock);
        }

        if (tracked)
        {
            // Current file position (ReadFile uses the implicit pointer unless overlapped).
            uint64_t off = 0;
            if (ov)
            {
                off = ((uint64_t)ov->OffsetHigh << 32) | ov->Offset;
            }
            else
            {
                LARGE_INTEGER zero{}, cur{};
                if (SetFilePointerEx(h, zero, &cur, FILE_CURRENT)) off = (uint64_t)cur.QuadPart;
            }
            RecordRead(1, h, off, count);
        }
        return o_ReadFile(h, buf, count, read, ov);
    }

    // Logs an Nt-level open and tracks the handle if it's an archive.
    void NoteNtOpen(const char* which, PHANDLE outHandle, POBJECT_ATTRIBUTES oa, NTSTATUS st)
    {
        if (!g_ready || !oa || !oa->ObjectName || !oa->ObjectName->Buffer) return;

        // UNICODE_STRING is NOT null-terminated; copy with an explicit length.
        wchar_t path[512];
        USHORT chars = oa->ObjectName->Length / sizeof(wchar_t);
        if (chars > 511) chars = 511;
        memcpy(path, oa->ObjectName->Buffer, chars * sizeof(wchar_t));
        path[chars] = L'\0';

        HANDLE h = (NT_SUCCESS(st) && outHandle) ? *outHandle : INVALID_HANDLE_VALUE;

        char line[1024];
        _snprintf(line, sizeof(line), "%s h=%08X st=%08X  %ls\r\n",
                  which, (unsigned)(uintptr_t)h, (unsigned)st, path);

        EnterCriticalSection(&g_lock);
        WriteText(g_openLog, line);
        if (h != INVALID_HANDLE_VALUE && IsArchivePathW(path)) Track(h);
        LeaveCriticalSection(&g_lock);
    }

    NTSTATUS NTAPI h_NtCreateFile(PHANDLE fh, ACCESS_MASK access, POBJECT_ATTRIBUTES oa,
                                  PIO_STATUS_BLOCK iosb, PLARGE_INTEGER alloc, ULONG attrs,
                                  ULONG share, ULONG disp, ULONG opts, PVOID ea, ULONG eaLen)
    {
        NTSTATUS st = o_NtCreateFile(fh, access, oa, iosb, alloc, attrs, share, disp, opts, ea, eaLen);
        NoteNtOpen("NTCREATE", fh, oa, st);
        return st;
    }

    NTSTATUS NTAPI h_NtOpenFile(PHANDLE fh, ACCESS_MASK access, POBJECT_ATTRIBUTES oa,
                                PIO_STATUS_BLOCK iosb, ULONG share, ULONG opts)
    {
        NTSTATUS st = o_NtOpenFile(fh, access, oa, iosb, share, opts);
        NoteNtOpen("NTOPEN  ", fh, oa, st);
        return st;
    }

    NTSTATUS NTAPI h_NtReadFile(HANDLE h, HANDLE evt, PIO_APC_ROUTINE apc, PVOID apcCtx,
                                PIO_STATUS_BLOCK iosb, PVOID buf, ULONG len,
                                PLARGE_INTEGER byteOffset, PULONG key)
    {
        // Traffic census: proves whether the game's reads reach ntdll's exported stub at
        // all (vs. bypassing it via direct syscalls). Logged sparsely to stay cheap.
        LONG n = InterlockedIncrement(&g_ntReadCalls);
        if (g_ready && (n & 0xFF) == 1)
        {
            char l[96];
            _snprintf(l, sizeof(l), "# NtReadFile traffic: %ld calls\r\n", n);
            EnterCriticalSection(&g_lock);
            WriteText(g_openLog, l);
            LeaveCriticalSection(&g_lock);
        }

        bool tracked = false;
        if (g_ready)
        {
            EnterCriticalSection(&g_lock);
            tracked = IsTracked(h);
            LeaveCriticalSection(&g_lock);
        }
        if (tracked)
            RecordRead(2, h, byteOffset ? (uint64_t)byteOffset->QuadPart : 0ull, len);

        return o_NtReadFile(h, evt, apc, apcCtx, iosb, buf, len, byteOffset, key);
    }

    BOOL WINAPI h_CloseHandle(HANDLE h)
    {
        if (g_ready)
        {
            EnterCriticalSection(&g_lock);
            Untrack(h);
            LeaveCriticalSection(&g_lock);
        }
        return o_CloseHandle(h);
    }

    // ---- init -----------------------------------------------------------------------

    HANDLE OpenLog(const wchar_t* leaf)
    {
        wchar_t dir[MAX_PATH], path[MAX_PATH];
        if (!GetTempPathW(MAX_PATH, dir)) return INVALID_HANDLE_VALUE;
        _snwprintf(path, MAX_PATH, L"%s%s", dir, leaf);
        return CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, nullptr,
                           CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    }

    void WriteStackHeader()
    {
        // Everything downstream rebases against this: raw stack values are absolute and
        // meaningless under ASLR without the module base.
        struct Hdr
        {
            uint32_t magic;   // 'TLRC'
            uint32_t version;
            uint32_t base;
            uint32_t size;
            uint32_t stackBytes;
        } hdr;
        hdr.magic      = 0x544C5243;
        hdr.version    = 1;
        hdr.base       = (uint32_t)g_moduleBase;
        hdr.size       = g_moduleSize;
        hdr.stackBytes = kStackBytes;
        DWORD w = 0;
        WriteFile(g_stackLog, &hdr, sizeof(hdr), &w, nullptr);
    }

    bool Init()
    {
        InitializeCriticalSection(&g_lock);

        HMODULE exe = GetModuleHandleW(nullptr);
        g_moduleBase = (uintptr_t)exe;
        auto dos = (PIMAGE_DOS_HEADER)exe;
        auto nt  = (PIMAGE_NT_HEADERS)((uint8_t*)exe + dos->e_lfanew);
        g_moduleSize = nt->OptionalHeader.SizeOfImage;

        HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        g_rtlCapture = (PFN_RtlCaptureStackBackTrace)
            GetProcAddress(ntdll, "RtlCaptureStackBackTrace");

        // Opened BEFORE hooks are enabled, so these calls are not intercepted.
        g_openLog  = OpenLog(L"talon-recon-opens.log");
        g_stackLog = OpenLog(L"talon-recon-stacks.bin");
        if (g_stackLog == INVALID_HANDLE_VALUE) return false;
        WriteStackHeader();

        char banner[512];
        _snprintf(banner, sizeof(banner),
                  "# Talon.Recon  pid=%lu  base=%08X  size=%08X\r\n",
                  GetCurrentProcessId(), (unsigned)g_moduleBase, g_moduleSize);
        WriteText(g_openLog, banner);

        if (MH_Initialize() != MH_OK) return false;

        HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
        struct { HMODULE m; const char* n; void* d; void** o; } hooks[] = {
            { k32,   "CreateFileW", (void*)&h_CreateFileW, (void**)&o_CreateFileW },
            { k32,   "CreateFileA", (void*)&h_CreateFileA, (void**)&o_CreateFileA },
            { k32,   "ReadFile",    (void*)&h_ReadFile,    (void**)&o_ReadFile    },
            { k32,   "CloseHandle", (void*)&h_CloseHandle, (void**)&o_CloseHandle },
            { ntdll, "NtReadFile",   (void*)&h_NtReadFile,   (void**)&o_NtReadFile   },
            { ntdll, "NtCreateFile", (void*)&h_NtCreateFile, (void**)&o_NtCreateFile },
            { ntdll, "NtOpenFile",   (void*)&h_NtOpenFile,   (void**)&o_NtOpenFile   },
        };

        // Record exactly WHERE each hook lands, rather than assuming. Probing an address we
        // *believed* was hooked, instead of making the payload report the truth, is what
        // cost hours here: every E9 we "verified" turned out to be dragonhook's, not ours.
        // Log the resolved target and the bytes before/after so a later probe knows.
        void* targets[8] = {};
        int   nTargets = 0;

        for (auto& hk : hooks)
        {
            void* target = (void*)GetProcAddress(hk.m, hk.n);
            if (!target) continue;

            // CRITICAL: follow FF 25 (jmp dword ptr [addr]) forwarder thunks to the real
            // implementation before hooking. kernel32's exports are only thunks into
            // KernelBase, and DQX's startup stub reads pristine ntdll.dll + Kernel32.dll
            // off disk and RESTORES those two modules' code — silently erasing any hook
            // placed on the thunk. It does NOT restore KernelBase, so a hook one hop
            // deeper survives. This is exactly what dragonhook does ("after 1 hop(s)"),
            // and it is why dragonhook works in production while hooking the thunk does not.
            for (int hop = 0; hop < 4; ++hop)
            {
                auto b = (const uint8_t*)target;
                if (b[0] == 0xFF && b[1] == 0x25)          // jmp dword ptr [imm32]
                {
                    auto slot = *(void***)(b + 2);
                    if (!slot || !*slot) break;
                    target = *slot;
                }
                else if (b[0] == 0xE9)                     // jmp rel32 (already hooked)
                {
                    break;
                }
                else break;
            }

            uint8_t before[8];
            memcpy(before, target, 8);

            MH_STATUS st = MH_CreateHook(target, hk.d, hk.o);
            char l[256];
            _snprintf(l, sizeof(l),
                      "# hook %-12s passed=%08X status=%d before=%02X %02X %02X %02X %02X\r\n",
                      hk.n, (unsigned)(uintptr_t)target, (int)st,
                      before[0], before[1], before[2], before[3], before[4]);
            WriteText(g_openLog, l);

            if (st == MH_OK && nTargets < 8) targets[nTargets++] = target;
        }

        if (MH_EnableHook(MH_ALL_HOOKS) != MH_OK)
        {
            WriteText(g_openLog, "# MH_EnableHook FAILED\r\n");
            return false;
        }

        for (int i = 0; i < nTargets; ++i)
        {
            uint8_t after[8];
            memcpy(after, targets[i], 8);
            char l[192];
            _snprintf(l, sizeof(l),
                      "# after-enable %08X = %02X %02X %02X %02X %02X  (%s)\r\n",
                      (unsigned)(uintptr_t)targets[i],
                      after[0], after[1], after[2], after[3], after[4],
                      after[0] == 0xE9 ? "OURS (E9)" : "NOT PATCHED HERE");
            WriteText(g_openLog, l);
        }

        g_ready = true;
        WriteText(g_openLog, "# hooks armed\r\n");
        return true;
    }
}

// Explicit entrypoint, kept for parity with Talon.Boot / the future CLR bootstrap.
extern "C" __declspec(dllexport) void TalonInit()
{
    // no-op: recon arms itself from DllMain (see below)
}

BOOL APIENTRY DllMain(HMODULE mod, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(mod);
        // Armed inline, under loader lock, ON PURPOSE: we are injected pre-entry-point, so
        // deferring to a worker thread would race the game's first .dat reads — the exact
        // reads we exist to observe. At this moment the process is effectively
        // single-threaded, so MinHook's thread-freeze is trivial. This mirrors dragonhook,
        // which also installs its file hooks from DllMain on this same game.
        Init();
    }
    return TRUE;
}
