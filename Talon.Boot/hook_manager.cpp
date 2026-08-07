// ── Hook manager ─────────────────────────────────────────────────────────────
// See hook_manager.h for the design rationale (register-then-install for packed
// targets; WaitGroup teardown; MinHook owns the byte-level patching).

#include "hook_manager.h"
#include "log.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>

#include "MinHook.h"

struct TalonHook {
    const char*   name;
    void*         target;
    void*         detour;
    void**        ppOriginal;   // caller's trampoline slot, filled by MinHook at create
    volatile LONG inflight;     // WaitGroup: detour calls currently executing
    bool          created;      // MH_CreateHook succeeded
    bool          enabled;      // MH_EnableHook succeeded
    bool          removed;      // torn down; skip on install
};

// Fixed table — no heap/STL, keeps the payload dependency-free. 32 is far more than
// the handful of hooks Talon will carry (VFS, network, D3D); raise if ever needed.
static const int        kMaxHooks   = 32;
static TalonHook        g_hooks[kMaxHooks];
static int              g_count     = 0;
static CRITICAL_SECTION g_cs;
static volatile LONG    g_cs_ready  = 0;   // g_cs initialized
static volatile LONG    g_mh_ready  = 0;   // MH_Initialize done

static void ensure_cs() {
    // Double-checked one-time init. Registration happens single-threaded at boot, but
    // enable/remove can race later, so the critical section must exist before any use.
    if (InterlockedCompareExchange(&g_cs_ready, 1, 0) == 0)
        InitializeCriticalSection(&g_cs);
}

static bool ensure_mh() {
    if (g_mh_ready) return true;
    MH_STATUS s = MH_Initialize();
    if (s == MH_OK || s == MH_ERROR_ALREADY_INITIALIZED) { g_mh_ready = 1; return true; }
    dbg("[hookmgr] MH_Initialize failed: %s\n", MH_StatusToString(s));
    return false;
}

TalonHook* hook_register(const char* name, void* target, void* detour, void** ppOriginal) {
    ensure_cs();
    EnterCriticalSection(&g_cs);

    TalonHook* h = nullptr;
    bool dup = false;
    for (int i = 0; i < g_count; i++) {
        if (g_hooks[i].target == target) { dup = true; break; }
    }
    if (dup) {
        dbg("[hookmgr] register: target %p already registered — refusing '%s'\n", target, name);
    } else if (g_count >= kMaxHooks) {
        dbg("[hookmgr] register: table full (%d) — dropping '%s'\n", kMaxHooks, name);
    } else {
        h = &g_hooks[g_count++];
        h->name = name; h->target = target; h->detour = detour; h->ppOriginal = ppOriginal;
        h->inflight = 0; h->created = false; h->enabled = false; h->removed = false;
        dbg("[hookmgr] registered '%s' target=%p (pending install)\n", name, target);
    }

    LeaveCriticalSection(&g_cs);
    return h;
}

int hook_install_all() {
    ensure_cs();
    EnterCriticalSection(&g_cs);

    int installed = 0;
    if (ensure_mh()) {
        for (int i = 0; i < g_count; i++) {
            TalonHook* h = &g_hooks[i];
            if (h->created || h->removed) continue;

            MH_STATUS s = MH_CreateHook(h->target, h->detour, h->ppOriginal);
            if (s != MH_OK) {
                dbg("[hookmgr] '%s' MH_CreateHook @%p failed: %s\n", h->name, h->target, MH_StatusToString(s));
                continue;
            }
            h->created = true;

            s = MH_EnableHook(h->target);
            if (s != MH_OK) {
                dbg("[hookmgr] '%s' MH_EnableHook @%p failed: %s\n", h->name, h->target, MH_StatusToString(s));
                continue;
            }
            h->enabled = true;
            installed++;
            dbg("[hookmgr] installed '%s' @%p via MinHook, trampoline=%p\n",
                h->name, h->target, h->ppOriginal ? *h->ppOriginal : nullptr);
        }
    }

    LeaveCriticalSection(&g_cs);
    return installed;
}


void hook_enter(TalonHook* h) { if (h) InterlockedIncrement(&h->inflight); }
void hook_leave(TalonHook* h) { if (h) InterlockedDecrement(&h->inflight); }

void hook_remove(TalonHook* h) {
    if (!h) return;
    ensure_cs();
    EnterCriticalSection(&g_cs);

    if (h->enabled) {
        // MinHook disables with all other threads frozen and restores the original
        // bytes, so once this returns no thread can ENTER the detour anymore.
        MH_STATUS s = MH_DisableHook(h->target);
        if (s == MH_OK) h->enabled = false;
        else dbg("[hookmgr] '%s' MH_DisableHook failed: %s\n", h->name, MH_StatusToString(s));
    }

    // Drain calls already inside the detour before MinHook frees the trampoline they
    // may still be executing in. Bounded so a wedged thread can't hang teardown forever.
    for (int spins = 0; h->inflight > 0 && spins < 100000; spins++) Sleep(0);
    if (h->inflight > 0) {
        // The target bytes are already restored, so no new calls can enter. Keep
        // the disabled hook (and its trampoline) alive for the slow calls that are
        // still using it; a later hook_remove call can finish the removal.
        dbg("[hookmgr] '%s' removal deferred with %ld call(s) still in flight\n",
            h->name, h->inflight);
        LeaveCriticalSection(&g_cs);
        return;
    }

    if (h->created) {
        MH_RemoveHook(h->target);
        h->created = false;
    }
    h->removed = true;
    dbg("[hookmgr] removed '%s' @%p\n", h->name, h->target);

    LeaveCriticalSection(&g_cs);
}

void hook_shutdown() {
    if (!g_cs_ready) return;
    EnterCriticalSection(&g_cs);
    if (g_mh_ready) {
        MH_DisableHook(MH_ALL_HOOKS);
        MH_Uninitialize();
        g_mh_ready = 0;
    }
    LeaveCriticalSection(&g_cs);
}
