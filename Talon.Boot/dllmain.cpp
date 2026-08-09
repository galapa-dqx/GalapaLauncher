#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include "coreclr_host.h"
#include "log.h"
#include "unpack_trigger.h"

static constexpr DWORD kInitializationTimeoutMs = 30000;

static HMODULE g_boot_module = nullptr;
static HANDLE g_unpack_complete = nullptr;
static HANDLE g_managed_ready = nullptr;
static volatile LONG g_started = 0;
static volatile LONG g_failure_notified = 0;

struct bootstrap_state {
    const char* start_info_json;
};

static DWORD WINAPI failure_message_worker(LPVOID parameter) {
    const wchar_t* message = static_cast<const wchar_t*>(parameter);
    MessageBoxW(nullptr, message, L"Talon hook failure",
                MB_OK | MB_ICONWARNING | MB_SETFOREGROUND);
    return 0;
}

// Never show a modal dialog on the loader, unpacker, or VCE threads. One worker
// reports the first bootstrap failure while DQX continues without Talon hooks.
static void notify_failure(const wchar_t* message) {
    if (InterlockedCompareExchange(&g_failure_notified, 1, 0) != 0) return;
    HANDLE thread = CreateThread(nullptr, 0, failure_message_worker,
                                 const_cast<wchar_t*>(message), 0, nullptr);
    if (thread) CloseHandle(thread);
}

static DWORD WINAPI managed_worker(LPVOID parameter) {
    bootstrap_state* state = static_cast<bootstrap_state*>(parameter);
    talon_managed_init_fn entry = nullptr;

    if (!load_managed_entry(g_boot_module, &entry)) {
        dbg("[boot] managed runtime bootstrap failed; releasing game thread\n");
        notify_failure(L"Talon could not start its managed runtime. DQX will continue without Talon hooks. See talon-boot.log for details.");
        cancel_unpack_barrier();
        SetEvent(g_managed_ready);
        delete state;
        return 0;
    }

    DWORD result = WaitForSingleObject(g_unpack_complete, kInitializationTimeoutMs);
    if (result != WAIT_OBJECT_0) {
        dbg("[boot] unpack barrier timed out; managed hooks were not installed\n");
        notify_failure(L"Talon did not observe DQX unpacking in time. DQX will continue without Talon hooks. See talon-boot.log for details.");
        cancel_unpack_barrier();
        SetEvent(g_managed_ready);
        delete state;
        return 0;
    }

    __try {
        entry((void*)state->start_info_json, g_managed_ready);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        dbg("[boot] managed entry raised SEH 0x%08lX; releasing game thread\n",
            GetExceptionCode());
        notify_failure(L"Talon's managed entry point failed. DQX will continue without Talon hooks. See talon-boot.log for details.");
        SetEvent(g_managed_ready);
    }

    delete state;
    return 0;
}

// Called by the injector's target-side APC thunk after LoadLibraryW. This remains
// a tiny native handoff: C++ owns only CLR startup and the unpack-complete barrier.
extern "C" __declspec(dllexport) DWORD __cdecl TalonInitialize(
    const char* start_info_json) {
    if (InterlockedCompareExchange(&g_started, 1, 0) != 0) return ERROR_ALREADY_EXISTS;

    open_log();
    dbg("[boot] Talon.Boot loaded (pid=%lu)\n", GetCurrentProcessId());

    g_unpack_complete = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    g_managed_ready = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!g_unpack_complete || !g_managed_ready) {
        DWORD error = GetLastError();
        dbg("[boot] CreateEvent failed (err=%lu)\n", error);
        notify_failure(L"Talon could not create its startup events. DQX will continue without Talon hooks. See talon-boot.log for details.");
        if (g_managed_ready) SetEvent(g_managed_ready);
        return error;
    }

    if (!start_unpack_barrier(g_unpack_complete, g_managed_ready)) {
        dbg("[boot] unpack barrier initialization failed\n");
        notify_failure(L"Talon could not arm its unpack barrier. DQX will continue without Talon hooks. See talon-boot.log for details.");
        SetEvent(g_managed_ready);
        return ERROR_INVALID_STATE;
    }

    auto state = new bootstrap_state{start_info_json};
    HANDLE thread = CreateThread(nullptr, 0, managed_worker, state, 0, nullptr);
    if (!thread) {
        DWORD error = GetLastError();
        dbg("[boot] managed worker creation failed (err=%lu)\n", error);
        notify_failure(L"Talon could not start its managed worker. DQX will continue without Talon hooks. See talon-boot.log for details.");
        delete state;
        cancel_unpack_barrier();
        SetEvent(g_managed_ready);
        return error;
    }

    CloseHandle(thread);
    return ERROR_SUCCESS;
}

BOOL WINAPI DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_boot_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}
