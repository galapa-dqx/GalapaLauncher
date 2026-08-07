// Bulk-unpack completion barrier for DQX's packed image.
//
// The injector pre-arms a generic rendezvous at the mapped PE entrypoint. Boot's
// VEH retargets DR0 to ntdll!NtProtectVirtualMemory before the first entrypoint
// instruction executes. The packer finishes its bulk write by changing exactly
// the page-rounded .text range to PAGE_EXECUTE_READ; the VEH parks it there while
// a normal worker resolves and installs hooks.

#include "unpack_trigger.h"
#include "vfs_hook.h"
#include "hook_manager.h"
#include "log.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <stdint.h>
#include <string.h>

static const DWORD kDr7ExecDr0 = 0x00000001;
static const DWORD kResumeFlag = 0x00010000;
static const DWORD kBarrierTimeoutMs = 30000;

static volatile LONG g_armed = 0;
static volatile LONG g_stage = 0;
static volatile LONG g_worker_ready = 0;
static uintptr_t g_entrypoint_va = 0;
static uintptr_t g_ntprotect_va = 0;
static uintptr_t g_text_begin = 0;
static uintptr_t g_text_end = 0;
static HANDLE g_barrier_event = nullptr;
static HANDLE g_barrier_done = nullptr;

static bool find_text_range(uint8_t* base) {
    auto dos = (PIMAGE_DOS_HEADER)base;
    auto nt = (PIMAGE_NT_HEADERS)(base + dos->e_lfanew);
    g_entrypoint_va = (uintptr_t)base + nt->OptionalHeader.AddressOfEntryPoint;
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

static LONG CALLBACK unpack_veh(EXCEPTION_POINTERS* ep) {
    if (!g_armed || ep->ExceptionRecord->ExceptionCode != EXCEPTION_SINGLE_STEP ||
        !(ep->ContextRecord->Dr6 & 1))
        return EXCEPTION_CONTINUE_SEARCH;

    uintptr_t address = (uintptr_t)ep->ExceptionRecord->ExceptionAddress;
    if (g_stage == 1 && address == g_entrypoint_va) {
        if (g_worker_ready) {
            set_dr0(ep->ContextRecord, g_ntprotect_va);
            InterlockedExchange(&g_stage, 2);
            dbg("[barrier] entrypoint rendezvous hit at %p; DR0 -> NtProtectVirtualMemory\n",
                (void*)address);
        } else {
            clear_dr0(ep->ContextRecord);
            InterlockedExchange(&g_armed, 0);
            dbg("[barrier] entrypoint rendezvous hit without a worker; DR0 cleared\n");
        }
        return EXCEPTION_CONTINUE_EXECUTION;
    }

    if (g_stage != 2 || address != g_ntprotect_va)
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
    if (g_barrier_event) SetEvent(g_barrier_event);

    DWORD result = g_barrier_done
        ? WaitForSingleObject(g_barrier_done, kBarrierTimeoutMs) : WAIT_FAILED;
    if (result != WAIT_OBJECT_0)
        dbg("[barrier] hook worker did not finish in %lu ms; resuming without hooks\n",
            kBarrierTimeoutMs);
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

static DWORD WINAPI barrier_worker(LPVOID) {
    DWORD result = g_barrier_event
        ? WaitForSingleObject(g_barrier_event, kBarrierTimeoutMs) : WAIT_FAILED;
    if (result != WAIT_OBJECT_0) {
        // Keep the VEH armed while removing DR0. If g_armed were cleared first,
        // another thread could hit the still-live breakpoint during this sweep and
        // have its EXCEPTION_SINGLE_STEP propagated as unhandled.
        clear_barrier_dr0_all_threads(GetCurrentThreadId());
        InterlockedExchange(&g_armed, 0);
        dbg("[barrier] no exact bulk-unpack transition in %lu ms; "
            "Talon DR0 cleared, no hooks installed\n", kBarrierTimeoutMs);
        return 0;
    }

    bool resolved = vfs_resolve_and_register();
    int installed = resolved ? hook_install_all() : 0;
    dbg("[barrier] bulk unpack complete; scanner resolved=%d, hooks installed=%d\n",
        resolved ? 1 : 0, installed);
    if (g_barrier_done) SetEvent(g_barrier_done);
    return 0;
}

void start_unpack_barrier() {
    uint8_t* base = (uint8_t*)GetModuleHandleA(nullptr);
    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    g_ntprotect_va = ntdll
        ? (uintptr_t)GetProcAddress(ntdll, "NtProtectVirtualMemory") : 0;
    if (!base || !g_ntprotect_va || !find_text_range(base)) {
        dbg("[barrier] could not resolve NtProtectVirtualMemory or .text; no hooks installed\n");
        return;
    }

    g_barrier_event = CreateEventA(nullptr, FALSE, FALSE, nullptr);
    g_barrier_done = CreateEventA(nullptr, FALSE, FALSE, nullptr);
    if (!g_barrier_event || !g_barrier_done) {
        dbg("[barrier] CreateEvent failed (err=%lu)\n", GetLastError());
        if (g_barrier_event) { CloseHandle(g_barrier_event); g_barrier_event = nullptr; }
        if (g_barrier_done) { CloseHandle(g_barrier_done); g_barrier_done = nullptr; }
        return;
    }

    PVOID veh_handle = AddVectoredExceptionHandler(1, unpack_veh);
    if (!veh_handle) {
        dbg("[barrier] AddVectoredExceptionHandler failed (err=%lu)\n", GetLastError());
        return;
    }
    InterlockedExchange(&g_stage, 1);
    InterlockedExchange(&g_armed, 1);

    HANDLE thread = CreateThread(nullptr, 0, barrier_worker, nullptr, 0, nullptr);
    if (!thread) {
        dbg("[barrier] CreateThread(worker) failed (err=%lu)\n", GetLastError());
        return;
    }
    CloseHandle(thread);
    InterlockedExchange(&g_worker_ready, 1);

    dbg("[barrier] awaiting injector entrypoint rendezvous=%p; "
        "NtProtectVirtualMemory=%p; .text=[%p,%p)\n",
        (void*)g_entrypoint_va, (void*)g_ntprotect_va,
        (void*)g_text_begin, (void*)g_text_end);
}
