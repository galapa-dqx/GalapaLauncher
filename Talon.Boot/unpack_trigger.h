#pragma once

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

// Install Boot's entrypoint/NtProtect barrier. The VEH signals unpack_complete and
// parks the unpacking thread until managed_ready is signaled (or 30 seconds elapse).
bool start_unpack_barrier(HANDLE unpack_complete, HANDLE managed_ready);

// Disarm Talon's DR0 rendezvous if initialization times out before unpack completes.
void cancel_unpack_barrier();
