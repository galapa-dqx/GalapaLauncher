#include "coreclr_host.h"
#include "log.h"

#include <stdint.h>
#include <string>
#include <vector>

// This file implements the standard .NET native-hosting sequence without taking
// a dependency on the SDK headers. These declarations mirror nethost/hostfxr.
using char_t = wchar_t;

struct get_hostfxr_parameters {
    size_t size;
    const char_t* assembly_path;
    const char_t* dotnet_root;
};

using get_hostfxr_path_fn =
    int32_t(__cdecl*)(char_t*, size_t*, const get_hostfxr_parameters*);
using hostfxr_handle = void*;
using hostfxr_initialize_for_runtime_config_fn =
    int32_t(__cdecl*)(const char_t*, const void*, hostfxr_handle*);
using hostfxr_get_runtime_delegate_fn =
    int32_t(__cdecl*)(hostfxr_handle, int32_t, void**);
using hostfxr_close_fn = int32_t(__cdecl*)(hostfxr_handle);
using load_assembly_and_get_function_pointer_fn =
    int32_t(__stdcall*)(const char_t*, const char_t*, const char_t*,
                        const char_t*, void*, void**);

static constexpr int32_t hdt_load_assembly_and_get_function_pointer = 3;

static std::wstring module_directory(HMODULE module) {
    std::vector<wchar_t> path(32768);
    DWORD length = GetModuleFileNameW(module, path.data(), (DWORD)path.size());
    if (!length || length == path.size()) return {};
    std::wstring result(path.data(), length);
    size_t slash = result.find_last_of(L"\\/");
    return slash == std::wstring::npos ? std::wstring() : result.substr(0, slash);
}

static std::wstring join(const std::wstring& directory, const wchar_t* name) {
    return directory + L"\\" + name;
}

static HMODULE load_hostfxr(
    const std::wstring& directory,
    const std::wstring& assembly_path) {
    // Talon is self-contained, so prefer the x86 hostfxr shipped beside Boot.
    std::wstring local_path = join(directory, L"hostfxr.dll");
    HMODULE local = LoadLibraryW(local_path.c_str());
    if (local) {
        dbg("[coreclr] using co-located hostfxr: %ls\n", local_path.c_str());
        return local;
    }

    // nethost is a fallback for development layouts that do not copy hostfxr.
    dbg("[coreclr] co-located hostfxr unavailable (err=%lu); trying nethost fallback\n",
        GetLastError());
    std::wstring nethost_path = join(directory, L"nethost.dll");
    HMODULE nethost = LoadLibraryW(nethost_path.c_str());
    if (!nethost) return nullptr;
    auto get_hostfxr_path = reinterpret_cast<get_hostfxr_path_fn>(
        GetProcAddress(nethost, "get_hostfxr_path"));
    if (!get_hostfxr_path) return nullptr;

    get_hostfxr_parameters parameters = {};
    parameters.size = sizeof(parameters);
    parameters.assembly_path = assembly_path.c_str();
    parameters.dotnet_root = nullptr;

    size_t path_size = 0;
    get_hostfxr_path(nullptr, &path_size, &parameters);
    if (!path_size) return nullptr;
    std::vector<wchar_t> path(path_size);
    if (get_hostfxr_path(path.data(), &path_size, &parameters) != 0) return nullptr;
    return LoadLibraryW(path.data());
}

bool load_managed_entry(HMODULE boot_module, talon_managed_init_fn* entry) {
    if (!entry) return false;
    *entry = nullptr;

    std::wstring directory = module_directory(boot_module);
    if (directory.empty()) {
        dbg("[coreclr] failed to determine Boot module directory\n");
        return false;
    }

    std::wstring assembly_path = join(directory, L"Talon.dll");
    std::wstring runtime_config = join(directory, L"Talon.runtimeconfig.json");

    // hostfxr reads the runtime config and selects the matching CoreCLR runtime.
    HMODULE hostfxr = load_hostfxr(directory, assembly_path);
    if (!hostfxr) {
        dbg("[coreclr] LoadLibraryW(hostfxr) failed (err=%lu)\n", GetLastError());
        return false;
    }

    // Resolve only the hostfxr API needed for component hosting.
    auto initialize = reinterpret_cast<hostfxr_initialize_for_runtime_config_fn>(
        GetProcAddress(hostfxr, "hostfxr_initialize_for_runtime_config"));
    auto get_delegate = reinterpret_cast<hostfxr_get_runtime_delegate_fn>(
        GetProcAddress(hostfxr, "hostfxr_get_runtime_delegate"));
    auto close = reinterpret_cast<hostfxr_close_fn>(
        GetProcAddress(hostfxr, "hostfxr_close"));
    if (!initialize || !get_delegate || !close) {
        dbg("[coreclr] required hostfxr exports are missing\n");
        return false;
    }

    hostfxr_handle context = nullptr;
    int32_t rc = initialize(runtime_config.c_str(), nullptr, &context);
    if (rc != 0 || !context) {
        dbg("[coreclr] runtime initialization failed (0x%08lX)\n", (DWORD)rc);
        return false;
    }

    // Ask hostfxr for the delegate that loads a managed component by path.
    void* load_assembly_raw = nullptr;
    rc = get_delegate(context, hdt_load_assembly_and_get_function_pointer,
                      &load_assembly_raw);
    close(context);
    if (rc != 0 || !load_assembly_raw) {
        dbg("[coreclr] load-assembly delegate failed (0x%08lX)\n", (DWORD)rc);
        return false;
    }

    // Resolve the stdcall callback that Boot invokes after the game unpacks.
    auto load_assembly = reinterpret_cast<load_assembly_and_get_function_pointer_fn>(
        load_assembly_raw);
    void* managed_entry = nullptr;
    rc = load_assembly(
        assembly_path.c_str(),
        L"Talon.EntryPoint, Talon",
        L"Initialize",
        L"Talon.EntryPoint+InitDelegate, Talon",
        nullptr,
        &managed_entry);
    if (rc != 0 || !managed_entry) {
        dbg("[coreclr] Talon.EntryPoint.Initialize resolution failed (0x%08lX)\n",
            (DWORD)rc);
        return false;
    }

    *entry = reinterpret_cast<talon_managed_init_fn>(managed_entry);
    dbg("[coreclr] managed entry resolved from %ls\n", assembly_path.c_str());
    return true;
}
