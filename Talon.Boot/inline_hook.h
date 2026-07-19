#pragma once

// ── Inline hooking ──────────────────────────────────────────────────────────
// Patch the first bytes of `target` with a jmp to `replacement`; return a
// trampoline that runs the stolen bytes then jumps back, so the caller can still
// invoke the original. Returns nullptr on failure (couldn't find a 5-byte
// instruction boundary, or the trampoline allocation failed).
void* inline_hook(void* target, void* replacement);
