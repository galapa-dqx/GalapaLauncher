#include "inline_hook.h"
#include "disasm.h"
#include "log.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string.h>
#include <stdint.h>

// Patch the first bytes of `target` with a jmp to `replacement`; return a
// trampoline that runs the stolen bytes then jumps back, so the caller can still
// invoke the original. Vfs_LoadResource is a normal function (not an export
// thunk) so the E9/FF25 follow below is a no-op for it, but it is harmless and
// kept for generality.
void* inline_hook(void* target, void* replacement) {
    uint8_t* fn = (uint8_t*)target;

    int hops = 0;
    while (hops < 8) {
        if (fn[0] == 0xE9) {
            int32_t rel;
            memcpy(&rel, fn + 1, 4);
            fn = fn + 5 + rel;
        } else if (fn[0] == 0xFF && fn[1] == 0x25) {
            uint32_t mem_addr;
            memcpy(&mem_addr, fn + 2, 4);
            uint32_t dest;
            memcpy(&dest, (void*)(uintptr_t)mem_addr, 4);
            fn = (uint8_t*)(uintptr_t)dest;
        } else {
            break;
        }
        hops++;
    }

    dbg("[hook] target=%p bytes: %02X %02X %02X %02X %02X %02X %02X %02X (after %d hop(s))\n",
        fn, fn[0], fn[1], fn[2], fn[3], fn[4], fn[5], fn[6], fn[7], hops);

    int stolen = 0;
    while (stolen < 5) {
        int ilen = insn_len32(fn + stolen);
        if (ilen < 1) ilen = 1;
        stolen += ilen;
        if (stolen > 20) { dbg("[hook] ERROR: couldn't find 5-byte boundary\n"); return nullptr; }
    }
    dbg("[hook] stolen=%d bytes\n", stolen);

    uint8_t* tramp = (uint8_t*)VirtualAlloc(nullptr, 32,
        MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!tramp) { dbg("[hook] VirtualAlloc failed\n"); return nullptr; }

    DWORD old;
    VirtualProtect(fn, stolen, PAGE_EXECUTE_READWRITE, &old);

    memcpy(tramp, fn, stolen);
    for (int i = 0; i < stolen; ) {
        int ilen = insn_len32(tramp + i);
        if ((tramp[i] == 0xE9 || tramp[i] == 0xE8) && ilen == 5) {
            int32_t orig_rel;
            memcpy(&orig_rel, tramp + i + 1, 4);
            uintptr_t abs = (uintptr_t)(fn + i + 5) + (uintptr_t)(intptr_t)orig_rel;
            int32_t new_rel = (int32_t)((intptr_t)abs - (intptr_t)(tramp + i + 5));
            memcpy(tramp + i + 1, &new_rel, 4);
        }
        i += ilen;
    }

    tramp[stolen] = 0xE9;
    int32_t back = (int32_t)((intptr_t)(fn + stolen) - (intptr_t)(tramp + stolen + 5));
    memcpy(tramp + stolen + 1, &back, 4);

    fn[0] = 0xE9;
    int32_t fwd = (int32_t)((intptr_t)(uint8_t*)replacement - (intptr_t)(fn + 5));
    memcpy(fn + 1, &fwd, 4);

    VirtualProtect(fn, stolen, old, &old);
    FlushInstructionCache(GetCurrentProcess(), fn, stolen);
    return tramp;
}
