// Optional dynamic-analysis probes for Talon.Recon.
//
// Injected as part of Talon.Recon via the existing Talon.Injector. These opt-in probes
// preserve the reusable experiments from the earlier one-shot probe payload:
// unpack-trajectory sampling, debug-register persistence, executable-byte integrity,
// hardware-breakpoint stack capture, and NtProtect transition tracing.
//
//   1. Does the game scan/clear DEBUG REGISTERS? -> is hardware-breakpoint hooking viable?
//   2. Does the game self-repair its own .text?  -> does our inline (byte-patch) hook survive?
//
// Both are observable on a PLACEHOLDER session: unpacking and anti-tamper run during boot,
// independent of login, so no real account is ever authenticated (and a crash on the menu is
// harmless). This payload only observes — it installs no production hook.
//
// TALON_RECON_TARGET_RVA overrides the historical Vfs_LoadResource RVA. This makes the
// integrity and breakpoint probes reusable when a patch moves the function.
//
// Output: %TEMP%\talon-recon-analysis.log (text, one line per event, flushed each write).
//
// Phases run sequentially, each logging its verdict BEFORE the next (riskier) phase, so a
// later crash never loses an earlier answer:
//   Phase T  .text unpack trajectory: bulk single-pass vs incremental on-execute (safe)
//   Phase 1  debug-register persistence on a private victim thread          (safe)
//   Phase 2  patch target[0] = 0xCC and watch for self-repair              (recoverable)
//   Phase 3  arm a HW execute BP on every game thread + VEH fire detection  (invasive)
//
// Phase T is the load-bearing test for Talon's many-hooks plan. The proposed architecture
// installs every hook after a single "game fully unpacked" barrier — which is only valid if
// DQX decompresses .text in ONE bulk pass (a real "done" moment exists). If instead it
// decrypts per-page on first execution, no such moment exists and the barrier is a fiction.
// Phase T decides this empirically by sampling .text population over the unpack window: a
// tight single step = bulk (barrier valid); a spread-out ramp with regions never filling
// during boot = incremental (rethink). See the trajectory summary/verdict lines.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <intrin.h>
#include <stdio.h>
#include <stdlib.h>
#include <stdarg.h>
#include <stdint.h>
#include "dynamic_analysis.h"

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
        _snwprintf(path, MAX_PATH, L"%stalon-recon-analysis.log", dir);
        g_log = _wfopen(path, L"a");
    }

    // ── module / target ─────────────────────────────────────────────────────────
    constexpr uint32_t kDefaultTargetRva = 0xFD0E0;

    // Named function starts from the reversed VFS map (BN addr - image base 0x4b0000), used by
    // Phase T as labelled anchors: in a bulk unpack they all populate in the same tick as the
    // grid; if any lags the others, that argues for on-execute decryption.
    struct Anchor { uint32_t rva; const char* name; };
    const Anchor kAnchors[] = {
        { 0xE91B0,  "Vfs_OpenFileOrArchiveEntry" },  // 0x5991b0
        { 0xFD0E0,  "Vfs_LoadResource" },            // 0x5ad0e0
        { 0xFD9B0,  "Vfs_ResolveMountAndLoad" },     // 0x5ad9b0
        { 0x103560, "Vfs_HashPath" },                // 0x5b3560
        { 0x103ED0, "Vfs_MountArchive" },            // 0x5b3ed0
    };
    constexpr int kNumAnchors = (int)(sizeof(kAnchors) / sizeof(kAnchors[0]));

    uintptr_t g_base   = 0;
    uint32_t  g_size   = 0;
    uintptr_t g_target = 0;
    uint32_t  g_targetRva = kDefaultTargetRva;
    bool      g_targetConfigured = false;

    bool ReadTargetRva()
    {
        char value[32] = {};
        if (!GetEnvironmentVariableA("TALON_RECON_TARGET_RVA", value, sizeof(value)))
            return true;
        char* end = nullptr;
        unsigned long parsed = strtoul(value, &end, 0);
        if (end == value || *end != '\0' || parsed >= g_size)
        {
            Log("invalid TALON_RECON_TARGET_RVA='%s' (expected an RVA inside SizeOfImage)", value);
            return false;
        }
        g_targetRva = parsed;
        g_targetConfigured = true;
        return true;
    }

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

    bool RegionExecReadable(const uint8_t* addr, size_t len);

    bool TargetReady()
    {
        if (!RegionExecReadable((const uint8_t*)g_target, kSigLen)) return false;
        if (!g_targetConfigured) return SigMatches((const uint8_t*)g_target);
        uint8_t populated = 0;
        __try { for (int i = 0; i < 16; ++i) populated |= ((volatile uint8_t*)g_target)[i]; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
        return populated != 0;
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
            if (TargetReady())
                return true;
            Sleep(100);
        }
        return false;
    }

    // ── Phase T: .text unpack trajectory ─────────────────────────────────────────
    // Classify one probe point: -1 = absent (not committed / no-access), 0 = committed but
    // zero-filled (not yet unpacked), 1 = populated (has non-zero content). The 16-byte read is
    // SEH-guarded so a race with the packer (un-committing/repermissioning a page) can't fault us.
    int ClassifyPoint(uintptr_t a)
    {
        MEMORY_BASIC_INFORMATION mbi;
        if (VirtualQuery((void*)a, &mbi, sizeof(mbi)) == 0) return -1;
        if (mbi.State != MEM_COMMIT) return -1;
        DWORD prot = mbi.Protect & 0xFF;
        if ((mbi.Protect & PAGE_GUARD) || prot == PAGE_NOACCESS) return -1;
        uint32_t acc = 0;
        __try { const volatile uint8_t* p = (const volatile uint8_t*)a;
                for (int i = 0; i < 16; i++) acc |= p[i]; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return -1; }
        return acc ? 1 : 0;
    }

    // Log the PE section table and return the .text bounds.
    bool FindTextSection(uintptr_t* outStart, uint32_t* outSize)
    {
        auto dos = (PIMAGE_DOS_HEADER)g_base;
        auto nt  = (PIMAGE_NT_HEADERS)((uint8_t*)g_base + dos->e_lfanew);
        auto sec = IMAGE_FIRST_SECTION(nt);
        int n = nt->FileHeader.NumberOfSections;
        *outStart = 0; *outSize = 0;
        for (int i = 0; i < n; i++)
        {
            char nm[9] = {}; memcpy(nm, sec[i].Name, 8);
            Log("  section %-8s VA=%08X vsize=%08X", nm,
                (unsigned)(g_base + sec[i].VirtualAddress), sec[i].Misc.VirtualSize);
            if (memcmp(sec[i].Name, ".text", 6) == 0)
            { *outStart = g_base + sec[i].VirtualAddress; *outSize = sec[i].Misc.VirtualSize; }
        }
        return *outSize != 0;
    }

    // Sample .text population every 25ms across the unpack window. A tight single step (whole
    // section flips zero->code within a few ticks) = bulk single-pass unpack, so a single
    // "unpack complete" barrier is valid. A spread-out ramp, or many regions never filling
    // during boot, = incremental/on-execute decryption, which has no single barrier moment.
    // Returns true if the configured target is present at the end (feeds `unpacked`).
    bool TextUnpackTrajectory()
    {
        Log("=== Phase T: .text unpack trajectory (bulk single-pass vs incremental on-execute) ===");
        uintptr_t tStart = 0; uint32_t tSize = 0;
        if (!FindTextSection(&tStart, &tSize)) { Log("  .text not found; skipping Phase T"); return false; }
        Log("  .text = [%08X, %08X) size=0x%X", (unsigned)tStart, (unsigned)(tStart + tSize), tSize);

        int nPoints = (int)(tSize / 0x1000);
        if (nPoints > 256) nPoints = 256;
        if (nPoints < 1)   nPoints = 1;
        uint32_t stride = tSize / (uint32_t)nPoints;
        stride &= ~0xFFFu;
        if (stride < 0x1000) stride = 0x1000;

        uintptr_t pts[256];
        int firstPop[256];
        for (int i = 0; i < nPoints; i++) { pts[i] = tStart + (uintptr_t)stride * i; firstPop[i] = -1; }
        int anchorPop[kNumAnchors];
        for (int i = 0; i < kNumAnchors; i++) anchorPop[i] = -1;
        Log("  sampling %d points across .text @ stride 0x%X, every 25ms (~8s window)", nPoints, stride);

        const int kTickMs = 25, kMaxTicks = 320;   // ~8s; unpack is ~0.33s so this captures it wide
        int prevPop = -1, maxPop = 0;
        int seriesTick[128], seriesPop[128], nSeries = 0;
        for (int t = 0; t < kMaxTicks; t++)
        {
            int committed = 0, pop = 0;
            for (int i = 0; i < nPoints; i++)
            {
                int c = ClassifyPoint(pts[i]);
                if (c >= 0) committed++;
                if (c == 1) { pop++; if (firstPop[i] < 0) firstPop[i] = t; }
            }
            for (int i = 0; i < kNumAnchors; i++)
                if (anchorPop[i] < 0 && ClassifyPoint(g_base + kAnchors[i].rva) == 1) anchorPop[i] = t;

            if (pop > maxPop) maxPop = pop;
            if (pop != prevPop)
            {
                if (nSeries < 128) { seriesTick[nSeries] = t; seriesPop[nSeries] = pop; nSeries++; }
                Log("  t=%5dms committed=%3d/%d populated=%3d", t * kTickMs, committed, nPoints, pop);
                prevPop = pop;
            }
            Sleep(kTickMs);
        }

        for (int i = 0; i < kNumAnchors; i++)
        {
            if (anchorPop[i] >= 0)
                Log("  anchor %-28s (rva %06X) populated @ t=%dms", kAnchors[i].name, kAnchors[i].rva, anchorPop[i] * kTickMs);
            else
                Log("  anchor %-28s (rva %06X) NEVER populated in window", kAnchors[i].name, kAnchors[i].rva);
        }

        int never = 0, mn = 1 << 30, mx = -1;
        for (int i = 0; i < nPoints; i++)
        {
            if (firstPop[i] < 0) { never++; continue; }
            if (firstPop[i] < mn) mn = firstPop[i];
            if (firstPop[i] > mx) mx = firstPop[i];
        }
        int tick10 = -1, tick90 = -1;
        for (int i = 0; i < nSeries; i++)
        {
            if (tick10 < 0 && seriesPop[i] >= (maxPop + 9) / 10)      tick10 = seriesTick[i];
            if (tick90 < 0 && seriesPop[i] >= (maxPop * 9) / 10)      tick90 = seriesTick[i];
        }
        int riseMs = (tick10 >= 0 && tick90 >= 0) ? (tick90 - tick10) * kTickMs : -1;
        Log("  SUMMARY: populated %d/%d (never=%d), first@%dms last@%dms, 10%%->90%% rise=%dms",
            maxPop, nPoints, never,
            (mn == (1 << 30)) ? -1 : mn * kTickMs, (mx < 0) ? -1 : mx * kTickMs, riseMs);

        // Verdict (framed as a lean, not proof — the log data is the real evidence).
        if (maxPop > 0 && riseMs >= 0 && riseMs <= 150 && never * 4 < nPoints)
            Log("  VERDICT: LEANS BULK — .text filled in one tight step (rise<=150ms, most points filled) "
                "-> a single 'unpack complete' barrier is valid for the many-hooks design");
        else
            Log("  VERDICT: LEANS INCREMENTAL/INCONCLUSIVE — population spread over %dms and/or %d/%d points "
                "never filled during boot -> re-examine before adopting one barrier", riseMs, never, nPoints);

        return TargetReady();
    }

    // ── shared VEH state ─────────────────────────────────────────────────────────
    volatile LONG g_int3Armed   = 0;   // Phase 2: 0xCC planted at g_target
    volatile LONG g_int3Exec    = 0;   // Phase 2: game executed the 0xCC (recovered)
    uint8_t       g_origByte    = 0;   // Phase 2: original g_target[0]

    volatile LONG g_hwArmed     = 0;   // Phase 3: HW exec BP active at g_target
    volatile LONG g_hwFires     = 0;   // Phase 3: times it fired
    volatile LONG g_stackDumped = 0;   // one-shot stack capture guard

    // Phase 0: HW write breakpoint that catches the unpacker writing target[0].
    volatile LONG g_writeWatch  = 0;   // write BP active
    volatile LONG g_writeHit    = 0;   // fired (one-shot)
    DWORD         g_writeEip     = 0;   // the writing instruction (= the unpacker)
    constexpr int kWriteStackWords = 24;
    DWORD         g_writeStack[kWriteStackWords] = {};

    uintptr_t     g_textStart   = 0;
    uint32_t      g_textSize    = 0;
    // Entrypoint -> NtProtectVirtualMemory barrier trace.
    volatile LONG g_protectWatch = 0;
    volatile LONG g_protectStage = 0;
    volatile LONG g_protectHit = 0;
    uintptr_t g_entryTarget = 0;
    uint32_t g_entryRva = 0;
    uintptr_t g_ntProtectTarget = 0;
    DWORD g_entryBytes[4] = {};
    struct ProtectCall { DWORD ret, process, base, size, protect; };
    static const int kMaxProtectCalls = 2048;
    ProtectCall g_protectCalls[kMaxProtectCalls] = {};
    volatile LONG g_protectCallCount = 0;
    volatile LONG g_vehInstalled = 0;

    using PFN_RtlCapture = USHORT (WINAPI*)(ULONG, ULONG, PVOID*, PULONG);
    PFN_RtlCapture g_rtlCapture = nullptr;

    bool AnalysisModeIs(const char* expected)
    {
        char value[32] = {};
        if (!GetEnvironmentVariableA("TALON_RECON_ANALYSIS", value, sizeof(value)))
            return false;
        return _stricmp(value, expected) == 0;
    }

    bool AnalysisWorkerRequested()
    {
        return GetEnvironmentVariableA("TALON_RECON_ANALYSIS", nullptr, 0) != 0;
    }

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

        if (g_protectWatch && er->ExceptionCode == EXCEPTION_SINGLE_STEP && (ep->ContextRecord->Dr6 & 1))
        {
            if (g_protectStage == 0 && addr == g_entryTarget)
            {
                __try
                {
                    const DWORD* p = (const DWORD*)g_entryTarget;
                    for (int i = 0; i < 4; ++i) g_entryBytes[i] = p[i];
                }
                __except (EXCEPTION_EXECUTE_HANDLER)
                {
                    for (int i = 0; i < 4; ++i) g_entryBytes[i] = 0;
                }
                ep->ContextRecord->Dr0 = g_ntProtectTarget;
                ep->ContextRecord->Dr7 = 1;
                ep->ContextRecord->Dr6 = 0;
                InterlockedExchange(&g_protectStage, 1);
                return EXCEPTION_CONTINUE_EXECUTION;
            }
            if (g_protectStage == 1 && addr == g_ntProtectTarget)
            {
                ProtectCall c = {};
                __try
                {
                    const DWORD* sp = (const DWORD*)ep->ContextRecord->Esp;
                    c.ret = sp[0];
                    c.process = sp[1];
                    c.base = sp[2] ? *(const DWORD*)(uintptr_t)sp[2] : 0;
                    c.size = sp[3] ? *(const DWORD*)(uintptr_t)sp[3] : 0;
                    c.protect = sp[4];
                }
                __except (EXCEPTION_EXECUTE_HANDLER) { c = {}; }

                LONG n = InterlockedIncrement(&g_protectCallCount) - 1;
                if (n >= 0 && n < kMaxProtectCalls) g_protectCalls[n] = c;

                uint64_t end = (uint64_t)c.base + c.size;
                uint64_t textEnd = (uint64_t)g_textStart + g_textSize;
                DWORD protection = c.protect & 0xff;
                bool executable = protection == PAGE_EXECUTE || protection == PAGE_EXECUTE_READ ||
                                  protection == PAGE_EXECUTE_READWRITE || protection == PAGE_EXECUTE_WRITECOPY;
                if (executable && c.base < textEnd && end > g_textStart &&
                    InterlockedExchange(&g_protectHit, 1) == 0)
                {
                    ep->ContextRecord->Dr0 = 0;
                    ep->ContextRecord->Dr7 = 0;
                    InterlockedExchange(&g_protectWatch, 0);
                }
                // RF suppresses DR0 for the instruction being resumed.
                ep->ContextRecord->EFlags |= 0x00010000;
                ep->ContextRecord->Dr6 = 0;
                return EXCEPTION_CONTINUE_EXECUTION;
            }
        }

        // Talon.Injector always leaves DR0 armed at the mapped PE entrypoint. Recon must
        // consume that rendezvous even when no analysis mode is selected; otherwise the
        // target receives an unhandled EXCEPTION_SINGLE_STEP. PROTECT mode handles the
        // same event above and retargets DR0 instead of clearing it.
        if (er->ExceptionCode == EXCEPTION_SINGLE_STEP && (ep->ContextRecord->Dr6 & 1)
            && addr == g_entryTarget)
        {
            ep->ContextRecord->Dr0 = 0;
            ep->ContextRecord->Dr7 &= ~0x000F0003u;
            ep->ContextRecord->Dr6 = 0;
            return EXCEPTION_CONTINUE_EXECUTION;
        }

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

        // Phase 0: HW WRITE breakpoint fired — the unpacker just wrote target[0]. The faulting
        // instruction (Eip) is inside the unpacker's decrypt/copy routine. Snapshot Eip + a slice
        // of the interrupted stack (own thread, readable) and disarm so it fires once.
        if (g_writeWatch && er->ExceptionCode == EXCEPTION_SINGLE_STEP && (ep->ContextRecord->Dr6 & 0xF)
            && InterlockedExchange(&g_writeHit, 1) == 0)
        {
            g_writeEip = ep->ContextRecord->Eip;
            const uint32_t* sp = (const uint32_t*)ep->ContextRecord->Esp;
            for (int i = 0; i < kWriteStackWords; i++) g_writeStack[i] = sp[i];
            ep->ContextRecord->Dr0 = 0;
            ep->ContextRecord->Dr7 = 0;
            ep->ContextRecord->Dr6 = 0;
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
    // DR7 for DR0 as a 1-byte breakpoint. EXECUTE: RW0=00, LEN0=00. WRITE: RW0=01, LEN0=00.
    constexpr DWORD kDr7ExecDr0  = 0x00000001;
    constexpr DWORD kDr7WriteDr0 = 0x00010001;

    bool SetHwBp(HANDLE th, uintptr_t addr, DWORD dr7 = kDr7ExecDr0)
    {
        if (SuspendThread(th) == (DWORD)-1) return false;
        CONTEXT c; c.ContextFlags = CONTEXT_DEBUG_REGISTERS;
        bool ok = GetThreadContext(th, &c) != 0;
        if (ok)
        {
            c.Dr0 = addr;
            c.Dr7 = dr7;
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

        uintptr_t sentinel = g_target ? g_target : (g_base + kDefaultTargetRva);
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
        Log("=== Phase 2: .text self-repair (patch target[0]=0xCC) ===");
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
                    Log("  target[0] changed to 0x%02X after ~%dms — but OUR VEH restored it "
                        "after target execution. Repair test inconclusive.", cur, i * 250);
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
            Log("  RESULT: HW BP retained but never fired (target not called this session)");
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

    void PhaseProtectBarrier()
    {
        Log("=== Phase PROTECT: PE entrypoint -> NtProtectVirtualMemory execute-BP chain ===");
        Log("  entry=%08X RVA=%08X NtProtect=%08X text=[%08X,%08X)",
            (unsigned)g_entryTarget, g_entryRva, (unsigned)g_ntProtectTarget,
            (unsigned)g_textStart, (unsigned)(g_textStart + g_textSize));
        for (int i = 0; i < 1200 && !g_protectHit; ++i) Sleep(5);

        LONG count = g_protectCallCount;
        if (count > kMaxProtectCalls) count = kMaxProtectCalls;
        Log("  stage=%ld bytes=%08X %08X %08X %08X calls=%ld hit=%ld",
            g_protectStage, g_entryBytes[0], g_entryBytes[1], g_entryBytes[2],
            g_entryBytes[3], g_protectCallCount, g_protectHit);
        for (LONG i = 0; i < count; ++i)
        {
            const ProtectCall& c = g_protectCalls[i];
            bool overlaps = c.base < (uint64_t)g_textStart + g_textSize &&
                            (uint64_t)c.base + c.size > g_textStart;
            Log("  #%03ld ret=%08X base=%08X size=%08X protect=%08X%s",
                i, c.ret, c.base, c.size, c.protect, overlaps ? " [overlaps .text]" : "");
        }
        Log("  RESULT: %s", g_protectHit
            ? "BARRIER FIRED on executable transition overlapping .text"
            : "barrier did not fire");
        if (!g_protectHit)
        {
            HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
            if (snap != INVALID_HANDLE_VALUE)
            {
                DWORD pid = GetCurrentProcessId();
                THREADENTRY32 te; te.dwSize = sizeof(te);
                if (Thread32First(snap, &te))
                    do {
                        if (te.th32OwnerProcessID != pid || te.th32ThreadID == GetCurrentThreadId()) continue;
                        HANDLE thread = OpenThread(
                            THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_SET_CONTEXT,
                            FALSE, te.th32ThreadID);
                        if (thread) { ClearHwBp(thread); CloseHandle(thread); }
                    } while (Thread32Next(snap, &te));
                CloseHandle(snap);
            }
        }
        InterlockedExchange(&g_protectWatch, 0);
    }

    // ── driver thread ─────────────────────────────────────────────────────────────
    DWORD WINAPI ProbeThread(LPVOID)
    {
        OpenLog();
        g_base = (uintptr_t)GetModuleHandleW(nullptr);
        auto dos = (PIMAGE_DOS_HEADER)g_base;
        auto nt  = (PIMAGE_NT_HEADERS)((uint8_t*)g_base + dos->e_lfanew);
        g_size = nt->OptionalHeader.SizeOfImage;
        if (!ReadTargetRva()) return 1;
        g_target = g_base + g_targetRva;

        HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        g_rtlCapture = (PFN_RtlCapture)GetProcAddress(ntdll, "RtlCaptureStackBackTrace");

        Log("###### Talon.Recon analysis pid=%lu base=%08X size=%08X target=%08X (RVA %08X) ######",
            GetCurrentProcessId(), (unsigned)g_base, g_size, (unsigned)g_target, g_targetRva);

        if (InterlockedCompareExchange(&g_vehInstalled,1,0)==0) AddVectoredExceptionHandler(1,Veh);
        if (AnalysisModeIs("protect")) { PhaseProtectBarrier(); Log("###### PROTECT barrier probe complete ######"); return 0; }

        if (AnalysisModeIs("trajectory"))
        {
            TextUnpackTrajectory();
            Log("###### unpack trajectory complete ######");
            return 0;
        }
        if (!AnalysisModeIs("full"))
        {
            char mode[32] = {};
            GetEnvironmentVariableA("TALON_RECON_ANALYSIS", mode, sizeof(mode));
            Log("unknown TALON_RECON_ANALYSIS mode '%s'; no invasive probes run", mode);
            return 1;
        }

        // Phase 0: arm a HW WRITE breakpoint at target[0] on every existing thread BEFORE the
        // packer runs, so we catch the exact instruction that unpacks the target into place
        // — an event-driven alternative to polling, which also identifies the unpacker.
        Log("=== Phase 0: catch the unpack write (HW write BP at target[0]) ===");
        InterlockedExchange(&g_writeHit, 0);
        InterlockedExchange(&g_writeWatch, 1);
        DWORD selfTid = GetCurrentThreadId();
        {
            int warmed = 0;
            HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
            if (snap != INVALID_HANDLE_VALUE)
            {
                DWORD pid = GetCurrentProcessId();
                THREADENTRY32 te; te.dwSize = sizeof(te);
                if (Thread32First(snap, &te))
                    do {
                        if (te.th32OwnerProcessID != pid || te.th32ThreadID == selfTid) continue;
                        HANDLE th = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_SET_CONTEXT,
                                               FALSE, te.th32ThreadID);
                        if (!th) continue;
                        if (SetHwBp(th, g_target, kDr7WriteDr0)) warmed++;
                        CloseHandle(th);
                    } while (Thread32Next(snap, &te));
                CloseHandle(snap);
            }
            Log("  armed write BP @%08X on %d thread(s); waiting for the unpacker to write it",
                (unsigned)g_target, warmed);
        }

        // Phase T runs the dense sampling that both characterises the unpack (bulk vs
        // incremental) and tells us whether the configured target ended up present. If the packer
        // was unusually slow and it isn't present yet, fall back to the coarse poll.
        bool unpacked = TextUnpackTrajectory();
        if (!unpacked) unpacked = WaitForUnpack(52);

        InterlockedExchange(&g_writeWatch, 0);
        if (g_writeHit)
        {
            Log("  UNPACK WRITE CAUGHT: writing instruction EIP=%08X (exe+0x%X) -- this is the unpacker",
                g_writeEip, (unsigned)(g_writeEip - g_base));
            Log("  interrupted-stack slice (in-module addresses = unpacker call chain):");
            for (int i = 0; i < kWriteStackWords; i++)
            {
                uint32_t v = g_writeStack[i];
                if (v >= g_base && v < g_base + g_size)
                    Log("    [esp+%02X] %08X (exe+0x%X)", i * 4, v, (unsigned)(v - g_base));
            }
        }
        else
        {
            Log("  write BP did NOT fire (packer may map the region wholesale rather than storing "
                "byte 0, or writes from a thread created after arming)");
        }

        if (unpacked)
        {
            const uint8_t* p = (const uint8_t*)g_target;
            Log("target code present at %08X: %02X %02X %02X %02X %02X %02X %02X %02X",
                (unsigned)g_target, p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[7]);
        }
        else
        {
            Log("target code NOT found within 60s (packer slow, RVA moved, or default signature changed).");
            Log("Phase 2/3 need the real function; running Phase 1 only.");
        }

        Phase1();
        if (unpacked) Phase2();
        if (unpacked) Phase3(GetCurrentThreadId());

        Log("###### probes complete ######");
        return 0;
    }
}

void StartDynamicAnalysis()
{
    g_base = (uintptr_t)GetModuleHandleW(nullptr);
    auto dos = (PIMAGE_DOS_HEADER)g_base;
    auto nt = (PIMAGE_NT_HEADERS)((uint8_t*)g_base + dos->e_lfanew);
    g_size = nt->OptionalHeader.SizeOfImage;
    g_entryRva = nt->OptionalHeader.AddressOfEntryPoint;
    g_entryTarget = g_base + g_entryRva;

    // Install this for every Recon run. Besides serving the optional probes, it consumes
    // the Injector's entrypoint rendezvous so Recon is safe to use as --boot-dll.
    if (InterlockedCompareExchange(&g_vehInstalled, 1, 0) == 0)
        AddVectoredExceptionHandler(1, Veh);

    if (AnalysisModeIs("protect"))
    {
        auto sec = IMAGE_FIRST_SECTION(nt);
        for (int i = 0; i < nt->FileHeader.NumberOfSections; ++i)
            if (memcmp(sec[i].Name, ".text", 6) == 0)
            {
                g_textStart = g_base + sec[i].VirtualAddress;
                g_textSize = sec[i].Misc.VirtualSize;
                break;
            }
        HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        g_ntProtectTarget = (uintptr_t)GetProcAddress(ntdll, "NtProtectVirtualMemory");
        InterlockedExchange(&g_protectWatch, 1);
        InterlockedExchange(&g_protectStage, 0);
    }

    if (!AnalysisWorkerRequested()) return;
    // Waiting, sampling, and thread-context work must happen outside the loader lock.
    HANDLE thread = CreateThread(nullptr, 0, ProbeThread, nullptr, 0, nullptr);
    if (thread) CloseHandle(thread);
}
