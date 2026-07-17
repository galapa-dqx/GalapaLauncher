// Talon.AntiDebug — one-shot anti-tamper probe payload.
//
// Injected by the existing Talon.Injector (`--boot-dll Talon.AntiDebug.dll`) via early-bird
// APC. Purpose: empirically answer, for DQXGame.exe, two questions that decide which hooking
// primitive Talon.Boot can rely on:
//
//   1. Does the game scan/clear DEBUG REGISTERS? -> is hardware-breakpoint hooking viable?
//   2. Does the game self-repair its own .text?  -> does our inline (byte-patch) hook survive?
//
// Both are observable on a PLACEHOLDER session: unpacking and anti-tamper run during boot,
// independent of login, so no real account is ever authenticated (and a crash on the menu is
// harmless). This payload only observes — it installs no production hook.
//
// Probe target is Vfs_LoadResource at module RVA 0xFD0E0 (BN addr 0x5ad0e0 - image base
// 0x4b0000). Because the game is packed, the function is not present at that address until the
// packer unpacks it, so we poll for its prologue signature first.
//
// Output: %TEMP%\talon-antidebug.log (text, one line per event, flushed each write).
//
// Phases run sequentially, each logging its verdict BEFORE the next (riskier) phase, so a
// later crash never loses an earlier answer:
//   Phase 1  debug-register persistence on a private victim thread          (safe)
//   Phase 2  patch Vfs_LoadResource[0] = 0xCC and watch for self-repair     (recoverable)
//   Phase 3  arm a HW execute BP on every game thread + VEH fire detection  (invasive)

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <intrin.h>
#include <stdio.h>
#include <stdarg.h>
#include <stdint.h>

namespace
{
    // ── logging ───────────────────────────────────────────────────────────────
    FILE* g_log = nullptr;

    void Log(const char* fmt, ...)
    {
        SYSTEMTIME st; GetSystemTime(&st);
        char msg[1024];
        va_list ap; va_start(ap, fmt);
        vsnprintf(msg, sizeof(msg), fmt, ap);
        va_end(ap);

        char line[1152];
        int n = snprintf(line, sizeof(line), "[%02u:%02u:%02u.%03u] %s\r\n",
                         st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, msg);
        OutputDebugStringA(line);
        if (g_log && n > 0) { fputs(line, g_log); fflush(g_log); }
    }

    void OpenLog()
    {
        wchar_t dir[MAX_PATH], path[MAX_PATH];
        if (!GetTempPathW(MAX_PATH, dir)) return;
        _snwprintf(path, MAX_PATH, L"%stalon-antidebug.log", dir);
        g_log = _wfopen(path, L"a");
    }

    // ── module / target ─────────────────────────────────────────────────────────
    constexpr uint32_t kVfsRva = 0xFD0E0;

    uintptr_t g_base   = 0;
    uint32_t  g_size   = 0;
    uintptr_t g_target = 0;   // runtime VA of Vfs_LoadResource

    // Prologue signature (-1 = wildcard):
    // 53 8B DC 83 ?? ?? 83 ?? ?? 83 ?? ?? 55 8B ?? ?? 89 ?? ?? ?? 8B EC B8 ?? ?? ?? ?? E8
    const int kSig[] = {
        0x53, 0x8B, 0xDC, 0x83, -1, -1, 0x83, -1, -1, 0x83, -1, -1,
        0x55, 0x8B, -1, -1, 0x89, -1, -1, -1, 0x8B, 0xEC, 0xB8, -1, -1, -1, -1, 0xE8
    };
    constexpr int kSigLen = (int)(sizeof(kSig) / sizeof(kSig[0]));

    bool SigMatches(const uint8_t* p)
    {
        for (int i = 0; i < kSigLen; i++)
            if (kSig[i] >= 0 && p[i] != (uint8_t)kSig[i]) return false;
        return true;
    }

    bool RegionExecReadable(const uint8_t* addr, size_t len)
    {
        MEMORY_BASIC_INFORMATION mbi;
        const uint8_t* end = addr + len;
        while (addr < end)
        {
            if (VirtualQuery(addr, &mbi, sizeof(mbi)) == 0) return false;
            if (mbi.State != MEM_COMMIT) return false;
            DWORD prot = mbi.Protect & 0xFF;
            if (mbi.Protect & PAGE_GUARD) return false;
            if (!(prot == PAGE_EXECUTE || prot == PAGE_EXECUTE_READ ||
                  prot == PAGE_EXECUTE_READWRITE || prot == PAGE_EXECUTE_WRITECOPY))
                return false;
            addr = (const uint8_t*)mbi.BaseAddress + mbi.RegionSize;
        }
        return true;
    }

    // Poll the expected address for the prologue while the packer runs. Returns true if found.
    bool WaitForUnpack(int seconds)
    {
        for (int i = 0; i < seconds * 10; i++)
        {
            if (RegionExecReadable((const uint8_t*)g_target, kSigLen) &&
                SigMatches((const uint8_t*)g_target))
                return true;
            Sleep(100);
        }
        return false;
    }

    // ── shared VEH state ─────────────────────────────────────────────────────────
    volatile LONG g_int3Armed   = 0;   // Phase 2: 0xCC planted at g_target
    volatile LONG g_int3Exec    = 0;   // Phase 2: game executed the 0xCC (recovered)
    uint8_t       g_origByte    = 0;   // Phase 2: original g_target[0]

    volatile LONG g_hwArmed     = 0;   // Phase 3: HW exec BP active at g_target
    volatile LONG g_hwFires     = 0;   // Phase 3: times it fired
    volatile LONG g_stackDumped = 0;   // one-shot stack capture guard

    using PFN_RtlCapture = USHORT (WINAPI*)(ULONG, ULONG, PVOID*, PULONG);
    PFN_RtlCapture g_rtlCapture = nullptr;

    void DumpStack(const char* tag)
    {
        if (InterlockedExchange(&g_stackDumped, 1) != 0) return;
        if (!g_rtlCapture) { Log("  [%s] (no RtlCaptureStackBackTrace)", tag); return; }
        void* frames[48] = {};
        USHORT n = g_rtlCapture(0, 48, frames, nullptr);
        Log("  [%s] stack (%u frames), module base=%08X:", tag, n, (unsigned)g_base);
        for (USHORT i = 0; i < n; i++)
        {
            uintptr_t f = (uintptr_t)frames[i];
            if (f >= g_base && f < g_base + g_size)
                Log("    #%02u %08X  (exe+0x%X)", i, (unsigned)f, (unsigned)(f - g_base));
            else
                Log("    #%02u %08X", i, (unsigned)f);
        }
    }

    LONG CALLBACK Veh(EXCEPTION_POINTERS* ep)
    {
        auto* er = ep->ExceptionRecord;
        uintptr_t addr = (uintptr_t)er->ExceptionAddress;

        // Phase 2: our INT3 was executed. Restore the byte, rewind EIP, continue.
        if (g_int3Armed && er->ExceptionCode == EXCEPTION_BREAKPOINT && addr == g_target)
        {
            DWORD op;
            if (VirtualProtect((void*)g_target, 1, PAGE_EXECUTE_READWRITE, &op))
            {
                *(uint8_t*)g_target = g_origByte;
                FlushInstructionCache(GetCurrentProcess(), (void*)g_target, 1);
                VirtualProtect((void*)g_target, 1, op, &op);
            }
            InterlockedExchange(&g_int3Exec, 1);
            ep->ContextRecord->Eip = (DWORD)g_target;   // re-run the restored instruction
            return EXCEPTION_CONTINUE_EXECUTION;
        }

        // Phase 3: HW execute breakpoint fired at the target.
        if (g_hwArmed && er->ExceptionCode == EXCEPTION_SINGLE_STEP && addr == g_target)
        {
            InterlockedIncrement(&g_hwFires);
            DumpStack("hw-bp");
            // Disarm DR on this thread so we continue past the entry instead of re-trapping.
            ep->ContextRecord->Dr7 = 0;
            ep->ContextRecord->Dr0 = 0;
            return EXCEPTION_CONTINUE_EXECUTION;
        }

        return EXCEPTION_CONTINUE_SEARCH;
    }

    // ── debug-register helpers ───────────────────────────────────────────────────
    // DR7 for DR0 as a 1-byte EXECUTE breakpoint: L0=1 (bit 0); RW0=00, LEN0=00.
    constexpr DWORD kDr7ExecDr0 = 0x00000001;

    bool SetHwBp(HANDLE th, uintptr_t addr)
    {
        if (SuspendThread(th) == (DWORD)-1) return false;
        CONTEXT c; c.ContextFlags = CONTEXT_DEBUG_REGISTERS;
        bool ok = GetThreadContext(th, &c) != 0;
        if (ok)
        {
            c.Dr0 = addr;
            c.Dr7 = kDr7ExecDr0;
            c.ContextFlags = CONTEXT_DEBUG_REGISTERS;
            ok = SetThreadContext(th, &c) != 0;
        }
        ResumeThread(th);
        return ok;
    }

    bool ReadHwBp(HANDLE th, uintptr_t* dr0, DWORD* dr7)
    {
        if (SuspendThread(th) == (DWORD)-1) return false;
        CONTEXT c; c.ContextFlags = CONTEXT_DEBUG_REGISTERS;
        bool ok = GetThreadContext(th, &c) != 0;
        ResumeThread(th);
        if (ok) { *dr0 = (uintptr_t)c.Dr0; *dr7 = (DWORD)c.Dr7; }
        return ok;
    }

    void ClearHwBp(HANDLE th)
    {
        if (SuspendThread(th) == (DWORD)-1) return;
        CONTEXT c; c.ContextFlags = CONTEXT_DEBUG_REGISTERS;
        if (GetThreadContext(th, &c))
        {
            c.Dr0 = 0; c.Dr7 = 0;
            c.ContextFlags = CONTEXT_DEBUG_REGISTERS;
            SetThreadContext(th, &c);
        }
        ResumeThread(th);
    }

    // ── victim thread (Phase 1) ──────────────────────────────────────────────────
    volatile LONG g_victimRun = 1;
    DWORD WINAPI VictimProc(LPVOID) { while (g_victimRun) Sleep(200); return 0; }

    // ── Phase 1: debug-register persistence ──────────────────────────────────────
    void Phase1()
    {
        Log("=== Phase 1: debug-register persistence (private victim thread) ===");
        HANDLE victim = CreateThread(nullptr, 0, VictimProc, nullptr, 0, nullptr);
        if (!victim) { Log("  could not create victim thread; skipping"); return; }
        Sleep(50);

        uintptr_t sentinel = g_target ? g_target : (g_base + kVfsRva);
        bool set = SetHwBp(victim, sentinel);
        uintptr_t dr0 = 0; DWORD dr7 = 0;
        ReadHwBp(victim, &dr0, &dr7);
        Log("  set=%d readback Dr0=%08X Dr7=%08X (wanted Dr0=%08X Dr7=%08X)",
            (int)set, (unsigned)dr0, (unsigned)dr7, (unsigned)sentinel, (unsigned)kDr7ExecDr0);

        if (!(dr0 == sentinel && (dr7 & 1)))
        {
            Log("  RESULT: could NOT establish a debug register (readback mismatch) — inconclusive");
        }
        else
        {
            bool cleared = false;
            for (int i = 0; i < 240 && !cleared; i++)   // ~60s @ 250ms
            {
                Sleep(250);
                uintptr_t d0 = 0; DWORD d7 = 0;
                if (!ReadHwBp(victim, &d0, &d7)) continue;
                if (!(d0 == sentinel && (d7 & 1)))
                {
                    cleared = true;
                    Log("  DR CLEARED after ~%dms (Dr0=%08X Dr7=%08X)", i * 250, (unsigned)d0, (unsigned)d7);
                }
            }
            if (cleared)
                Log("  RESULT: debug registers ARE scanned/cleared -> HW-breakpoint hooking NOT reliable");
            else
                Log("  RESULT: debug register PERSISTED 60s -> no global DR scanner -> HW-BP hooking viable");
        }

        g_victimRun = 0;
        WaitForSingleObject(victim, 2000);
        CloseHandle(victim);
    }

    // ── Phase 2: .text self-repair / software-breakpoint scan ─────────────────────
    void Phase2()
    {
        Log("=== Phase 2: .text self-repair (patch Vfs_LoadResource[0]=0xCC) ===");
        uint8_t orig[8];
        memcpy(orig, (void*)g_target, 8);
        g_origByte = orig[0];
        Log("  target=%08X orig bytes: %02X %02X %02X %02X %02X %02X %02X %02X",
            (unsigned)g_target, orig[0], orig[1], orig[2], orig[3], orig[4], orig[5], orig[6], orig[7]);

        DWORD op;
        if (!VirtualProtect((void*)g_target, 8, PAGE_EXECUTE_READWRITE, &op))
        { Log("  VirtualProtect(RWX) failed err=%lu; skipping", GetLastError()); return; }
        InterlockedExchange(&g_int3Exec, 0);
        InterlockedExchange(&g_int3Armed, 1);
        *(uint8_t*)g_target = 0xCC;
        FlushInstructionCache(GetCurrentProcess(), (void*)g_target, 1);
        VirtualProtect((void*)g_target, 8, op, &op);
        Log("  patched target[0] 0x%02X -> 0xCC; watching for repair (~30s)", orig[0]);

        bool repaired = false;
        for (int i = 0; i < 120 && !repaired; i++)   // ~30s @ 250ms
        {
            Sleep(250);
            uint8_t cur = *(volatile uint8_t*)g_target;
            if (cur != 0xCC)
            {
                repaired = true;
                if (g_int3Exec)
                    Log("  target[0] changed to 0x%02X after ~%dms — but OUR VEH restored it (game EXECUTED "
                        "Vfs_LoadResource; menu loads via VFS). Repair test inconclusive.", cur, i * 250);
                else
                    Log("  target[0] REPAIRED to 0x%02X after ~%dms (game restored it)", cur, i * 250);
            }
        }

        if (!repaired)
            Log("  RESULT: patch survived 30s untouched -> no .text self-repair -> inline hook VIABLE");
        else if (g_int3Exec)
            Log("  RESULT: inconclusive (function was executed & self-recovered before any repair window)");
        else
            Log("  RESULT: .text is self-repaired -> active integrity check -> inline hook AT RISK");

        // Always restore.
        InterlockedExchange(&g_int3Armed, 0);
        if (VirtualProtect((void*)g_target, 8, PAGE_EXECUTE_READWRITE, &op))
        {
            *(uint8_t*)g_target = orig[0];
            FlushInstructionCache(GetCurrentProcess(), (void*)g_target, 1);
            VirtualProtect((void*)g_target, 8, op, &op);
        }
        Log("  restored target[0]=0x%02X", orig[0]);
    }

    // ── Phase 3: game-thread HW execute BP + fire detection (invasive) ────────────
    void Phase3(DWORD selfTid)
    {
        Log("=== Phase 3: HW execute BP on all game threads + fire detection (invasive) ===");
        InterlockedExchange(&g_hwFires, 0);
        InterlockedExchange(&g_stackDumped, 0);
        InterlockedExchange(&g_hwArmed, 1);

        HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snap == INVALID_HANDLE_VALUE) { Log("  snapshot failed; skipping"); InterlockedExchange(&g_hwArmed, 0); return; }

        DWORD pid = GetCurrentProcessId();
        THREADENTRY32 te; te.dwSize = sizeof(te);
        int armed = 0;
        DWORD armedTids[128]; int nArmed = 0;
        if (Thread32First(snap, &te))
        {
            do {
                if (te.th32OwnerProcessID != pid) continue;
                if (te.th32ThreadID == selfTid) continue;
                HANDLE th = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_SET_CONTEXT,
                                       FALSE, te.th32ThreadID);
                if (!th) continue;
                if (SetHwBp(th, g_target)) { armed++; if (nArmed < 128) armedTids[nArmed++] = te.th32ThreadID; }
                CloseHandle(th);
            } while (Thread32Next(snap, &te));
        }
        CloseHandle(snap);
        Log("  armed HW exec BP @%08X on %d game threads", (unsigned)g_target, armed);

        // Watch ~30s: count fires and sample whether the BP is still set on the armed threads.
        int lastFires = 0;
        for (int i = 0; i < 120; i++)
        {
            Sleep(250);
            int f = g_hwFires;
            if (f != lastFires) { Log("  HW BP fired: total=%d", f); lastFires = f; }
        }

        // Sample persistence: how many armed threads still hold our DR?
        int stillSet = 0, checked = 0;
        for (int i = 0; i < nArmed; i++)
        {
            HANDLE th = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT, FALSE, armedTids[i]);
            if (!th) continue;
            uintptr_t d0 = 0; DWORD d7 = 0;
            if (ReadHwBp(th, &d0, &d7)) { checked++; if (d0 == g_target && (d7 & 1)) stillSet++; }
            CloseHandle(th);
        }
        Log("  after 30s: %d/%d still-armed threads retain the DR; fires=%d",
            stillSet, checked, g_hwFires);

        if (g_hwFires > 0)
            Log("  RESULT: HW execute BP FIRED (survived to a real call) -> HW-BP hooking viable & useful");
        else if (checked > 0 && stillSet == 0)
            Log("  RESULT: HW BP was cleared on all threads before firing -> active DR scanning");
        else if (checked > 0 && stillSet == checked)
            Log("  RESULT: HW BP retained but never fired (Vfs_LoadResource not called this session)");
        else
            Log("  RESULT: inconclusive (armed=%d checked=%d)", armed, checked);

        InterlockedExchange(&g_hwArmed, 0);
        // Clear DR on everything we armed.
        for (int i = 0; i < nArmed; i++)
        {
            HANDLE th = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_SET_CONTEXT,
                                   FALSE, armedTids[i]);
            if (th) { ClearHwBp(th); CloseHandle(th); }
        }
    }

    // ── driver thread ─────────────────────────────────────────────────────────────
    DWORD WINAPI ProbeThread(LPVOID)
    {
        OpenLog();
        g_base = (uintptr_t)GetModuleHandleW(nullptr);
        auto dos = (PIMAGE_DOS_HEADER)g_base;
        auto nt  = (PIMAGE_NT_HEADERS)((uint8_t*)g_base + dos->e_lfanew);
        g_size = nt->OptionalHeader.SizeOfImage;
        g_target = g_base + kVfsRva;

        HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        g_rtlCapture = (PFN_RtlCapture)GetProcAddress(ntdll, "RtlCaptureStackBackTrace");

        Log("###### Talon.AntiDebug pid=%lu base=%08X size=%08X target=%08X ######",
            GetCurrentProcessId(), (unsigned)g_base, g_size, (unsigned)g_target);

        AddVectoredExceptionHandler(1, Veh);

        bool unpacked = WaitForUnpack(60);
        if (unpacked)
        {
            const uint8_t* p = (const uint8_t*)g_target;
            Log("Vfs_LoadResource prologue present at %08X: %02X %02X %02X %02X %02X %02X %02X %02X",
                (unsigned)g_target, p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[7]);
        }
        else
        {
            Log("Vfs_LoadResource prologue NOT found within 60s (packer slow, or address moved).");
            Log("Phase 2/3 need the real function; running Phase 1 only.");
        }

        Phase1();
        if (unpacked) Phase2();
        if (unpacked) Phase3(GetCurrentThreadId());

        Log("###### probes complete ######");
        return 0;
    }
}

extern "C" __declspec(dllexport) void TalonInit() { /* probes self-arm from DllMain */ }

BOOL APIENTRY DllMain(HMODULE mod, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(mod);
        // All work (waiting, suspending threads, VEH) happens off the loader lock.
        HANDLE h = CreateThread(nullptr, 0, ProbeThread, nullptr, 0, nullptr);
        if (h) CloseHandle(h);
    }
    return TRUE;
}
