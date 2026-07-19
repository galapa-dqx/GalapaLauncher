// Universal post-unpack barrier for DQX's KONN-packed image.
//
// The injector decodes the packed file's KONN metadata and pre-arms DR0 at the
// second-stage entry before the primary thread is resumed. Our VEH retargets that
// execute breakpoint to ntdll!NtProtectVirtualMemory. Whole-.text executable
// transitions are candidates: the VEH parks the unpacker while a normal worker scans
// for unpacked targets. False candidates resume with DR0 rearmed; the confirmed
// candidate remains parked until every scanner-resolved hook is installed.

#include "unpack_trigger.h"
#include "vfs_hook.h"
#include "hook_manager.h"
#include "log.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

static const DWORD kDr7ExecDr0 = 0x00000001;
static const DWORD kResumeFlag = 0x00010000;
static const DWORD kBarrierTimeoutMs = 30000;

static volatile LONG g_armed = 0;
static volatile LONG g_protect_stage = 0;
static volatile LONG g_candidate_active = 0;
static volatile LONG g_barrier_complete = 0;
static uintptr_t g_stage2_va = 0;
static uintptr_t g_ntprotect_va = 0;
static uintptr_t g_text_begin = 0;
static uintptr_t g_text_end = 0;
static HANDLE g_candidate_event = nullptr;
static HANDLE g_candidate_done = nullptr;

static bool executable_protection(DWORD protect) {
    switch (protect & 0xFF) {
        case PAGE_EXECUTE:
        case PAGE_EXECUTE_READ:
        case PAGE_EXECUTE_READWRITE:
        case PAGE_EXECUTE_WRITECOPY:
            return true;
        default:
            return false;
    }
}

static bool find_text_range(uint8_t* base) {
    auto dos = (PIMAGE_DOS_HEADER)base;
    auto nt = (PIMAGE_NT_HEADERS)(base + dos->e_lfanew);
    auto section = IMAGE_FIRST_SECTION(nt);
    for (WORD i = 0; i < nt->FileHeader.NumberOfSections; ++i) {
        if (memcmp(section[i].Name, ".text", 5) != 0) continue;
        uintptr_t begin = (uintptr_t)base + section[i].VirtualAddress;
        uintptr_t end = begin + section[i].Misc.VirtualSize;
        g_text_begin = begin & ~(uintptr_t)0xFFF;
        g_text_end = (end + 0xFFF) & ~(uintptr_t)0xFFF;
        return g_text_end > g_text_begin;
    }
    return false;
}

static LONG CALLBACK unpack_veh(EXCEPTION_POINTERS* ep) {
    if (!g_armed || ep->ExceptionRecord->ExceptionCode != EXCEPTION_SINGLE_STEP ||
        !(ep->ContextRecord->Dr6 & 1))
        return EXCEPTION_CONTINUE_SEARCH;

    uintptr_t address = (uintptr_t)ep->ExceptionRecord->ExceptionAddress;

    if (g_protect_stage == 1 && address == g_stage2_va) {
        ep->ContextRecord->Dr0 = g_ntprotect_va;
        ep->ContextRecord->Dr7 = kDr7ExecDr0;
        ep->ContextRecord->Dr6 = 0;
        InterlockedExchange(&g_protect_stage, 2);
        return EXCEPTION_CONTINUE_EXECUTION;
    }

    if (g_protect_stage == 2 && address == g_ntprotect_va) {
        DWORD requested_base = 0, requested_size = 0, requested_protect = 0;
        __try {
            const DWORD* sp = (const DWORD*)ep->ContextRecord->Esp;
            requested_base = sp[2] ? *(const DWORD*)(uintptr_t)sp[2] : 0;
            requested_size = sp[3] ? *(const DWORD*)(uintptr_t)sp[3] : 0;
            requested_protect = sp[4];
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            requested_base = requested_size = requested_protect = 0;
        }

        uint64_t raw_end = (uint64_t)requested_base + requested_size;
        uintptr_t page_begin = (uintptr_t)requested_base & ~(uintptr_t)0xFFF;
        uintptr_t page_end = raw_end <= UINTPTR_MAX - 0xFFF
            ? ((uintptr_t)raw_end + 0xFFF) & ~(uintptr_t)0xFFF : UINTPTR_MAX;
        bool candidate = executable_protection(requested_protect) &&
            page_begin <= g_text_begin && page_end >= g_text_end;

        // RF suppresses DR0 for the NtProtect instruction being resumed.
        ep->ContextRecord->EFlags |= kResumeFlag;
        ep->ContextRecord->Dr6 = 0;

        if (candidate && InterlockedCompareExchange(&g_candidate_active, 1, 0) == 0) {
            // Temporarily disarm while the worker decides whether game code is present.
            ep->ContextRecord->Dr0 = 0;
            ep->ContextRecord->Dr7 = 0;
            if (g_candidate_event) SetEvent(g_candidate_event);

            DWORD decision = g_candidate_done
                ? WaitForSingleObject(g_candidate_done, kBarrierTimeoutMs) : WAIT_FAILED;
            if (decision == WAIT_OBJECT_0 && g_barrier_complete) {
                InterlockedExchange(&g_armed, 0);
            } else if (g_armed) {
                // This was an earlier staging transition. Let its pending syscall run,
                // then observe the next NtProtectVirtualMemory call.
                ep->ContextRecord->Dr0 = g_ntprotect_va;
                ep->ContextRecord->Dr7 = kDr7ExecDr0;
            }
            InterlockedExchange(&g_candidate_active, 0);
        }
        return EXCEPTION_CONTINUE_EXECUTION;
    }

    return EXCEPTION_CONTINUE_SEARCH;
}

// On timeout, remove only Talon's DR0 execute breakpoint and preserve any debugger's
// other hardware-breakpoint slots.
static void clear_barrier_dr0_all_threads(DWORD self_tid) {
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snap == INVALID_HANDLE_VALUE) return;
    DWORD pid = GetCurrentProcessId();
    THREADENTRY32 te = {}; te.dwSize = sizeof(te);
    if (Thread32First(snap, &te)) {
        do {
            if (te.th32OwnerProcessID != pid || te.th32ThreadID == self_tid) continue;
            HANDLE thread = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT |
                                       THREAD_SET_CONTEXT, FALSE, te.th32ThreadID);
            if (!thread) continue;
            if (SuspendThread(thread) != (DWORD)-1) {
                CONTEXT c = {}; c.ContextFlags = CONTEXT_DEBUG_REGISTERS;
                if (GetThreadContext(thread, &c) &&
                    (c.Dr0 == g_stage2_va || c.Dr0 == g_ntprotect_va)) {
                    c.Dr0 = 0;
                    c.Dr7 &= ~0x000F0003u; // L0/G0 and RW0/LEN0 only
                    c.ContextFlags = CONTEXT_DEBUG_REGISTERS;
                    SetThreadContext(thread, &c);
                }
                ResumeThread(thread);
            }
            CloseHandle(thread);
        } while (Thread32Next(snap, &te));
    }
    CloseHandle(snap);
}

static DWORD WINAPI watcher_thread(LPVOID) {
    dbg("[barrier] KONN stage2=%p -> NtProtectVirtualMemory=%p; .text=[%p,%p)\n",
        (void*)g_stage2_va, (void*)g_ntprotect_va,
        (void*)g_text_begin, (void*)g_text_end);

    ULONGLONG deadline = GetTickCount64() + kBarrierTimeoutMs;
    for (;;) {
        ULONGLONG now = GetTickCount64();
        DWORD remaining = now < deadline ? (DWORD)(deadline - now) : 0;
        DWORD wr = g_candidate_event
            ? WaitForSingleObject(g_candidate_event, remaining) : WAIT_FAILED;
        if (wr != WAIT_OBJECT_0) {
            InterlockedExchange(&g_armed, 0);
            clear_barrier_dr0_all_threads(GetCurrentThreadId());
            if (g_candidate_done) SetEvent(g_candidate_done);
            dbg("[barrier] DID NOT CONFIRM unpack in %lu ms; Talon DR0 cleared, no hooks installed\n",
                kBarrierTimeoutMs);
            return 0;
        }

        bool resolved = vfs_resolve_and_register();
        if (!resolved) {
            dbg("[barrier] executable .text candidate rejected; unpacked VFS signature not present yet\n");
            if (g_candidate_done) SetEvent(g_candidate_done);
            continue;
        }

        int installed = hook_install_all();
        InterlockedExchange(&g_barrier_complete, 1);
        InterlockedExchange(&g_armed, 0);
        dbg("[barrier] CONFIRMED via page-rounded .text executable transition; "
            "scanner resolved=1, hooks installed=%d\n", installed);
        if (g_candidate_done) SetEvent(g_candidate_done);
        return 0;
    }
}

void start_unpack_watcher() {
    char stage_rva_text[32] = {};
    DWORD n = GetEnvironmentVariableA("TALON_UNPACK_STAGE_RVA", stage_rva_text,
                                      sizeof(stage_rva_text));
    if (n == 0 || n >= sizeof(stage_rva_text)) {
        dbg("[barrier] TALON_UNPACK_STAGE_RVA missing; universal barrier not armed "
            "(launch with Talon.Injector --unpack-barrier)\n");
        return;
    }

    char* end = nullptr;
    unsigned long rva = strtoul(stage_rva_text, &end, 16);
    uint8_t* base = (uint8_t*)GetModuleHandleA(nullptr);
    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    g_ntprotect_va = ntdll
        ? (uintptr_t)GetProcAddress(ntdll, "NtProtectVirtualMemory") : 0;
    if (end == stage_rva_text || *end != '\0' || !rva || !g_ntprotect_va ||
        !find_text_range(base)) {
        dbg("[barrier] invalid TALON_UNPACK_STAGE_RVA or PE state; universal barrier not armed\n");
        return;
    }
    g_stage2_va = (uintptr_t)base + rva;
    InterlockedExchange(&g_protect_stage, 1);

    // Auto-reset events implement a candidate/decision handshake between the VEH and
    // worker, allowing an arbitrary number of early staging transitions.
    g_candidate_event = CreateEventA(nullptr, FALSE, FALSE, nullptr);
    g_candidate_done = CreateEventA(nullptr, FALSE, FALSE, nullptr);
    if (!g_candidate_event || !g_candidate_done) {
        dbg("[barrier] CreateEvent failed (err=%lu)\n", GetLastError());
        if (g_candidate_event) { CloseHandle(g_candidate_event); g_candidate_event = nullptr; }
        if (g_candidate_done) { CloseHandle(g_candidate_done); g_candidate_done = nullptr; }
        return;
    }

    // The injector's stage-two breakpoint may fire immediately after DllMain returns, so
    // the VEH must be installed synchronously under this call, before spawning the worker.
    PVOID veh_handle = AddVectoredExceptionHandler(1, unpack_veh);
    if (!veh_handle) {
        dbg("[barrier] AddVectoredExceptionHandler failed (err=%lu)\n", GetLastError());
        return;
    }
    InterlockedExchange(&g_armed, 1);

    HANDLE thread = CreateThread(nullptr, 0, watcher_thread, nullptr, 0, nullptr);
    if (thread) CloseHandle(thread);
    else {
        InterlockedExchange(&g_armed, 0);
        dbg("[barrier] CreateThread(watcher) failed (err=%lu)\n", GetLastError());
    }
}
