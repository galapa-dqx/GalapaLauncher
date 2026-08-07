// Talon.Boot — DQX loose-file override payload (VFS-semantic hook).
//
// Injected into the 32-bit DQX game process by Talon.Injector via an early-bird
// APC (see Talon.Injector/Injector.cs). Once the game has unpacked itself, this
// DLL inline-hooks the game's own resource loader, Vfs_LoadResource, so that a
// loose file living under an override directory is served in place of the asset
// the game would otherwise decompress out of its packed .dat archives.
//
// Why the VFS layer instead of dragonhook's file-I/O layer:
//   dragonhook hooks CreateFile/ReadFile and re-encodes loose files back into the
//   game's on-disk block format (IDX relocation + type-2 deflate). Hooking the
//   semantic loader instead means the game does its own archive I/O and we only
//   intercept the (path -> resource) call. No IDX parsing, no re-compression, no
//   knowledge of the .dat format, and no --data-dir: the game reads Content\Data
//   itself. The trade-off is that this binds to a game-internal address, so it is
//   version-specific (see vfs_hook.cpp's re-anchoring notes) whereas dragonhook is not.
//
// Source layout:
//   log.*            diagnostics to %TEMP%\\talon-boot.log + OutputDebugString
//   hook_manager.*   MinHook-backed hook registry
//   vfs_hook.*       VFS override and post-unpack signature scanner
//   unpack_trigger.* in-process bulk-unpack barrier
//   dllmain.cpp      boot orchestration and entrypoints

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include "log.h"
#include "vfs_hook.h"
#include "unpack_trigger.h"

static volatile LONG g_booted = 0;

// Reads configuration and starts the bulk-unpack barrier. Idempotent.
static void talon_boot() {
    if (InterlockedCompareExchange(&g_booted, 1, 0) != 0) return;

    open_log();

    SYSTEMTIME st;
    GetSystemTime(&st);
    dbg("[boot] Talon.Boot loaded (pid=%lu) at %04u-%02u-%02u %02u:%02u:%02u.%03uZ\n",
        GetCurrentProcessId(), st.wYear, st.wMonth, st.wDay,
        st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);


    char overrideDir[MAX_PATH];
    DWORD n = GetEnvironmentVariableA("TALON_OVERRIDE_DIR", overrideDir, sizeof(overrideDir));
    if (n == 0 || n >= sizeof(overrideDir)) {
        dbg("[boot] TALON_OVERRIDE_DIR not set — Talon.Boot is a no-op this launch\n");
        return;
    }
    vfs_set_override_dir(overrideDir);
    dbg("[boot] override dir = %s\n", overrideDir);

    char censusBuf[8];
    if (GetEnvironmentVariableA("TALON_VFS_CENSUS", censusBuf, sizeof(censusBuf)) > 0) {
        vfs_set_census(true);
        dbg("[boot] VFS census logging ENABLED\n");
    }

    // Boot observes the packer's final bulk .text protection transition itself.
    start_unpack_barrier();
}

// Explicit hand-off entrypoint for the entrypoint-rewrite / CLR bootstrap paths.
// talon_boot() is idempotent, so a later call after DllMain already ran is a no-op.
extern "C" __declspec(dllexport) void TalonInit() {
    talon_boot();
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID) {
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(inst);
    talon_boot();
    return TRUE;
}
