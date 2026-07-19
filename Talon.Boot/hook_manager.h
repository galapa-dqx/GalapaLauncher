#pragma once
#include <stdint.h>

// ── Hook manager ─────────────────────────────────────────────────────────────
// A small MinHook-backed registry, shaped after Dalamud.Boot's native hook layer.
// It separates REGISTRATION from INSTALLATION, which is mandatory for a packed
// target: MinHook reads the target's prologue to build a trampoline, and that
// prologue does not exist while DQX's .text is still packed. So hook modules
// register descriptors at boot (target address is a fixed VA, valid even while the
// bytes are zero-filled), and the unpack barrier calls hook_install_all() once the
// code is present. This is exactly the shape the multi-hook OEP barrier will drive:
// N modules register, one barrier installs them all.
//
// Teardown is admitted from day one (plugins will load/unload): every detour must
// bracket its body with the in-flight guard so hook_remove() can drain safely while
// the game is still calling through the hook. Removal itself is delegated to MinHook
// (MH_RemoveHook restores the original bytes), so we do not hand-roll byte snapshots.

// Opaque handle. Callers keep the pointer a hook_register() hands back; a detour
// needs it only to open the in-flight guard.
struct TalonHook;

// Register a hook descriptor. Does NOT install. `name` is for logging; `target` is
// the absolute VA to hook (valid even while packed/zero-filled); `detour` is the
// replacement; `ppOriginal` receives the trampoline (the un-hooked callable) at
// install time, mirroring MinHook's out-param. One hook per target (MinHook's model)
// — a duplicate target returns nullptr. Returns nullptr if the table is full.
TalonHook* hook_register(const char* name, void* target, void* detour, void** ppOriginal);

// Create + enable every registered-but-not-installed hook via MinHook, in the CURRENT
// (normal) thread context — call this from the unpack barrier, never from a VEH.
// Returns the number of hooks newly installed. Idempotent per hook.
int hook_install_all();

// The trampoline (un-hooked callable) for a hook; null before install. A detour that
// stored its own `ppOriginal` slot uses that directly; this is for callers that didn't.
void* hook_original(TalonHook* h);

// ── in-flight guard (WaitGroup) ──────────────────────────────────────────────
// A detour MUST bracket its whole body with these so hook_remove() can drain in-flight
// calls before MinHook frees the trampoline. Prefer the RAII HookGuard.
void hook_enter(TalonHook* h);
void hook_leave(TalonHook* h);

struct HookGuard {
    TalonHook* h;
    explicit HookGuard(TalonHook* hook) : h(hook) { hook_enter(h); }
    ~HookGuard() { hook_leave(h); }
    HookGuard(const HookGuard&) = delete;
    HookGuard& operator=(const HookGuard&) = delete;
};

// Safe teardown: disable (MinHook restores the bytes, so no NEW detour entries), wait
// for in-flight detour calls to drain, then remove (frees the trampoline). Safe to call
// while the game runs — but never from inside the detour being removed (self-drain would
// deadlock). No-op on an unknown/already-removed hook.
void hook_remove(TalonHook* h);

// Disable all hooks and shut MinHook down. Call on unload.
void hook_shutdown();
