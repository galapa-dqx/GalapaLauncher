#pragma once

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

using talon_managed_init_fn = void(__stdcall*)(void* start_info_json, void* main_thread_continue_event);

// Initializes the co-located self-contained Talon application and resolves
// Talon.EntryPoint.Initialize as a managed function pointer.
bool load_managed_entry(HMODULE boot_module, talon_managed_init_fn* entry);
