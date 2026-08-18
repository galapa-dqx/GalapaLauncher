#pragma once

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

enum talon_initialization_state : LONG {
    talon_initialization_pending = 0,
    talon_initialization_committed = 1,
    talon_initialization_cancelled = 2,
};

// Install Boot's entrypoint/NtProtect barrier. The VEH signals unpack_complete and
// parks the unpacking thread until managed_ready is signaled (or startup is cancelled).
bool start_unpack_barrier(
    HANDLE unpack_complete,
    HANDLE managed_ready,
    HANDLE initialization_cancelled,
    volatile LONG* initialization_state);

// Disarm Talon's DR0 rendezvous if initialization times out before unpack completes.
void cancel_unpack_barrier();
