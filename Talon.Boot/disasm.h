#pragma once
#include <stdint.h>

// ── Minimal x86-32 instruction length decoder ───────────────────────────────
// Used by inline_hook to steal whole instructions for the trampoline. Handles
// the opcode shapes found in the Vfs_LoadResource prologue (push/mov/sub/and).
// Returns the byte length of the instruction at p, or 1 on an unknown opcode
// (safe fallback — the caller accumulates until it has >= 5 bytes).
int insn_len32(const uint8_t* p);
