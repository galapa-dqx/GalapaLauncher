// ── Unpack trigger ─────────────────────────────────────────────────────────────
// DQX's .text is packed: at inject the target region is committed but zero-filled,
// and the unpacker DECOMPRESSES it in blocks (non-linear writes), so watching a
// single byte's write does not tell us the whole function is present. Instead we arm
// a HARDWARE EXECUTE breakpoint at target[0] on every thread before the game runs.
// It sits harmlessly on the zero-filled address until the game unpacks the function
// and CALLS it — and a call cannot happen until the function is fully unpacked. The
// resulting #DB (caught by a VEH) is our exact, poll-free signal. The VEH does NOT install
// the hook itself — the MinHook engine suspends threads and takes locks, which is unsafe
// from an exception handler — it only signals the watcher thread, which installs the hook
// in normal context. That first triggering call therefore runs the original function
// unhooked; every call after it is served. (The planned OEP barrier will install before any
// game code runs, closing even that one-call gap.)
// This survives because DQX does not scan debug registers (verified 2026-07-17).
//
// There is intentionally NO poll or scan fallback: if the trigger never fires (e.g.
// a patch moved the function or cleared DRs) we no-op and log a re-anchor notice.
// Per-patch testing before the injector is used is expected to catch that.

#include "unpack_trigger.h"
#include "vfs_hook.h"
#include "hook_manager.h"
#include "log.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <stdint.h>

// DR7 for DR0 as a 1-byte EXECUTE breakpoint: L0=1 (bit 0), RW0=00, LEN0=00.
static const DWORD    kDr7ExecDr0  = 0x00000001;
static volatile LONG  g_call_armed = 0;
static uintptr_t      g_target_va  = 0;
static HANDLE         g_unpack_event = nullptr;
static PVOID          g_veh_handle   = nullptr;

// Fires on the FIRST execution of Vfs_LoadResource — by which point the function is
// necessarily fully unpacked (you can't call a half-decompressed function). We do NOT hook
// here: MinHook's install suspends threads and takes locks, which is unsafe from a VEH.
// Instead we disarm this thread's breakpoint, signal the watcher, and resume with EIP
// unchanged — so this call runs the original function while the watcher installs the hook
// in normal context. Every subsequent call is served.
static LONG CALLBACK unpack_veh(EXCEPTION_POINTERS* ep) {
    if (g_call_armed &&
        ep->ExceptionRecord->ExceptionCode == EXCEPTION_SINGLE_STEP &&
        (ep->ContextRecord->Dr6 & 0xF) &&
        (uintptr_t)ep->ExceptionRecord->ExceptionAddress == g_target_va) {
        ep->ContextRecord->Dr0 = 0;   // disarm this thread so it doesn't re-trap
        ep->ContextRecord->Dr7 = 0;
        ep->ContextRecord->Dr6 = 0;
        if (g_unpack_event) SetEvent(g_unpack_event);
        return EXCEPTION_CONTINUE_EXECUTION;
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

// Set DR0/DR7 on every thread in this process except `selfTid`.
static void set_dr_all_threads(uintptr_t addr, DWORD dr7, DWORD selfTid) {
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snap == INVALID_HANDLE_VALUE) return;
    DWORD pid = GetCurrentProcessId();
    THREADENTRY32 te; te.dwSize = sizeof(te);
    if (Thread32First(snap, &te)) {
        do {
            if (te.th32OwnerProcessID != pid || te.th32ThreadID == selfTid) continue;
            HANDLE th = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_SET_CONTEXT,
                                   FALSE, te.th32ThreadID);
            if (!th) continue;
            if (SuspendThread(th) != (DWORD)-1) {
                CONTEXT c; c.ContextFlags = CONTEXT_DEBUG_REGISTERS;
                if (GetThreadContext(th, &c)) {
                    c.Dr0 = addr;
                    c.Dr7 = dr7;
                    c.ContextFlags = CONTEXT_DEBUG_REGISTERS;
                    SetThreadContext(th, &c);
                }
                ResumeThread(th);
            }
            CloseHandle(th);
        } while (Thread32Next(snap, &te));
    }
    CloseHandle(snap);
}

// Install the inline hook when the game first CALLS Vfs_LoadResource, detected purely
// via an execute-BP trigger (no polling, no scan fallback). Runs on its own thread so
// DllMain does no work under the loader lock beyond spawning it.
static DWORD WINAPI watcher_thread(LPVOID) {
    uint8_t* base = (uint8_t*)GetModuleHandleA(nullptr);
    uint8_t* candidate = base + VFS_LOADRESOURCE_RVA;
    DWORD selfTid = GetCurrentThreadId();
    g_target_va = (uintptr_t)candidate;
    dbg("[boot] watcher: module base=%p, expecting Vfs_LoadResource at %p\n", base, candidate);

    // Arm an execute BP at target[0] on every thread and wait for the first call. The
    // breakpoint sits harmlessly on the (currently zero-filled) address until the game
    // unpacks the function and calls it — at which point the whole function is present.
    g_unpack_event = CreateEventA(nullptr, TRUE, FALSE, nullptr);
    g_veh_handle = AddVectoredExceptionHandler(1, unpack_veh);
    InterlockedExchange(&g_call_armed, 1);
    set_dr_all_threads((uintptr_t)candidate, kDr7ExecDr0, selfTid);
    dbg("[boot] watcher: armed execute-BP trigger @%p (first call); waiting\n", candidate);

    DWORD wr = g_unpack_event ? WaitForSingleObject(g_unpack_event, 30000) : WAIT_FAILED;

    // Tear the trigger down BEFORE installing. Disarm every thread first (the VEH is still
    // live to catch any straggler that traps mid-teardown), then stop the VEH from acting,
    // then remove it — so the execute-BP can't re-trap on the E9 MinHook is about to write.
    set_dr_all_threads(0, 0, selfTid);              // disarm every thread
    InterlockedExchange(&g_call_armed, 0);
    if (g_veh_handle) { RemoveVectoredExceptionHandler(g_veh_handle); g_veh_handle = nullptr; }

    if (wr == WAIT_OBJECT_0) {
        // Normal thread context now — safe for MinHook's thread suspension. Install every
        // registered hook (currently just VFS; the OEP barrier will drive the same call
        // for the full multi-hook set).
        int n = hook_install_all();
        if (n > 0)
            dbg("[boot] watcher: execute-BP trigger FIRED; %d hook(s) installed in watcher thread\n", n);
        else
            dbg("[boot] watcher: trigger fired but NO hooks installed (see [hookmgr] errors)\n");
    } else {
        dbg("[boot] watcher: *** EXECUTE-BP TRIGGER DID NOT FIRE (wait=%lu) — not hooked; "
            "re-anchor the RVA/signature after a game patch ***\n", wr);
    }

    if (g_unpack_event) { CloseHandle(g_unpack_event); g_unpack_event = nullptr; }
    return 0;
}

void start_unpack_watcher() {
    // Creating (not waiting on) a thread from DllMain is safe; the caller's
    // DisableThreadLibraryCalls suppresses this thread's attach callback.
    HANDLE h = CreateThread(nullptr, 0, watcher_thread, nullptr, 0, nullptr);
    if (h) CloseHandle(h);
    else dbg("[boot] CreateThread(watcher) failed (err=%lu)\n", GetLastError());
}
