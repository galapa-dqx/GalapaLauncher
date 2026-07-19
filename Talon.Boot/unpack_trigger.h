#pragma once

// ── Unpack trigger ─────────────────────────────────────────────────────────────
// DQX's .text is packed, so Vfs_LoadResource is not present at its address until
// the packer has run. Rather than poll, we arm a hardware execute breakpoint at
// the function's address and install the inline hook when the game first CALLS it
// (a call can't happen until the function is fully unpacked). See the .cpp for the
// full rationale — including why there is no poll/scan fallback.
//
// Spawns the watcher thread off the loader lock and returns immediately.
void start_unpack_watcher();
