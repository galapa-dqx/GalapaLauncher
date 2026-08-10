// Bulk-unpack completion barrier for DQX's packed image.
//
// The injector pre-arms a generic rendezvous at the mapped PE entrypoint. Boot's
// VEH retargets DR0 to ntdll!NtProtectVirtualMemory before the first entrypoint
// instruction executes. The packer finishes its bulk write by changing exactly
// the page-rounded .text range to PAGE_EXECUTE_READ; the VEH parks it there while
// a normal worker resolves and installs hooks.

#include "unpack_trigger.h"
#include "log.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <stdint.h>
#include <string.h>

static const DWORD kDr7ExecDr0 = 0x00000001;
static const DWORD kResumeFlag = 0x00010000;
static const DWORD kBarrierTimeoutMs = 30000;
static const DWORD kCancellationGraceMs = 5000;
static const LONG kStageAwaitingEntrypoint = 1;
static const LONG kStageAwaitingProtection = 2;
static const LONG kStageCancelled = 3;

static volatile LONG g_armed = 0;
static volatile LONG g_stage = 0;
static uintptr_t g_entrypoint_va = 0;
static uintptr_t g_ntprotect_va = 0;
static uintptr_t g_text_begin = 0;
static uintptr_t g_text_end = 0;
static HANDLE g_unpack_complete = nullptr;
static HANDLE g_managed_ready = nullptr;
static HANDLE g_initialization_cancelled = nullptr;
static volatile LONG* g_initialization_state = nullptr;
static PVOID g_veh_handle = nullptr;

static bool find_text_range(uint8_t* base) {
    auto dos = (PIMAGE_DOS_HEADER)base;
    auto nt = (PIMAGE_NT_HEADERS)(base + dos->e_lfanew);
    g_entrypoint_va = (uintptr_t)base + nt->OptionalHeader.AddressOfEntryPoint;
    auto section = IMAGE_FIRST_SECTION(nt);
    for (WORD i = 0; i < nt->FileHeader.NumberOfSections; ++i) {
        static const BYTE text_name[IMAGE_SIZEOF_SHORT_NAME] =
            {'.', 't', 'e', 'x', 't', 0, 0, 0};
        if (memcmp(section[i].Name, text_name, sizeof(text_name)) != 0) continue;
        uintptr_t begin = (uintptr_t)base + section[i].VirtualAddress;
        uintptr_t end = begin + section[i].Misc.VirtualSize;
        g_text_begin = begin & ~(uintptr_t)0xFFF;
        g_text_end = (end + 0xFFF) & ~(uintptr_t)0xFFF;
        return g_text_end > g_text_begin;
    }
    return false;
}

static void set_dr0(CONTEXT* context, uintptr_t address) {
    context->Dr0 = address;
    context->Dr7 = (context->Dr7 & ~0x000F0003u) | kDr7ExecDr0;
    context->Dr6 = 0;
}

static void clear_dr0(CONTEXT* context) {
    context->Dr0 = 0;
    context->Dr7 &= ~0x000F0003u;
    context->Dr6 = 0;
}

static bool cancel_initialization() {
    if (!g_initialization_state) return false;
    LONG previous = InterlockedCompareExchange(
        g_initialization_state,
        talon_initialization_cancelled,
        talon_initialization_pending);
    if (previous != talon_initialization_pending) return false;
    if (g_initialization_cancelled) SetEvent(g_initialization_cancelled);
    return true;
}

static LONG CALLBACK unpack_veh(EXCEPTION_POINTERS* ep) {
    if (ep->ExceptionRecord->ExceptionCode != EXCEPTION_SINGLE_STEP ||
        !(ep->ContextRecord->Dr6 & 1))
        return EXCEPTION_CONTINUE_SEARCH;

    uintptr_t address = (uintptr_t)ep->ExceptionRecord->ExceptionAddress;
    LONG stage = InterlockedCompareExchange(&g_stage, 0, 0);
    if (stage == kStageCancelled) {
        // cancel_unpack_barrier can race a dispatch whose debug context is held
        // in EXCEPTION_POINTERS rather than the thread's live CONTEXT. Keep the
        // VEH registered and clear any late saved Talon breakpoint here.
        uintptr_t dr0 = (uintptr_t)ep->ContextRecord->Dr0;
        if (address != g_entrypoint_va && address != g_ntprotect_va &&
            dr0 != g_entrypoint_va && dr0 != g_ntprotect_va)
            return EXCEPTION_CONTINUE_SEARCH;
        ep->ContextRecord->EFlags |= kResumeFlag;
        clear_dr0(ep->ContextRecord);
        return EXCEPTION_CONTINUE_EXECUTION;
    }
    if (!g_armed) return EXCEPTION_CONTINUE_SEARCH;

    if (stage == kStageAwaitingEntrypoint && address == g_entrypoint_va) {
        // start_unpack_barrier publishes every target before setting g_armed.
        set_dr0(ep->ContextRecord, g_ntprotect_va);
        if (InterlockedCompareExchange(
                &g_stage,
                kStageAwaitingProtection,
                kStageAwaitingEntrypoint) != kStageAwaitingEntrypoint) {
            ep->ContextRecord->EFlags |= kResumeFlag;
            clear_dr0(ep->ContextRecord);
            return EXCEPTION_CONTINUE_EXECUTION;
        }
        dbg("[barrier] entrypoint rendezvous hit at %p; DR0 -> NtProtectVirtualMemory\n",
            (void*)address);
        return EXCEPTION_CONTINUE_EXECUTION;
    }

    if (stage != kStageAwaitingProtection || address != g_ntprotect_va)
        return EXCEPTION_CONTINUE_SEARCH;

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
    bool final_transition = requested_protect == PAGE_EXECUTE_READ &&
        page_begin == g_text_begin && page_end == g_text_end;

    // RF suppresses DR0 for the NtProtect instruction being resumed.
    ep->ContextRecord->EFlags |= kResumeFlag;
    ep->ContextRecord->Dr6 = 0;
    if (!final_transition)
        return EXCEPTION_CONTINUE_EXECUTION;

    dbg("[barrier] exact .text -> PAGE_EXECUTE_READ: base=%08lX size=%08lX; "
        "parking unpacker\n", requested_base, requested_size);
    clear_dr0(ep->ContextRecord);
    InterlockedExchange(&g_armed, 0);
    if (g_unpack_complete) SetEvent(g_unpack_complete);

    DWORD result = g_managed_ready
        ? WaitForSingleObject(g_managed_ready, kBarrierTimeoutMs) : WAIT_FAILED;
    if (result != WAIT_OBJECT_0) {
        bool cancelled = cancel_initialization();
        dbg("[barrier] managed hook initialization did not finish in %lu ms; "
            "%s Talon startup\n",
            kBarrierTimeoutMs,
            cancelled ? "cancelling" : "waiting for committed");

        // A cancelled managed preparation owns enabled hooks until it rolls them
        // back. Give it a bounded cleanup window before DQX resumes. If managed
        // committed at the deadline, the same window covers its imminent ready signal.
        if (g_managed_ready)
            result = WaitForSingleObject(g_managed_ready, kCancellationGraceMs);
        if (result != WAIT_OBJECT_0)
            dbg("[barrier] managed cancellation did not settle in %lu ms; resuming\n",
                kCancellationGraceMs);
    }
    // Do not unregister the VEH from inside its own callback. Windows waits for
    // active callbacks to drain during removal, which deadlocks this thread. The
    // inert handler remains safe because Talon.Boot stays loaded for the process
    // lifetime and g_armed now makes later dispatches continue immediately.
    return EXCEPTION_CONTINUE_EXECUTION;
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
                CONTEXT context = {}; context.ContextFlags = CONTEXT_DEBUG_REGISTERS;
                if (GetThreadContext(thread, &context) &&
                    (context.Dr0 == g_entrypoint_va || context.Dr0 == g_ntprotect_va)) {
                    clear_dr0(&context);
                    context.ContextFlags = CONTEXT_DEBUG_REGISTERS;
                    SetThreadContext(thread, &context);
                }
                ResumeThread(thread);
            }
            CloseHandle(thread);
        } while (Thread32Next(snap, &te));
    }
    CloseHandle(snap);
}

bool start_unpack_barrier(
    HANDLE unpack_complete,
    HANDLE managed_ready,
    HANDLE initialization_cancelled,
    volatile LONG* initialization_state) {
    uint8_t* base = (uint8_t*)GetModuleHandleA(nullptr);
    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    g_ntprotect_va = ntdll
        ? (uintptr_t)GetProcAddress(ntdll, "NtProtectVirtualMemory") : 0;
    if (!base || !g_ntprotect_va || !find_text_range(base)) {
        dbg("[barrier] could not resolve NtProtectVirtualMemory or .text; no hooks installed\n");
        return false;
    }

    if (!unpack_complete || !managed_ready || !initialization_cancelled ||
        !initialization_state)
        return false;
    g_unpack_complete = unpack_complete;
    g_managed_ready = managed_ready;
    g_initialization_cancelled = initialization_cancelled;
    g_initialization_state = initialization_state;

    g_veh_handle = AddVectoredExceptionHandler(1, unpack_veh);
    if (!g_veh_handle) {
        dbg("[barrier] AddVectoredExceptionHandler failed (err=%lu)\n", GetLastError());
        return false;
    }
    InterlockedExchange(&g_stage, kStageAwaitingEntrypoint);
    InterlockedExchange(&g_armed, 1);

    dbg("[barrier] awaiting injector entrypoint rendezvous=%p; "
        "NtProtectVirtualMemory=%p; .text=[%p,%p)\n",
        (void*)g_entrypoint_va, (void*)g_ntprotect_va,
        (void*)g_text_begin, (void*)g_text_end);
    return true;
}

void cancel_unpack_barrier() {
    cancel_initialization();
    // Publish cancellation before sweeping live thread contexts. A concurrent
    // VEH clears its saved exception context, which the sweep cannot modify.
    // Keep the VEH registered afterward to consume any already-pending trap.
    InterlockedExchange(&g_stage, kStageCancelled);
    clear_barrier_dr0_all_threads(GetCurrentThreadId());
    InterlockedExchange(&g_armed, 0);
}
