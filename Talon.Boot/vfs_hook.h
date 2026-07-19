#pragma once
#include <stddef.h>
#include <stdint.h>

// ── Vfs_LoadResource hook ─────────────────────────────────────────────────────
// The DQX resource loader we inline-hook so a loose file under an override
// directory is served in place of the asset the game would decompress out of its
// packed .dat archives. See vfs_hook.cpp for the ABI and buffer-ownership notes.

// Configuration, set by boot before the watcher arms.
//   override dir: empty => the hook passes everything through to the original.
//   census: opt-in logging of every requested resource path (TALON_VFS_CENSUS).
void vfs_set_override_dir(const char* dir);
void vfs_set_census(bool enabled);

// Register the Vfs_LoadResource override hook with the hook manager. Does not install —
// the unpack barrier calls hook_install_all() once .text is unpacked (MinHook needs the
// prologue present to build the trampoline).
void vfs_register();

// ── Locating Vfs_LoadResource at runtime ─────────────────────────────────────
// Runtime VA = <exe load base> + VFS_LOADRESOURCE_RVA. The prologue signature
// confirms the expected address (and could seed a scan if the game is patched).
// Patch-day re-anchor notes live in vfs_hook.cpp.
extern const uint32_t VFS_LOADRESOURCE_RVA;
extern const int      VFS_SIG_LEN;

// True if the bytes at p match the Vfs_LoadResource prologue signature.
bool sig_matches(const uint8_t* p);

// True if [addr, addr+len) is committed, readable and executable — safe to read
// while the packer may still be mapping the image.
bool region_readable(const uint8_t* addr, size_t len);
